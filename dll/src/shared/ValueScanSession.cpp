// ValueScanSession.cpp — implementation of the Phase 27b session container.

#include "ValueScanSession.h"

namespace UnityCEDumper::ValueScan {

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
                                     std::vector<Candidate> candidates) {
    // Opportunistic expiry pass on every Begin so abandoned sessions
    // don't pile up. Cheap when the table is small.
    ExpireOldSessions();

    std::lock_guard<std::mutex> lk(mu_);
    std::uint64_t id = nextId_++;
    auto sess = std::make_unique<Session>();
    sess->id         = id;
    sess->dt         = dt;
    sess->candidates = std::move(candidates);
    sess->lastUse    = std::chrono::steady_clock::now();
    sessions_.emplace(id, std::move(sess));
    return id;
}

bool SessionManager::End(std::uint64_t sessionId) {
    std::lock_guard<std::mutex> lk(mu_);
    return sessions_.erase(sessionId) > 0;
}

void SessionManager::ExpireOldSessions() {
    auto now = std::chrono::steady_clock::now();
    std::lock_guard<std::mutex> lk(mu_);
    for (auto it = sessions_.begin(); it != sessions_.end(); ) {
        if (now - it->second->lastUse > kExpirySeconds) {
            it = sessions_.erase(it);
        } else {
            ++it;
        }
    }
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

}  // namespace UnityCEDumper::ValueScan
