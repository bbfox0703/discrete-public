// PEParser.cpp — implementation. See header for design + scope.

#include "PEParser.h"

#include <windows.h>

#include <algorithm>
#include <cstring>

namespace UnityCEDumper::Phase_D::PE {

namespace {

constexpr std::uint16_t kPe32  = 0x010Bu;   // IMAGE_NT_OPTIONAL_HDR32_MAGIC
constexpr std::uint16_t kPe32p = 0x020Bu;   // IMAGE_NT_OPTIONAL_HDR64_MAGIC

bool Fail(std::string* errOut, const char* msg) {
    if (errOut) *errOut = msg;
    return false;
}

}  // namespace

std::optional<std::uint64_t> Image::RvaToFileOffset(std::uint64_t rva) const noexcept {
    for (const auto& s : sections) {
        // Section's RVA range = [virtualAddress, virtualAddress + virtualSize).
        // Caller can hit RVAs inside the file headers (rva < first section's
        // VA) — those map 1:1 in real images because the headers are loaded
        // as-is. We return nullopt for "no section match" and let the caller
        // decide; pe-info's typical consumer will never ask for header RVAs.
        if (rva >= s.virtualAddress &&
            rva <  static_cast<std::uint64_t>(s.virtualAddress) + s.virtualSize) {
            std::uint64_t delta = rva - s.virtualAddress;
            // Past raw size = uninitialised BSS — no file backing.
            if (delta >= s.rawSize) return std::nullopt;
            return static_cast<std::uint64_t>(s.fileOffset) + delta;
        }
    }
    return std::nullopt;
}

std::optional<std::uint64_t> Image::FileOffsetToRva(std::uint64_t fileOffset) const noexcept {
    for (const auto& s : sections) {
        if (fileOffset >= s.fileOffset &&
            fileOffset <  static_cast<std::uint64_t>(s.fileOffset) + s.rawSize) {
            std::uint64_t delta = fileOffset - s.fileOffset;
            return static_cast<std::uint64_t>(s.virtualAddress) + delta;
        }
    }
    return std::nullopt;
}

const Section* Image::FindSectionByRva(std::uint64_t rva) const noexcept {
    for (const auto& s : sections) {
        if (rva >= s.virtualAddress &&
            rva <  static_cast<std::uint64_t>(s.virtualAddress) + s.virtualSize) {
            return &s;
        }
    }
    return nullptr;
}

std::optional<Image> Parse(const std::uint8_t* data,
                           std::size_t          size,
                           std::string*         errOut) {
    if (data == nullptr) { Fail(errOut, "null data pointer"); return std::nullopt; }
    if (size < sizeof(IMAGE_DOS_HEADER)) {
        Fail(errOut, "file too small for DOS header");
        return std::nullopt;
    }

    IMAGE_DOS_HEADER dos{};
    std::memcpy(&dos, data, sizeof(dos));
    if (dos.e_magic != IMAGE_DOS_SIGNATURE) {
        Fail(errOut, "bad DOS magic (not a PE)");
        return std::nullopt;
    }
    if (dos.e_lfanew < 0 ||
        static_cast<std::size_t>(dos.e_lfanew) + sizeof(std::uint32_t) +
            sizeof(IMAGE_FILE_HEADER) > size) {
        Fail(errOut, "e_lfanew out of range");
        return std::nullopt;
    }

    // PE\0\0 signature at e_lfanew.
    std::uint32_t signature = 0;
    std::memcpy(&signature, data + dos.e_lfanew, sizeof(signature));
    if (signature != IMAGE_NT_SIGNATURE) {
        Fail(errOut, "bad NT signature (not a PE)");
        return std::nullopt;
    }

    IMAGE_FILE_HEADER fileHeader{};
    std::memcpy(&fileHeader, data + dos.e_lfanew + sizeof(std::uint32_t),
                sizeof(fileHeader));

    // Optional header — variable size; magic tells us PE32 vs PE32+.
    std::size_t optHeaderOffset = dos.e_lfanew + sizeof(std::uint32_t) +
                                  sizeof(IMAGE_FILE_HEADER);
    if (optHeaderOffset + sizeof(std::uint16_t) > size) {
        Fail(errOut, "optional header magic out of range");
        return std::nullopt;
    }
    std::uint16_t optMagic = 0;
    std::memcpy(&optMagic, data + optHeaderOffset, sizeof(optMagic));

    Image img;
    img.machine = static_cast<Machine>(fileHeader.Machine);

    if (optMagic == kPe32) {
        if (optHeaderOffset + sizeof(IMAGE_OPTIONAL_HEADER32) > size) {
            Fail(errOut, "PE32 optional header truncated");
            return std::nullopt;
        }
        IMAGE_OPTIONAL_HEADER32 oh{};
        std::memcpy(&oh, data + optHeaderOffset, sizeof(oh));
        img.is64Bit       = false;
        img.imageBase     = oh.ImageBase;
        img.entryPointRva = oh.AddressOfEntryPoint;
        img.sizeOfImage   = oh.SizeOfImage;
    } else if (optMagic == kPe32p) {
        if (optHeaderOffset + sizeof(IMAGE_OPTIONAL_HEADER64) > size) {
            Fail(errOut, "PE32+ optional header truncated");
            return std::nullopt;
        }
        IMAGE_OPTIONAL_HEADER64 oh{};
        std::memcpy(&oh, data + optHeaderOffset, sizeof(oh));
        img.is64Bit       = true;
        img.imageBase     = oh.ImageBase;
        img.entryPointRva = oh.AddressOfEntryPoint;
        img.sizeOfImage   = oh.SizeOfImage;
    } else {
        Fail(errOut, "unsupported optional header magic (not PE32 / PE32+)");
        return std::nullopt;
    }

    // Section table follows the optional header. SizeOfOptionalHeader from
    // the FileHeader tells us where it ends — don't assume sizeof(IMAGE_
    // OPTIONAL_HEADER*) matches what's actually written (Windows allows
    // smaller optional headers in some niche cases).
    std::size_t sectionTableOffset = optHeaderOffset + fileHeader.SizeOfOptionalHeader;
    std::size_t sectionTableEnd =
        sectionTableOffset +
        static_cast<std::size_t>(fileHeader.NumberOfSections) * sizeof(IMAGE_SECTION_HEADER);
    if (sectionTableEnd > size) {
        Fail(errOut, "section table truncated");
        return std::nullopt;
    }

    img.sections.reserve(fileHeader.NumberOfSections);
    for (WORD i = 0; i < fileHeader.NumberOfSections; ++i) {
        IMAGE_SECTION_HEADER sh{};
        std::memcpy(&sh, data + sectionTableOffset + i * sizeof(IMAGE_SECTION_HEADER),
                    sizeof(sh));

        // Name field is 8 bytes, NOT null-terminated when it fills the whole
        // 8. Trim trailing nulls explicitly. Long names are PE-coff-style
        // ("/<dec offset>") — we don't resolve those (typical IL2CPP DLLs
        // use short names like .text / .rdata / .data / .rsrc / .reloc).
        char nameBuf[9] = {0};
        std::memcpy(nameBuf, sh.Name, 8);
        std::size_t nameLen = 0;
        while (nameLen < 8 && nameBuf[nameLen] != '\0') ++nameLen;

        Section s;
        s.name            = std::string(nameBuf, nameLen);
        s.virtualAddress  = sh.VirtualAddress;
        s.virtualSize     = sh.Misc.VirtualSize;
        s.fileOffset      = sh.PointerToRawData;
        s.rawSize         = sh.SizeOfRawData;
        s.characteristics = sh.Characteristics;
        img.sections.push_back(std::move(s));
    }

    return img;
}

std::optional<Image> ParseFile(const std::wstring&         path,
                               std::vector<std::uint8_t>*  bytesOut,
                               std::string*                errOut) {
    HANDLE h = ::CreateFileW(path.c_str(), GENERIC_READ,
                             FILE_SHARE_READ | FILE_SHARE_WRITE,
                             nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        Fail(errOut, "CreateFileW failed");
        return std::nullopt;
    }

    LARGE_INTEGER size{};
    if (!::GetFileSizeEx(h, &size) || size.QuadPart <= 0 ||
        // Sanity cap: 512 MB. GameAssembly.dll is typically 30-200 MB but
        // capping defends against accidentally passing a non-PE blob.
        size.QuadPart > (512ll * 1024 * 1024)) {
        ::CloseHandle(h);
        Fail(errOut, "file size invalid or > 512 MB");
        return std::nullopt;
    }

    std::vector<std::uint8_t> buf(static_cast<std::size_t>(size.QuadPart));
    DWORD totalRead = 0;
    DWORD remaining = static_cast<DWORD>(buf.size());
    DWORD got = 0;
    while (remaining > 0) {
        if (!::ReadFile(h, buf.data() + totalRead, remaining, &got, nullptr) || got == 0) {
            ::CloseHandle(h);
            Fail(errOut, "ReadFile failed mid-file");
            return std::nullopt;
        }
        totalRead += got;
        remaining -= got;
    }
    ::CloseHandle(h);

    auto img = Parse(buf.data(), buf.size(), errOut);
    if (img && bytesOut) {
        *bytesOut = std::move(buf);
    }
    return img;
}

}  // namespace UnityCEDumper::Phase_D::PE
