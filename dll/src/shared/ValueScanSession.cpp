// ValueScanSession.cpp — implementation of the Phase 27b session container.

#include "ValueScanSession.h"

namespace UnityCEDumper::ValueScan {

SessionManager& SessionManager::Instance() {
    // Magic-static singleton. Thread-safe initialization per the C++11
    // standard. Lives for the DLL's lifetime — no destruction concern
    // because process exit unmaps everything.
    static SessionManager inst;
    return inst;
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
