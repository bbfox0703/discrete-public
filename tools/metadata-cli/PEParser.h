// PEParser.h — minimal PE (Portable Executable) reader for offline
// IL2CPP analysis. Phase D step 1.
//
// Scope: parse DOS header + NT headers + section table from a PE file
// on disk, expose enough surface for the rest of Phase D (offline
// registration finder + method-pointer resolution) to operate on file
// bytes without launching the game.
//
// Non-goals (deliberately): import / export tables, relocations,
// directory entries, resource section walking, debug symbols. Add them
// in follow-up phases if a real consumer needs them.
//
// PE structure references (Microsoft / ECMA-335):
//   * IMAGE_DOS_HEADER  — 0x40 bytes; `e_magic` = "MZ"; `e_lfanew`
//                         points to NT header.
//   * IMAGE_NT_HEADERS  — Signature ("PE\0\0") + FileHeader + OptionalHeader.
//   * IMAGE_SECTION_HEADER — 0x28 bytes; one per section.
//
// We intentionally re-declare the necessary structs locally instead of
// pulling in <windows.h> from this header so consumers (metadata-cli +
// future cross-platform tools) can build without the Win32 SDK in
// their include path. The implementation file uses <windows.h> for
// the magic constants; the structs match its definitions byte-for-byte.

#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace UnityCEDumper::Phase_D::PE {

enum class Machine : std::uint16_t {
    Unknown = 0x0000,
    I386    = 0x014C,   // x86
    Amd64   = 0x8664,   // x64
};

// One section table entry. Names are truncated to 8 bytes by spec.
struct Section {
    std::string   name;            // null-stripped from the 8-byte field
    std::uint32_t virtualAddress;  // RVA of the section in the loaded image
    std::uint32_t virtualSize;     // size in memory (may exceed rawSize)
    std::uint32_t fileOffset;      // PointerToRawData — offset in file
    std::uint32_t rawSize;         // SizeOfRawData — bytes on disk
    std::uint32_t characteristics; // IMAGE_SCN_* flags
};

// Result of parsing a PE file. Lifetime-coupled to the underlying
// byte buffer the caller supplied to Parse().
struct Image {
    Machine                  machine        = Machine::Unknown;
    bool                     is64Bit        = false;
    std::uint64_t            imageBase      = 0;     // VA at which the image expects to load
    std::uint32_t            entryPointRva  = 0;
    std::uint32_t            sizeOfImage    = 0;
    std::vector<Section>     sections;

    // Translate an RVA into a file offset, walking the section table.
    // Returns std::nullopt if the RVA doesn't fall in any section
    // (which happens for RVAs inside the headers — those map 1:1).
    std::optional<std::uint64_t> RvaToFileOffset(std::uint64_t rva) const noexcept;

    // Translate a file offset back into an RVA. Inverse of the above —
    // useful when an AOB hit lands in a section's raw bytes and the
    // caller needs the corresponding loaded-image RVA.
    std::optional<std::uint64_t> FileOffsetToRva(std::uint64_t fileOffset) const noexcept;

    // Find the section whose VA range covers `rva`. Lifetime tied to
    // this Image. Returns nullptr on miss.
    const Section* FindSectionByRva(std::uint64_t rva) const noexcept;
};

// Parse a PE file from raw bytes. Returns std::nullopt on any of:
//   * file too small for the DOS / NT / section headers
//   * bad MZ / PE magic
//   * unsupported optional-header magic (only PE32 / PE32+ accepted)
//   * section-table boundary checks fail
//
// `errOut` (optional) receives a one-line diagnostic on failure.
std::optional<Image> Parse(const std::uint8_t* data,
                           std::size_t          size,
                           std::string*         errOut = nullptr);

// Convenience wrapper: open the file, slurp into a vector, parse.
// On success, `bytesOut` (optional) receives ownership of the byte
// buffer — keep it alive for the lifetime of the returned Image,
// since Image's section table indexes into `bytesOut`.
std::optional<Image> ParseFile(const std::wstring&         path,
                               std::vector<std::uint8_t>*  bytesOut = nullptr,
                               std::string*                errOut   = nullptr);

}  // namespace UnityCEDumper::Phase_D::PE
