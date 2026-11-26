// ParallelScanDriver.h — exception-safe worker-pool drivers for Memory.cpp
// (A-H2) and IL2CPPAddressFinder.cpp (B-M10). Extracted into a header so
// offline C++ self-tests can inject thread-spawn failures and worker-body
// exceptions without crashing or mocking OS threads.
#pragma once

#include <thread>
#include <utility>
#include <vector>

namespace Discrete::Memory::Detail {

template <typename WorkerFn, typename SpawnFn>
void RunWorkerPool(unsigned int nthreads, WorkerFn&& worker, SpawnFn&& spawnThread) noexcept {
    auto safeWorker = [&worker]() noexcept {
        try {
            worker();
        } catch (...) {
            // Never allow an exception (e.g. std::bad_alloc on a 32-bit host)
            // to escape a std::thread entry point and invoke std::terminate.
        }
    };

    if (nthreads <= 1) {
        safeWorker();
        return;
    }

    std::vector<std::thread> pool;
    try {
        pool.reserve(nthreads);
        for (unsigned int t = 0; t < nthreads; ++t) {
            pool.push_back(spawnThread(safeWorker));
        }
    } catch (...) {
        // Thread creation failed (e.g. x86 address-space pressure). Join any
        // workers already started so pool destruction never hits std::terminate,
        // then drain any remaining work inline on the calling thread.
        for (auto& th : pool) {
            if (th.joinable()) th.join();
        }
        pool.clear();
        safeWorker();
    }
    for (auto& th : pool) {
        if (th.joinable()) th.join();
    }
}

// Indexed variant (B-M10): passes thread slot index `tid` in [0, nthreads) to
// `worker(tid)` so each worker can write into its own per-thread accumulator
// without locking. If thread creation fails partway through, all started
// threads are joined first and remaining work is drained inline on slot 0
// (which is idle once joined; workers MUST append/accumulate into their slot
// rather than overwriting prior results).
template <typename IndexedWorkerFn, typename SpawnFn>
void RunIndexedWorkerPool(unsigned int nthreads, IndexedWorkerFn&& worker, SpawnFn&& spawnThread) noexcept {
    auto safeWorker = [&worker](unsigned int tid) noexcept {
        try {
            worker(tid);
        } catch (...) {
            // Never allow an exception to escape a std::thread entry point.
        }
    };

    if (nthreads <= 1) {
        safeWorker(0u);
        return;
    }

    std::vector<std::thread> pool;
    try {
        pool.reserve(nthreads);
        for (unsigned int t = 0; t < nthreads; ++t) {
            pool.push_back(spawnThread([safeWorker, t]() noexcept { safeWorker(t); }));
        }
    } catch (...) {
        for (auto& th : pool) {
            if (th.joinable()) th.join();
        }
        pool.clear();
        safeWorker(0u);
    }
    for (auto& th : pool) {
        if (th.joinable()) th.join();
    }
}

template <typename IndexedWorkerFn>
void RunIndexedWorkerPool(unsigned int nthreads, IndexedWorkerFn&& worker) noexcept {
    RunIndexedWorkerPool(
        nthreads,
        std::forward<IndexedWorkerFn>(worker),
        [](auto&& fn) { return std::thread(std::forward<decltype(fn)>(fn)); });
}

}  // namespace Discrete::Memory::Detail
