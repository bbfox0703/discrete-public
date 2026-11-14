// discrete_core.h — C ABI of the Rust core (dll/rust). Hand-maintained: keep
// in sync with the #[unsafe(no_mangle)] functions in dll/rust/src/.
//
// Conventions (see dll/rust/src/lib.rs):
//   * Every function catches Rust panics internally and returns its documented
//     failure value instead; nothing ever unwinds into C++.
//   * Pointers returned by a dc_md_* function point into memory owned by the
//     DcMetadata handle and stay valid until dc_md_free() on that handle.
//   * Byte strings are (ptr, len) pairs, NOT NUL-terminated and NOT validated
//     as UTF-8. Copy them with std::string(ptr, len).
#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// --- Phase R0: core -------------------------------------------------------

// Log categories passed to the sink. The C++ side maps these to
// Discrete::MagicStrings::LogCategory*.
enum {
    DC_LOG_INIT     = 0,
    DC_LOG_SCAN     = 1,
    DC_LOG_METADATA = 2,
    DC_LOG_PIPE     = 3,
    DC_LOG_WALK     = 4,
};

// msg is UTF-8, not NUL-terminated, valid only during the call. May be called
// from any thread.
typedef void (*dc_log_sink_fn)(uint32_t category, const char* msg, size_t len);

// Register (or clear, with NULL) the log sink. Without a sink, Rust log lines
// are dropped — same as Discrete::Log::Write before Log::Init().
void dc_core_set_log_sink(dc_log_sink_fn sink);

// Static NUL-terminated banner: "discrete_core " followed by the same text as
// the C++ DISCRETE_BUILD_BANNER of the build that compiled it, e.g.
// "discrete_core 0.1.0.1469 9636b00 (Release, 2026-11-14T09:00:00Z)"
// ("discrete_core 0.1.0 (cargo)" when built by cargo outside CMake).
const char* dc_core_version(void);

// --- Phase R1: IL2CPP global-metadata.dat parser ---------------------------
//
// Rust port of the former C++ Discrete::IL2CPP::Metadata::Parser. The C++
// class (MetadataParser.h) is now a thin wrapper over these functions
// (MetadataParserRs.cpp), so every consumer keeps its API.
//
// Return conventions:
//   * int32_t "found" results: 1 = value written to *out, 0 = std::nullopt
//     (*out untouched). Never negative.
//   * dc_md_enumerate_*: writes min(total, cap) rows to out (out may be NULL
//     when cap == 0) and returns `total`, the number of rows the C++ API
//     would return. Callers size `out` from the row's count field
//     (fieldCount / methodCount / parameterCount / dc_md_image_count), so a
//     single call is normally enough.
//   * Every DcBytes points into the handle's file buffer (or static memory);
//     ptr may be NULL when len == 0.
//   * All functions accept a NULL handle and return the "not loaded" answer.
//   * A handle is immutable after load and safe to use from many threads at
//     once (the lazy nested-type map is built under a OnceLock).

typedef struct DcMetadata DcMetadata;

typedef struct DcBytes {
    const uint8_t* ptr;
    size_t         len;
} DcBytes;

typedef struct DcTypeDef {
    int32_t  index;
    DcBytes  ns;
    DcBytes  name;
    int32_t  declaring_type_index;
    int32_t  field_start;
    uint16_t field_count;
    int32_t  method_start;
    uint16_t method_count;
    uint32_t flags;
    uint32_t bitfield;
    uint32_t token;
} DcTypeDef;

typedef struct DcFieldDef {
    int32_t  index;
    DcBytes  name;
    int32_t  type_index;
    uint32_t token;
} DcFieldDef;

typedef struct DcMethodDef {
    int32_t  index;
    DcBytes  name;
    int32_t  declaring_type_index;
    int32_t  return_type_index;
    int32_t  parameter_start;
    uint16_t parameter_count;
    uint32_t flags;
    uint32_t token;
} DcMethodDef;

typedef struct DcParameterDef {
    int32_t  index;
    DcBytes  name;
    int32_t  type_index;
    uint32_t token;
} DcParameterDef;

typedef struct DcImageDef {
    int32_t  index;
    DcBytes  name;
    int32_t  type_start;
    uint32_t type_count;
    uint32_t token;
    int32_t  method_start;
    int32_t  method_end;
} DcImageDef;

typedef struct DcFieldDefault {
    int32_t field_index;
    int32_t type_index;
    int32_t data_index;
    DcBytes data;      // empty when data_index < 0 or the bytes are unavailable
} DcFieldDefault;

// Reads and parses the file at `path` (UTF-16, `len` code units, no NUL
// needed). Returns a handle even when loading fails — check dc_md_loaded();
// version etc. reflect whatever the failed load got to, exactly like the old
// C++ object did. Returns NULL only on an internal panic.
DcMetadata* dc_md_load_path_w(const uint16_t* path, size_t len);
void        dc_md_free(DcMetadata* md);  // NULL is a no-op

int32_t  dc_md_loaded(const DcMetadata* md);   // 0 / 1
int32_t  dc_md_version(const DcMetadata* md);
// "v24.0" / "v24.1" / "v24" for v24 metadata, empty otherwise. Static memory.
DcBytes  dc_md_v24_sub_version_label(const DcMetadata* md);

uint32_t dc_md_string_literal_count(const DcMetadata* md);
int32_t  dc_md_get_string_literal(const DcMetadata* md, uint32_t idx, DcBytes* out);
DcBytes  dc_md_get_heap_string(const DcMetadata* md, int32_t name_index);  // empty on failure

uint32_t dc_md_type_definition_count(const DcMetadata* md);
int32_t  dc_md_get_type_definition(const DcMetadata* md, int32_t idx, DcTypeDef* out);
int32_t  dc_md_find_type_by_name(const DcMetadata* md,
                                 const uint8_t* ns, size_t ns_len,
                                 const uint8_t* name, size_t name_len,
                                 DcTypeDef* out);
size_t   dc_md_enumerate_type_fields(const DcMetadata* md, const DcTypeDef* type,
                                     DcFieldDef* out, size_t cap);
int32_t  dc_md_find_field_default(const DcMetadata* md, int32_t global_field_index,
                                  DcFieldDefault* out);
int32_t  dc_md_try_decode_string_default(const DcMetadata* md, int32_t data_index,
                                         DcBytes* out);
int32_t  dc_md_get_declaring_type_index(const DcMetadata* md, int32_t type_def_idx);

uint32_t dc_md_method_definition_count(const DcMetadata* md);
int32_t  dc_md_get_method_definition(const DcMetadata* md, int32_t idx, DcMethodDef* out);
size_t   dc_md_enumerate_type_methods(const DcMetadata* md, const DcTypeDef* type,
                                      DcMethodDef* out, size_t cap);

uint32_t dc_md_parameter_count(const DcMetadata* md);
int32_t  dc_md_get_parameter(const DcMetadata* md, int32_t idx, DcParameterDef* out);
size_t   dc_md_enumerate_method_parameters(const DcMetadata* md, const DcMethodDef* method,
                                           DcParameterDef* out, size_t cap);

uint32_t dc_md_image_count(const DcMetadata* md);
int32_t  dc_md_get_image(const DcMetadata* md, int32_t idx, DcImageDef* out);
size_t   dc_md_enumerate_images(const DcMetadata* md, DcImageDef* out, size_t cap);
int32_t  dc_md_find_image_containing_method(const DcMetadata* md, int32_t global_method_idx,
                                            DcImageDef* out);

// --- Phase R2: shared SEH read shim ------------------------------------------
//
// Rust never dereferences foreign (game) memory. When a Rust module needs
// bytes from the host process it calls back into C++, which copies them under
// SEH (Memory::SafeReadBytes — see Discrete::RustCore::SafeReadShim). Returns
// 1 when all `len` bytes were copied into `dst`, 0 otherwise (dst contents
// unspecified). `ctx` is passed through untouched.
typedef int32_t (*dc_read_fn)(void* ctx, uintptr_t addr, uint8_t* dst, size_t len);

// --- Phase R2: AOB pattern builder (port of shared/AobPattern.cpp) -----------

typedef struct DcAobResult {
    size_t  length;               // bytes the pattern covers (after terminal trim)
    size_t  instruction_count;
    size_t  rel_branch_count;     // CALL/JMP/Jcc relative immediates wildcarded
    size_t  rip_rel_count;        // RIP-relative ModRM disp32 wildcarded
    size_t  bailed_at;            // valid when decoder_bailed
    size_t  requested_length;     // the `len` passed in
    size_t  pattern_len;          // bytes written to pattern_out
    uint8_t decoder_bailed;       // 0 / 1
    uint8_t stopped_at_terminal;  // 0 / 1
} DcAobResult;

// Builds the CE-style pattern ("48 89 5C 24 ?? E8 ?? ?? ?? ??", uppercase hex,
// single spaces, no trailing space, no NUL) for bytes[0..len). `bytes` may be
// NULL only when len == 0. pattern_out needs >= 3*len bytes, mask_out >= len
// (1 = wildcard, written for the first `length` bytes). The raw bytes of the
// C++ PatternResult are bytes[0..length) of the input, so they are not copied
// back. Returns 1 on success; 0 when a buffer is too small or on an internal
// panic (then *out is zeroed).
int32_t dc_aob_build_pattern(const uint8_t* bytes, size_t len, int32_t stop_at_terminal,
                             char* pattern_out, size_t pattern_cap,
                             uint8_t* mask_out, size_t mask_cap,
                             DcAobResult* out);

// --- Phase R2: GroupMatch SDR (port of shared/GroupMatch.h) ------------------
//
// Slots in CSR form: slot s's leaf-index list is
// leaves[offsets[s] .. offsets[s+1]), so `offsets` has n_slots + 1 entries
// (offsets may be NULL only when n_slots == 0; leaves may be NULL when no slot
// has entries). Leaf indices outside [0, n_leaves) are ignored, exactly like
// the legacy header. Panics fall back to 0.

int32_t dc_gm_has_distinct_assignment(const size_t* offsets, const int32_t* leaves,
                                      size_t n_slots, int32_t n_leaves);
// out has n_slots entries. Returns 1 and fills out[s] with slot s's distinct
// leaf, or returns 0 with every out[s] = -1.
int32_t dc_gm_resolve_assignment(const size_t* offsets, const int32_t* leaves,
                                 size_t n_slots, int32_t n_leaves, int32_t* out);

// --- Phase R2: value-scan refine comparator (port of shared/ValueRefine.cpp) -

// dt = numeric Discrete::Memory::ValueDataType (Int8=0 .. Double=9); any other
// value drops. scan_type is ASCII (ptr, len). cur / prev / arg_lo / arg_hi are
// 8-byte payloads; on keep, the first ValueDataTypeSize(dt) bytes of prev are
// overwritten with cur. Returns 1 keep / 0 drop (0 on panic, prev untouched).
int32_t dc_value_refine_compare(int32_t dt, const uint8_t* scan_type, size_t scan_type_len,
                                const uint8_t* cur, uint8_t* prev,
                                const uint8_t* arg_lo, const uint8_t* arg_hi,
                                double tolerance);

// --- Phase R2: IL2CPP static-field chain detector -----------------------------
// (port of il2cpp/IL2CPPChainDetector.cpp; Zydis -> iced-x86)

typedef struct DcChainMatch {
    uintptr_t hit_ip;       // hop-3 instruction (the field read / write)
    uintptr_t hop_one_ip;   // hop-1 RIP-relative class-pointer load
    uint8_t   is_write;     // 0 = "read", 1 = "write"
} DcChainMatch;

typedef struct DcChainStats {
    size_t  hop_one_candidates;
    size_t  chain_broken;
    size_t  chains_emitted;
    size_t  bytes_scanned;
    size_t  failed_reads;
    // 1 when the scan ran to completion and the counters above are meaningful
    // — the legacy function writes its ChainScanStats* ONLY then (it returns
    // early, leaving the caller's stats untouched, on empty slots /
    // static_fields_offset == 0 / module or sections not found).
    uint8_t valid;
} DcChainStats;

typedef struct DcChainResult DcChainResult;

// module_base = GameAssembly.dll base (0 = not loaded -> empty result). The PE
// headers and every executable section are read through `read`. Same chunking,
// dedup, ordering and match_limit semantics as the legacy function. Returns a
// handle, or NULL on an internal panic or when the 1 MiB scan buffer cannot be
// allocated (callers treat NULL as "no matches, stats untouched"); read it with
// the accessors below.
DcChainResult* dc_chain_detect(uintptr_t module_base, dc_read_fn read, void* read_ctx,
                               const uintptr_t* slots, size_t n_slots,
                               size_t static_fields_offset, size_t target_field_offset,
                               size_t match_limit);
size_t  dc_chain_result_len(const DcChainResult* r);                // 0 for NULL
int32_t dc_chain_result_get(const DcChainResult* r, size_t i, DcChainMatch* out);  // 1 / 0
void    dc_chain_result_stats(const DcChainResult* r, DcChainStats* out);         // zeroes for NULL
void    dc_chain_result_free(DcChainResult* r);                                    // NULL ok

// --- Phase R2: IL2CPP field-xref scan kernels ---------------------------------
// (port of il2cpp/IL2CPPFieldXrefScan.cpp; Zydis -> iced-x86 via the chain
// detector's decoder adapter.) The two pure loops behind IL2CPPFieldXref: the
// caller passes its own arrays (copied by Rust before the call returns); code
// bytes are read only through `read`. Same chunking, skip distances, decode
// stop rules and candidate order as the legacy functions — see
// IL2CPPFieldXrefScan.h for the contract. Each scan returns a handle, or NULL
// on an internal panic, a NULL `read`, or when its working buffer cannot be
// allocated (callers treat NULL as "no candidates"). A NULL array is accepted
// as empty. Read results with the accessors; every accessor accepts NULL.

typedef struct DcXrefSection {
    uintptr_t va;     // absolute VA of an executable section
    size_t    size;   // VirtualSize
} DcXrefSection;

typedef struct DcXrefRipHit {
    uintptr_t ip;      // instruction start
    uint8_t   opcode;  // 0x8B MOV r,m / 0x8D LEA (read); 0x89 MOV m,r (write)
} DcXrefRipHit;

typedef struct DcXrefRange {
    uintptr_t start;  // method body start
    size_t    len;    // bytes to decode (already clamped by the caller)
} DcXrefRange;

typedef struct DcXrefInstHit {
    size_t    method;  // index into the ranges array
    uintptr_t ip;      // instruction start
    uint8_t   write;   // 1 = write action, 0 = read (LEA counts as read)
} DcXrefInstHit;

typedef struct DcXrefRipResult  DcXrefRipResult;
typedef struct DcXrefInstResult DcXrefInstResult;

// ScanRipRelative: every REX? {8B,8D,89} ModRM(00,101) disp32 whose RIP-relative
// target == `target`, all of them, in scan order (1 MiB chunks + 16-byte
// overlap; an unreadable chunk skips 4 KiB).
DcXrefRipResult* dc_xref_rip_scan(const DcXrefSection* sections, size_t n, uintptr_t target,
                                  dc_read_fn read, void* read_ctx);
size_t  dc_xref_rip_result_len(const DcXrefRipResult* r);                        // 0 for NULL
int32_t dc_xref_rip_result_get(const DcXrefRipResult* r, size_t i, DcXrefRipHit* out);  // 1 / 0
void    dc_xref_rip_result_free(DcXrefRipResult* r);                             // NULL ok

// ScanInstanceAccesses: linear-decodes each range (stops at the first 0xCC or
// decode failure; an unreadable range is skipped) and reports every visible
// [gpr + field_offset] memory operand (no index, displacement present, base
// not RIP/RSP/RBP), method by method, instruction by instruction, operand by
// operand.
DcXrefInstResult* dc_xref_inst_scan(const DcXrefRange* ranges, size_t n, size_t field_offset,
                                    dc_read_fn read, void* read_ctx);
size_t  dc_xref_inst_result_len(const DcXrefInstResult* r);                         // 0 for NULL
int32_t dc_xref_inst_result_get(const DcXrefInstResult* r, size_t i, DcXrefInstHit* out);  // 1 / 0
void    dc_xref_inst_result_free(DcXrefInstResult* r);                              // NULL ok

#ifdef __cplusplus
}  // extern "C"
#endif
