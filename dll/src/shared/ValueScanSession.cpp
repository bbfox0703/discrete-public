// ValueScanSession.cpp — implementation of the Phase 27b session container.

#include "ValueScanSession.h"

namespace Discrete::ValueScan {

SessionManager& SessionManager::Instance() {
    // Heap-allocated, intentionally leaked singleton. The DLL holds up to
    // 50K candidates per session × multiple sessions × ~200 bytes of
    // string metadata each — easily 10+ MB of small allocations. Running
    // the C++ destructor chain over those at process exit can take long
    // enough to make the game APPEAR to hang during shutdown (observed
    // live 2026-05-20 on 活俠傳 after a value scan).
    //
    // The leak is harmless: Windows reclaims the entire process heap on
    // termination, so we don't need to free anything. The destructor chain
    // only matters for FreeLibrary unload, which we never do in practice
    // (the DLL stays loaded until the game exits).
    static SessionManager* inst = new SessionManager();
    return *inst;
}

std::uint64_t SessionManager::Begin(Memory::ValueDataType dt,
                                     std::vector<Candidate> candidates,
                                     std::uint64_t ownerClientId) {
    auto now = std::chrono::steady_clock::now();
    std::lock_guard<std::mutex> lk(mu_);
    ExpireOldSessionsLocked(now);
    std::uint64_t id = nextId_++;
    auto sess = std::make_unique<Session>();
    sess->id            = id;
    sess->ownerClientId = ownerClientId;
    sess->dt            = dt;
    sess->candidates    = std::move(candidates);
    sess->lastUse       = now;
    sessions_.emplace(id, std::move(sess));
    return id;
}

bool SessionManager::End(std::uint64_t sessionId, std::uint64_t callerClientId) {
    std::lock_guard<std::mutex> lk(mu_);
    ExpireOldSessionsLocked(std::chrono::steady_clock::now());
    auto it = sessions_.find(sessionId);
    if (it == sessions_.end()) return false;
    if (it->second->ownerClientId != 0 && callerClientId != 0 &&
        it->second->ownerClientId != callerClientId) {
        return false;
    }
    sessions_.erase(it);
    return true;
}

std::size_t SessionManager::DropForOwner(std::uint64_t ownerClientId) {
    if (ownerClientId == 0) return 0;
    std::lock_guard<std::mutex> lk(mu_);
    std::size_t dropped = 0;
    for (auto it = sessions_.begin(); it != sessions_.end(); ) {
        if (it->second->ownerClientId == ownerClientId) {
            it = sessions_.erase(it);
            ++dropped;
        } else {
            ++it;
        }
    }
    return dropped;
}

void SessionManager::ExpireOldSessions() {
    auto now = std::chrono::steady_clock::now();
    std::lock_guard<std::mutex> lk(mu_);
    ExpireOldSessionsLocked(now);
}

void SessionManager::DropAll() {
    std::lock_guard<std::mutex> lk(mu_);
    sessions_.clear();
}

SessionManager::Stats SessionManager::GetStats() {
    Stats s;
    std::lock_guard<std::mutex> lk(mu_);
    s.sessionCount = sessions_.size();
    s.nextId       = nextId_;
    for (const auto& kv : sessions_) {
        s.totalCandidates += kv.second->candidates.size();
    }
    return s;
}

}  // namespace Discrete::ValueScan
