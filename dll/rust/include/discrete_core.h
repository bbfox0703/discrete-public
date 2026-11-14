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

// Static NUL-terminated banner, e.g. "discrete_core 0.1.0".
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

#ifdef __cplusplus
}  // extern "C"
#endif
