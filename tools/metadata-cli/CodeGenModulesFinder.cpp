// CodeGenModulesFinder.cpp — see header for design rationale.
//
// Scan strategy: walk init-data sections, look for runs of 8-byte
// aligned pointer-shaped values where every entry dereferences to a
// struct whose first field is a pointer to a string ending in ".dll".
// That's the unique signature of the `codeGenModules` array of
// `Il2CppCodeGenModule*` — moduleName is always the first field of
// each codeGenModule, and Unity's IL2CPP toolchain always names the
// modules after the source assembly file (`Assembly-CSharp.dll`,
// `mscorlib.dll`, etc.).
//
// Once the array is located, we read each module's first three
// fields (moduleName, methodPointerCount, methodPointers) — those
// have been stable across every version from v24.2 onwards. The
// tail (adjustorThunks / invokerIndices / rgctx / debuggerMetadata /
// moduleInitializer …) varies per version but we don't need those
// for D-4's purpose (method-pointer resolution).

#include "CodeGenModulesFinder.h"

#include <algorithm>
#include <cstring>

namespace UnityCEDumper::Phase_D::CodeGenModules {

namespace {

constexpr std::uint32_t kScn_Read     = 0x40000000u;
constexpr std::uint32_t kScn_Exec     = 0x20000000u;
constexpr std::uint32_t kScn_CntInit  = 0x00000040u;
constexpr std::uint32_t kScn_CntBss   = 0x00000080u;

constexpr std::uint64_t kMaxMethodCount = 1ull << 24;   // 16M methods is generous
constexpr std::size_t   kMinModuleNameLen = 5;          // shortest plausible "X.dll" is 5 chars

bool ReadAt(const std::uint8_t* bytes, std::size_t size,
            std::uint64_t offset, void* dst, std::size_t n) noexcept {
    if (offset > size || offset + n > size) return false;
    std::memcpy(dst, bytes + offset, n);
    return true;
}

std::optional<std::uint64_t> VaToFileOffset(const PE::Image& img, std::uint64_t va) noexcept {
    if (va < img.imageBase) return std::nullopt;
    return img.RvaToFileOffset(va - img.imageBase);
}

// Read an ASCIIZ string at `fileOffset`. Returns empty on failure or
// if the string exceeds `maxLen` (defends against runaway reads into
// uninitialised .bss tail).
std::string ReadCString(const std::uint8_t* bytes, std::size_t size,
                         std::uint64_t fileOffset, std::size_t maxLen = 256) noexcept {
    std::string out;
    out.reserve(32);
    for (std::size_t i = 0; i < maxLen; ++i) {
        if (fileOffset + i >= size) return {};
        char c = static_cast<char>(bytes[fileOffset + i]);
        if (c == '\0') return out;
        // Plausibility: module names are ASCII alnum + ".-_".
        if (!((c >= 'A' && c <= 'Z') ||
              (c >= 'a' && c <= 'z') ||
              (c >= '0' && c <= '9') ||
              c == '.' || c == '-' || c == '_' || c == '+')) {
            return {};   // non-printable / bad char → not a module name
        }
        out.push_back(c);
    }
    // Hit maxLen without seeing NUL → reject.
    return {};
}

// Validate that `ptr` (a candidate Il2CppCodeGenModule* VA) points to
// a struct shaped like {ptr moduleName, i64 methodCount, ptr methodPtrs}
// where moduleName resolves to a ".dll" string. Returns the parsed
// Module on success, std::nullopt on any structural rejection.
std::optional<Module> ProbeCodeGenModule(
    const std::uint8_t* bytes, std::size_t size,
    const PE::Image& img,
    std::uint64_t modulePtrVa) noexcept {

    if (modulePtrVa == 0 || (modulePtrVa & 7ull) != 0) return std::nullopt;
    auto modFileOff = VaToFileOffset(img, modulePtrVa);
    if (!modFileOff) return std::nullopt;

    // First three fields: moduleName / methodPointerCount / methodPointers.
    std::uint64_t moduleNameVa  = 0;
    std::uint64_t methodCount   = 0;
    std::uint64_t methodPtrsVa  = 0;
    if (!ReadAt(bytes, size, *modFileOff + 0x00, &moduleNameVa, 8)) return std::nullopt;
    if (!ReadAt(bytes, size, *modFileOff + 0x08, &methodCount,  8)) return std::nullopt;
    if (!ReadAt(bytes, size, *modFileOff + 0x10, &methodPtrsVa, 8)) return std::nullopt;

    // moduleName must point to a ".dll" string in some readable section.
    auto nameFileOff = VaToFileOffset(img, moduleNameVa);
    if (!nameFileOff) return std::nullopt;
    std::string name = ReadCString(bytes, size, *nameFileOff);
    if (name.size() < kMinModuleNameLen) return std::nullopt;
    // Ends with ".dll" — case-sensitive on disk (Unity emits lowercase).
    static constexpr const char* kSuffix = ".dll";
    if (name.size() < 4 ||
        name.compare(name.size() - 4, 4, kSuffix) != 0) {
        return std::nullopt;
    }

    // methodCount sanity.
    if (methodCount == 0 || methodCount >= kMaxMethodCount) return std::nullopt;

    // methodPointers VA must translate cleanly and start non-null.
    if (methodPtrsVa == 0 || (methodPtrsVa & 7ull) != 0) return std::nullopt;
    auto methodPtrsFileOff = VaToFileOffset(img, methodPtrsVa);
    if (!methodPtrsFileOff) return std::nullopt;

    Module m;
    m.name              = std::move(name);
    m.modulePtrVa       = modulePtrVa;
    if (auto rva = img.FileOffsetToRva(*modFileOff); rva) m.modulePtrRva = *rva;
    m.methodPointersVa  = methodPtrsVa;
    m.methodPointersRva = methodPtrsVa - img.imageBase;
    m.methodCount       = methodCount;
    return m;
}

}  // namespace

std::optional<Found> FindOffline(const std::uint8_t* bytes,
                                  std::size_t          size,
                                  const PE::Image&     img,
                                  std::size_t          minModules,
                                  std::size_t          maxModules,
                                  std::string*         errOut) {
    if (bytes == nullptr || size == 0) {
        if (errOut) *errOut = "empty file";
        return std::nullopt;
    }
    if (img.machine != PE::Machine::Amd64) {
        if (errOut) *errOut = "only x64 IL2CPP supported (x86 layout TBD)";
        return std::nullopt;
    }

    // Collect init-data sections — same set RegistrationFinder uses.
    std::vector<const PE::Section*> dataSections;
    for (const auto& s : img.sections) {
        std::uint32_t ch = s.characteristics;
        if (!(ch & kScn_Read))                     continue;
        if (ch & kScn_Exec)                        continue;
        if (!(ch & (kScn_CntInit | kScn_CntBss)))  continue;
        dataSections.push_back(&s);
    }
    if (dataSections.empty()) {
        if (errOut) *errOut = "no init-data sections to scan";
        return std::nullopt;
    }

    // Scan each data section for runs of valid codeGenModule pointers.
    // At each 8-byte aligned offset:
    //   1. Read a u64 there. If 0 or unaligned, skip.
    //   2. Probe — does it look like a valid Il2CppCodeGenModule*?
    //   3. If yes, keep extending the run by reading the next u64s and
    //      probing each. Stop when ProbeCodeGenModule fails.
    //   4. If the run reached >= minModules and <= maxModules, accept.

    Found best;
    std::size_t bestStartIdx = 0;
    (void)bestStartIdx;

    for (const PE::Section* sec : dataSections) {
        if (sec->rawSize < 16) continue;
        std::uint64_t startFileOff = sec->fileOffset;
        std::uint64_t endFileOff   = std::min<std::uint64_t>(
            startFileOff + sec->rawSize, size);

        for (std::uint64_t off = startFileOff;
             off + 8 <= endFileOff;
             off += 8) {
            std::uint64_t firstPtr = 0;
            if (!ReadAt(bytes, size, off, &firstPtr, 8)) continue;
            if (firstPtr == 0 || (firstPtr & 7ull) != 0) continue;

            auto firstModule = ProbeCodeGenModule(bytes, size, img, firstPtr);
            if (!firstModule) continue;

            // Extend the run.
            std::vector<Module> run;
            run.push_back(*firstModule);
            std::uint64_t cursor = off + 8;
            while (run.size() < maxModules && cursor + 8 <= endFileOff) {
                std::uint64_t nextPtr = 0;
                if (!ReadAt(bytes, size, cursor, &nextPtr, 8)) break;
                if (nextPtr == 0 || (nextPtr & 7ull) != 0) break;
                auto m = ProbeCodeGenModule(bytes, size, img, nextPtr);
                if (!m) break;
                run.push_back(*m);
                cursor += 8;
            }

            if (run.size() < minModules) {
                // Skip past the failed run so we don't re-probe its members.
                off = cursor;
                continue;
            }

            // Found it. Keep the longest run we've seen so far — defends
            // against a small false-positive cluster appearing earlier in
            // a section than the real codeGenModules array.
            if (run.size() > best.modules.size()) {
                best.arrayRva = 0;
                if (auto rva = img.FileOffsetToRva(off); rva) best.arrayRva = *rva;
                best.arrayVa     = img.imageBase + best.arrayRva;
                best.moduleCount = run.size();
                best.modules     = std::move(run);
                best.sectionName = sec->name;
            }
            // Skip past the consumed run.
            off = cursor;
        }
    }

    if (best.modules.empty()) {
        if (errOut) {
            *errOut = "no codeGenModules array matched (pre-v24.2 IL2CPP uses the "
                      "flat methodPointers path — not yet supported offline)";
        }
        return std::nullopt;
    }
    return best;
}

}  // namespace UnityCEDumper::Phase_D::CodeGenModules
