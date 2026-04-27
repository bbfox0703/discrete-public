// Memory.h — safe in-process memory primitives for backend code paths.
// All reads/writes operate on the host process's address space (the game we're injected into).
#pragma once

#include <cstddef>
#include <cstdint>
#include <optional>
#include <string_view>
#include <vector>

namespace UnityCEDumper::Memory {

// Test whether [addr, addr+size) is fully readable (committed + non-PAGE_NOACCESS).
bool IsReadable(const void* addr, std::size_t size) noexcept;
bool IsWritable(const void* addr, std::size_t size) noexcept;

// SEH-protected raw read into dst. Returns false on AV; caller-supplied buffer untouched on failure.
bool SafeReadBytes(const void* addr, void* dst, std::size_t size) noexcept;
bool SafeWriteBytes(void* addr, const void* src, std::size_t size) noexcept;

// Typed read. Returns std::nullopt on AV / unreadable region.
template <typename T>
std::optional<T> SafeRead(const void* addr) noexcept {
    static_assert(std::is_trivially_copyable_v<T>, "SafeRead requires trivially copyable T");
    T value{};
    if (!SafeReadBytes(addr, &value, sizeof(T))) return std::nullopt;
    return value;
}

template <typename T>
bool SafeWrite(void* addr, const T& value) noexcept {
    static_assert(std::is_trivially_copyable_v<T>, "SafeWrite requires trivially copyable T");
    return SafeWriteBytes(addr, &value, sizeof(T));
}

// AOB scan in [base, base+size). `pattern` is the bytes; `mask` is one char per pattern byte:
// 'x' = match, '?' = wildcard. Returns first match or nullptr.
// Pattern and mask must have equal length.
const std::uint8_t* AOBScan(const std::uint8_t* base, std::size_t size,
                            const std::uint8_t* pattern, std::string_view mask) noexcept;

// Convenience: scan an entire module's image for a pattern.
const std::uint8_t* AOBScanModule(const wchar_t* moduleName,
                                  const std::uint8_t* pattern, std::string_view mask) noexcept;

}  // namespace UnityCEDumper::Memory
