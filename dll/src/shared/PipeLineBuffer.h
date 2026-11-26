// PipeLineBuffer.h — newline-delimited framing buffer for PipeServer (A-M2).
//
// Byte-mode Windows named pipes coalesce back-to-back writes into a single
// ReadFile() chunk. Preserving bytes after the first '\n' across ReadLine()
// calls prevents pipelined requests (e.g. a second RPC or cancelScan sent
// before the first response returns) from being silently dropped.
//
// Also enforces the 1 MiB per-line sanity cap and reports TooLarge explicitly
// so PipeServer can return a JSON-RPC -32600 error before closing (A-L5).
//
// Pure header — shared by PipeServer.cpp and dll/tests/dll_core_test.cpp.

#pragma once

#include <cstddef>
#include <string>

namespace Discrete::PipeFraming {

class LineBuffer {
public:
    static constexpr std::size_t kMaxLineBytes = 1024u * 1024u; // 1 MiB

    enum class ExtractStatus {
        NeedMore,
        LineReady,
        TooLarge,
    };

    explicit LineBuffer(std::size_t maxLineBytes = kMaxLineBytes) noexcept
        : maxLineBytes_(maxLineBytes) {}

    /// Append raw bytes read from the pipe.
    void Append(const char* data, std::size_t len) {
        if (data == nullptr || len == 0u) return;
        pending_.append(data, len);
    }

    /// Attempt to extract the next '\n'-delimited line (stripping '\r').
    /// Preserves all bytes after '\n' inside `pending_` for subsequent calls.
    ExtractStatus TryPopLine(std::string& out) {
        out.clear();
        const std::size_t nl = pending_.find('\n');
        if (nl == std::string::npos) {
            if (CountNonCr(pending_.data(), pending_.size()) > maxLineBytes_) {
                pending_.clear();
                return ExtractStatus::TooLarge;
            }
            return ExtractStatus::NeedMore;
        }

        const std::size_t contentLen = CountNonCr(pending_.data(), nl);
        if (contentLen > maxLineBytes_) {
            pending_.erase(0, nl + 1u);
            return ExtractStatus::TooLarge;
        }

        out.reserve(contentLen);
        for (std::size_t i = 0u; i < nl; ++i) {
            if (pending_[i] != '\r') out.push_back(pending_[i]);
        }
        pending_.erase(0, nl + 1u);
        return ExtractStatus::LineReady;
    }

    std::size_t PendingBytes() const noexcept { return pending_.size(); }
    void Clear() noexcept { pending_.clear(); }

private:
    static std::size_t CountNonCr(const char* s, std::size_t n) noexcept {
        std::size_t c = 0u;
        for (std::size_t i = 0u; i < n; ++i) {
            if (s[i] != '\r') ++c;
        }
        return c;
    }

    std::size_t maxLineBytes_;
    std::string pending_;
};

}  // namespace Discrete::PipeFraming
