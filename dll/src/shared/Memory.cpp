// Memory.cpp — implementation of safe memory ops with SEH wrappers.

#include "Memory.h"

#include <windows.h>

namespace UnityCEDumper::Memory {

namespace {

bool RegionHasProtect(const void* addr, std::size_t size, DWORD requiredAny, DWORD forbiddenAny) noexcept {
    const std::uint8_t* p = static_cast<const std::uint8_t*>(addr);
    const std::uint8_t* end = p + size;
    while (p < end) {
        MEMORY_BASIC_INFORMATION mbi{};
        if (::VirtualQuery(p, &mbi, sizeof(mbi)) == 0) return false;
        if (mbi.State != MEM_COMMIT) return false;
        if (mbi.Protect & PAGE_GUARD) return false;
        if (mbi.Protect & forbiddenAny) return false;
        if ((mbi.Protect & requiredAny) == 0) return false;

        const std::uint8_t* regionEnd = static_cast<const std::uint8_t*>(mbi.BaseAddress) + mbi.RegionSize;
        if (regionEnd <= p) return false;
        p = regionEnd;
    }
    return true;
}

#pragma warning(push)
#pragma warning(disable: 4509)  // SEH used in function with object that has destructor — none here.

bool ReadSeh(const void* addr, void* dst, std::size_t size) noexcept {
    __try {
        ::memcpy(dst, addr, size);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool WriteSeh(void* addr, const void* src, std::size_t size) noexcept {
    __try {
        ::memcpy(addr, src, size);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

#pragma warning(pop)

}  // namespace

bool IsReadable(const void* addr, std::size_t size) noexcept {
    if (!addr || size == 0) return false;
    constexpr DWORD kReadable =
        PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY |
        PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
    return RegionHasProtect(addr, size, kReadable, PAGE_NOACCESS);
}

bool IsWritable(const void* addr, std::size_t size) noexcept {
    if (!addr || size == 0) return false;
    constexpr DWORD kWritable =
        PAGE_READWRITE | PAGE_WRITECOPY |
        PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
    return RegionHasProtect(addr, size, kWritable, PAGE_NOACCESS);
}

bool SafeReadBytes(const void* addr, void* dst, std::size_t size) noexcept {
    if (!addr || !dst || size == 0) return false;
    if (!IsReadable(addr, size)) return false;
    return ReadSeh(addr, dst, size);
}

bool SafeWriteBytes(void* addr, const void* src, std::size_t size) noexcept {
    if (!addr || !src || size == 0) return false;
    if (!IsWritable(addr, size)) return false;
    return WriteSeh(addr, src, size);
}

const std::uint8_t* AOBScan(const std::uint8_t* base, std::size_t size,
                            const std::uint8_t* pattern, std::string_view mask) noexcept {
    if (!base || !pattern || mask.empty() || size < mask.size()) return nullptr;
    const std::size_t patLen = mask.size();
    const std::uint8_t* end = base + size - patLen;
    for (const std::uint8_t* p = base; p <= end; ++p) {
        bool matched = true;
        for (std::size_t i = 0; i < patLen; ++i) {
            if (mask[i] == 'x' && p[i] != pattern[i]) { matched = false; break; }
        }
        if (matched) return p;
    }
    return nullptr;
}

const std::uint8_t* AOBScanModule(const wchar_t* moduleName,
                                  const std::uint8_t* pattern, std::string_view mask) noexcept {
    HMODULE mod = ::GetModuleHandleW(moduleName);
    if (!mod) return nullptr;

    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(mod);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return nullptr;
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS*>(reinterpret_cast<std::uint8_t*>(mod) + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return nullptr;

    const std::uint8_t* base = reinterpret_cast<const std::uint8_t*>(mod);
    const std::size_t size = nt->OptionalHeader.SizeOfImage;
    return AOBScan(base, size, pattern, mask);
}

}  // namespace UnityCEDumper::Memory
