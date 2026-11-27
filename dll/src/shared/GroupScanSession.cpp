// GroupScanSession.cpp — implementation of the group-scan session container
// (the group analogue of ValueScanSession.cpp).

#include "GroupScanSession.h"

namespace Discrete::GroupScan {

SessionManager& SessionManager::Instance() {
    // Intentionally-leaked singleton — same rationale as ValueScan::SessionManager:
    // running the C++ destructor chain over many small allocations at process
    // exit can make the game APPEAR to hang during shutdown. Windows reclaims the
    // whole heap on termination, so the leak is harmless (the DLL stays loaded
    // until the game exits).
    static SessionManager* inst = new SessionManager();
    return *inst;
}

std::uint64_t SessionManager::Begin(std::vector<SlotSpec> slots,
                                    std::vector<Candidate> candidates,
                                    std::uint64_t ownerClientId) {
    auto now = std::chrono::steady_clock::now();
    std::lock_guard<std::mutex> lk(mu_);
    ExpireOldSessionsLocked(now);
    std::uint64_t id = nextId_++;
    auto sess = std::make_unique<Session>();
    sess->id            = id;
    sess->ownerClientId = ownerClientId;
    sess->slots         = std::move(slots);
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

}  // namespace Discrete::GroupScan
