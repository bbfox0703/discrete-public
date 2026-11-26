// ParallelScanDriver.h — exception-safe worker-pool driver for Memory.cpp (A-H2).
// Extracted into a header so offline C++ self-tests can inject thread-spawn
// failures and worker-body exceptions without crashing or mocking OS threads.
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

}  // namespace Discrete::Memory::Detail
