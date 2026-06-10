// MainThreadDispatcher.cpp — Phase 20-MT.
//
// Implements the cross-backend main-thread marshaling queue. See the
// header for the public-surface comments. The implementation choices:
//
//   * Queue is a plain std::deque under one mutex. We expect at most
//     a handful of in-flight items per frame, so the lock contention
//     is negligible. A lock-free MPSC would be more cool but isn't
//     needed at this scale and adds platform complexity.
//
//   * Per-item completion uses a Win32 event handle (auto-reset).
//     The worker thread blocks on WaitForSingleObject with timeout;
//     the main thread signals via SetEvent after the closure runs.
//     Auto-reset means the event is consumed atomically.
//
//   * Main thread detection: enumerate process threads with Toolhelp32
//     and pick the oldest creation time. The OS process main thread is
//     always created first, and for any Unity build that's the same
//     thread the engine uses for the player loop. We avoid Unity-version
//     fragility by not relying on Mono / IL2CPP exports for this.
//
//   * Recursion guard is a thread_local flag. NotifyMainThreadTick is
//     called from the runtime_invoke detour hook; when we drain a work
//     item, the closure typically calls runtime_invoke itself (that's
//     the whole point), which re-enters the hook. The guard short-
//     circuits the inner Drain() so we don't recurse.

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include "MainThreadDispatcher.h"

#include "Logger.h"
#include "MagicStrings.h"

#include <windows.h>
#include <tlhelp32.h>

#include <atomic>
#include <chrono>
#include <cstdio>
#include <deque>
#include <memory>
#include <mutex>
#include <string>

namespace Discrete::MainThreadDispatcher {

// Local printf-style log helpers. We route everything into the "init"
// category since main-thread plumbing landings + diagnostics belong
// alongside the resolver / handshake log lines a user would consult.
namespace {
template <typename... Args>
void LogF(const char* fmt, Args... args) {
    char buf[512];
    std::snprintf(buf, sizeof(buf), fmt, args...);
    Discrete::Log::Write(Discrete::MagicStrings::LogCategoryInit, buf);
}
}  // namespace


namespace {

// One entry on the work queue. The dispatcher owns the WorkFn and the
// completion event. Worker thread waits on done_event; main thread runs
// work, then sets done_event.
struct WorkItem {
    WorkFn       fn;
    HANDLE       done_event = nullptr;  // auto-reset, signaled when work completes
    std::atomic<bool> consumed;     // true once main thread has executed fn
    std::atomic<bool> aborted;      // true if SubmitAndWait gave up before drain

    // The WorkItem OWNS the completion event: it's closed here, when the last
    // shared_ptr (submitter or queue) is released — NOT in SubmitAndWait. This
    // closes the timeout race: Drain holds a reference for the whole
    // fn()+SetEvent, so SetEvent can never land on a closed (possibly
    // kernel-reused) handle even if the submitter timed out and returned
    // mid-drain. Last reference wins; closed exactly once.
    ~WorkItem() {
        if (done_event) ::CloseHandle(done_event);
    }
};

// Global state. All access goes through g_mutex except for the cheap
// atomic counters which are inspected in tight paths (NotifyMainThreadTick).
std::mutex                         g_mutex;
std::deque<std::shared_ptr<WorkItem>> g_queue;

std::atomic<bool>                  g_initialized{false};
std::atomic<std::uint32_t>         g_main_thread_id{0};
std::atomic<std::uint64_t>         g_tick_count{0};
std::atomic<std::uint64_t>         g_submit_count{0};
std::atomic<std::uint64_t>         g_drain_count{0};
std::atomic<std::uint64_t>         g_timeout_count{0};
std::atomic<std::uint64_t>         g_reject_count{0};

// Wall-clock timestamp (steady_clock ticks) of the last
// NotifyMainThreadTick call. Read by IsHealthy() + Stats.
std::atomic<std::int64_t>          g_last_tick_ms{0};

// thread_local recursion guard for Drain(). Hook -> Drain -> work fn ->
// runtime_invoke -> hook -> (guard set, skip Drain). Per-thread storage
// is naturally correct because only the main thread can possibly recurse;
// worker threads never enter Drain.
thread_local bool                  t_drain_in_progress = false;

// thread_local "we're inside Drain on main thread, running a work item"
// flag. Distinct from t_drain_in_progress because that's set across the
// whole Drain pass (including queue manipulation outside any item's
// closure); this one is set ONLY while a work item's closure is on the
// stack. Read by ThreadAttachGuard to skip mono_thread_attach /
// mono_thread_detach when running on the already-attached Unity main
// thread — nested attach kills Unity's runtime handle (build #54).
thread_local bool                  t_inside_work_item = false;

// Returns now() in milliseconds since steady_clock epoch.
std::int64_t NowMs() {
    using namespace std::chrono;
    return duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count();
}

// Finds the process main thread = the oldest-created thread in this
// process. Returns 0 if enumeration fails or no threads found.
std::uint32_t DetectMainThreadId() {
    const DWORD pid = ::GetCurrentProcessId();
    HANDLE snap = ::CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) {
        LogF("[MainThreadDispatcher] CreateToolhelp32Snapshot failed: GLE=%lu", ::GetLastError());
        return 0;
    }

    THREADENTRY32 te{};
    te.dwSize = sizeof(te);

    DWORD     best_tid = 0;
    FILETIME  best_create{};
    best_create.dwLowDateTime  = 0xFFFFFFFFu;
    best_create.dwHighDateTime = 0xFFFFFFFFu;

    if (::Thread32First(snap, &te)) {
        do {
            if (te.th32OwnerProcessID != pid) continue;
            HANDLE hThread = ::OpenThread(THREAD_QUERY_LIMITED_INFORMATION,
                                          FALSE, te.th32ThreadID);
            if (!hThread) continue;

            FILETIME create{}, exit{}, kernel{}, user{};
            if (::GetThreadTimes(hThread, &create, &exit, &kernel, &user)) {
                if (::CompareFileTime(&create, &best_create) < 0) {
                    best_create = create;
                    best_tid    = te.th32ThreadID;
                }
            }
            ::CloseHandle(hThread);
        } while (::Thread32Next(snap, &te));
    }

    ::CloseHandle(snap);
    return best_tid;
}

}  // namespace

bool Init() {
    bool expected = false;
    if (!g_initialized.compare_exchange_strong(expected, true)) {
        return true;  // already initialized — no-op
    }

    const std::uint32_t tid = DetectMainThreadId();
    if (tid == 0) {
        g_initialized.store(false);
        LogF("[MainThreadDispatcher] Init: main thread detection failed");
        return false;
    }
    g_main_thread_id.store(tid);
    LogF("[MainThreadDispatcher] Init: main thread id = %lu", tid);
    return true;
}

void Shutdown() {
    g_initialized.store(false);

    // Drain queue with aborted=true so any worker thread still waiting
    // wakes up + sees the abort.
    std::lock_guard<std::mutex> lock(g_mutex);
    for (auto& item : g_queue) {
        item->aborted.store(true);
        ::SetEvent(item->done_event);
    }
    g_queue.clear();
}

bool IsInitialized() {
    return g_initialized.load();
}

std::uint32_t GetMainThreadId() {
    return g_main_thread_id.load();
}

void NotifyMainThreadTick() {
    g_tick_count.fetch_add(1);
    g_last_tick_ms.store(NowMs());
    Drain();
}

void Drain() {
    if (!g_initialized.load()) return;
    if (t_drain_in_progress) return;  // recursion guard — see file-top comment

    // The hook installer is supposed to only call us on main thread, but
    // double-check here so unit tests / other callers can't accidentally
    // run work fns on the wrong thread.
    if (::GetCurrentThreadId() != g_main_thread_id.load()) {
        return;
    }

    t_drain_in_progress = true;
    for (;;) {
        std::shared_ptr<WorkItem> item;
        {
            std::lock_guard<std::mutex> lock(g_mutex);
            if (g_queue.empty()) break;
            item = g_queue.front();
            g_queue.pop_front();
        }
        if (item->aborted.load()) {
            // Worker thread already gave up; skip the closure entirely.
            continue;
        }
        // Run the closure outside the mutex so nested SubmitAndWait
        // (rare but possible from re-entrant Unity callbacks) can still
        // enqueue without deadlock. Set the inside-work-item flag so
        // backend ThreadAttachGuards know to skip the attach/detach
        // pair (Unity main thread is already attached; nested attach
        // tears down its handle — see build #54 forensic).
        t_inside_work_item = true;
        try {
            item->fn();
        } catch (...) {
            LogF("[MainThreadDispatcher] Drain: work closure threw — swallowed");
        }
        t_inside_work_item = false;
        item->consumed.store(true);
        ::SetEvent(item->done_event);
        g_drain_count.fetch_add(1);
    }
    t_drain_in_progress = false;
}

DispatchResult SubmitAndWait(WorkFn work, std::int32_t timeout_ms) {
    DispatchResult dr{};

    if (!g_initialized.load()) {
        dr.error           = "dispatcher not initialized";
        dr.diagnostic_path = "uninitialized";
        return dr;
    }
    if (!work) {
        dr.error           = "work closure is empty";
        dr.diagnostic_path = "empty_work";
        return dr;
    }

    // Optimization: if the caller IS the main thread (rare — only happens
    // if Unity somehow calls into us on main thread, e.g. via a managed
    // callback that hits a pipe RPC), run inline. Avoids a deadlock where
    // the main thread waits on an event that only the main thread can set.
    const std::uint32_t main_tid = g_main_thread_id.load();
    if (::GetCurrentThreadId() == main_tid) {
        try {
            work();
            dr.ok              = true;
            dr.diagnostic_path = "inline_main_thread";
            g_drain_count.fetch_add(1);
            return dr;
        } catch (...) {
            dr.error           = "work closure threw on inline main-thread path";
            dr.diagnostic_path = "inline_threw";
            return dr;
        }
    }

    g_submit_count.fetch_add(1);

    // Queue full → reject fast. Sustained queue growth signals a frozen
    // main thread; better to error than block all worker threads.
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        if (g_queue.size() >= kMaxQueuedItems) {
            g_reject_count.fetch_add(1);
            dr.rejected        = true;
            dr.error           = "main-thread queue full (sustained burst or frozen game?)";
            dr.diagnostic_path = "queue_full";
            return dr;
        }
    }

    // Build work item. The WorkItem destructor owns done_event (see struct),
    // so the event is closed when the last shared_ptr is released — never
    // leaked on early-return, never closed out from under Drain's SetEvent.
    auto item = std::make_shared<WorkItem>();
    item->fn         = std::move(work);
    item->done_event = ::CreateEventW(nullptr, FALSE /*auto-reset*/, FALSE, nullptr);
    if (!item->done_event) {
        dr.error           = "CreateEventW failed";
        dr.diagnostic_path = "event_create_failed";
        return dr;
    }
    item->consumed.store(false);
    item->aborted.store(false);

    {
        std::lock_guard<std::mutex> lock(g_mutex);
        g_queue.push_back(item);
    }

    // Block until main thread drains, or timeout fires.
    const DWORD wait = (timeout_ms < 0) ? INFINITE : static_cast<DWORD>(timeout_ms);
    const DWORD wr   = ::WaitForSingleObject(item->done_event, wait);
    // Do NOT close done_event here. The queue may still hold a reference (we
    // timed out, or Drain is mid-flight on this item); closing it now would
    // let Drain's SetEvent fire on a dead/reused handle. The WorkItem
    // destructor closes it when the last shared_ptr drops — see the struct.

    if (wr == WAIT_OBJECT_0 && item->consumed.load()) {
        dr.ok              = true;
        dr.diagnostic_path = "drained";
        return dr;
    }

    // Timeout: mark item as aborted so Drain skips it when it eventually
    // reaches the front of the queue. We don't yank the entry — that
    // would need to scan the deque under lock and is a bigger surgery
    // for marginal benefit. The Drain path checks aborted before invoking.
    item->aborted.store(true);
    g_timeout_count.fetch_add(1);
    dr.timed_out       = true;
    dr.error           = "main-thread queue drain timeout";
    dr.diagnostic_path = "timeout";
    return dr;
}

bool IsCurrentlyDrainingOnMainThread() {
    return t_inside_work_item;
}

bool IsHealthy() {
    if (!g_initialized.load()) return false;
    const std::int64_t last = g_last_tick_ms.load();
    if (last == 0) return false;  // never observed a tick yet
    return (NowMs() - last) <= kHealthyWindowMs;
}

Stats GetStats() {
    Stats s{};
    s.initialized      = g_initialized.load();
    s.main_thread_id   = g_main_thread_id.load();
    s.tick_count       = g_tick_count.load();
    s.submit_count     = g_submit_count.load();
    s.drain_count      = g_drain_count.load();
    s.timeout_count    = g_timeout_count.load();
    s.reject_count     = g_reject_count.load();
    const std::int64_t last = g_last_tick_ms.load();
    s.last_tick_age_ms = (last == 0) ? -1 : (NowMs() - last);
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        s.queue_depth = g_queue.size();
    }
    return s;
}

}  // namespace Discrete::MainThreadDispatcher
