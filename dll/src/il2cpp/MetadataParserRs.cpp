// MetadataParserRs.cpp — Phase R1. Adapts the Rust metadata parser
// (dll/rust/src/metadata, C ABI in dll/rust/include/discrete_core.h) to the
// existing Discrete::IL2CPP::Metadata::Parser class, so no consumer changes.
//
// The legacy C++ implementation (MetadataParser.cpp) was removed when R1
// closed (git history, last in 5748299). This file holds no parsing logic: every method marshals arguments,
// calls one dc_md_* function and copies the result into the C++ view types.
// Parser::Get() / Load() stay in MetadataParserHostPath.cpp (they only call
// LoadFromPath).
//
// Lifetime: every DcBytes the Rust side returns points into the handle's file
// buffer, so it is copied into std::string / std::vector before returning.

#include "MetadataParser.h"

#include <discrete_core.h>

#include <algorithm>
#include <utility>

namespace Discrete::IL2CPP::Metadata {

namespace {

std::string ToString(const DcBytes& b) {
    if (b.ptr == nullptr || b.len == 0) return {};
    return std::string(reinterpret_cast<const char*>(b.ptr), b.len);
}

std::vector<std::uint8_t> ToBytes(const DcBytes& b) {
    if (b.ptr == nullptr || b.len == 0) return {};
    return std::vector<std::uint8_t>(b.ptr, b.ptr + b.len);
}

// A view of a std::string for passing back into Rust. Valid only while the
// source string lives (always the duration of one dc_md_* call here).
DcBytes Borrow(const std::string& s) {
    return DcBytes{reinterpret_cast<const std::uint8_t*>(s.data()), s.size()};
}

Parser::TypeDefView FromDc(const DcTypeDef& d) {
    Parser::TypeDefView v;
    v.index              = d.index;
    v.ns                 = ToString(d.ns);
    v.name               = ToString(d.name);
    v.declaringTypeIndex = d.declaring_type_index;
    v.fieldStart         = d.field_start;
    v.fieldCount         = d.field_count;
    v.methodStart        = d.method_start;
    v.methodCount        = d.method_count;
    v.flags              = d.flags;
    v.bitfield           = d.bitfield;
    v.token              = d.token;
    return v;
}

DcTypeDef ToDc(const Parser::TypeDefView& v) {
    DcTypeDef d{};
    d.index                = v.index;
    d.ns                   = Borrow(v.ns);
    d.name                 = Borrow(v.name);
    d.declaring_type_index = v.declaringTypeIndex;
    d.field_start          = v.fieldStart;
    d.field_count          = v.fieldCount;
    d.method_start         = v.methodStart;
    d.method_count         = v.methodCount;
    d.flags                = v.flags;
    d.bitfield             = v.bitfield;
    d.token                = v.token;
    return d;
}

Parser::FieldDefView FromDc(const DcFieldDef& d) {
    Parser::FieldDefView v;
    v.index     = d.index;
    v.name      = ToString(d.name);
    v.typeIndex = d.type_index;
    v.token     = d.token;
    return v;
}

Parser::MethodDefView FromDc(const DcMethodDef& d) {
    Parser::MethodDefView v;
    v.index              = d.index;
    v.name               = ToString(d.name);
    v.declaringTypeIndex = d.declaring_type_index;
    v.returnTypeIndex    = d.return_type_index;
    v.parameterStart     = d.parameter_start;
    v.parameterCount     = d.parameter_count;
    v.flags              = d.flags;
    v.token              = d.token;
    return v;
}

DcMethodDef ToDc(const Parser::MethodDefView& v) {
    DcMethodDef d{};
    d.index                = v.index;
    d.name                 = Borrow(v.name);
    d.declaring_type_index = v.declaringTypeIndex;
    d.return_type_index    = v.returnTypeIndex;
    d.parameter_start      = v.parameterStart;
    d.parameter_count      = v.parameterCount;
    d.flags                = v.flags;
    d.token                = v.token;
    return d;
}

Parser::ParameterDefView FromDc(const DcParameterDef& d) {
    Parser::ParameterDefView v;
    v.index     = d.index;
    v.name      = ToString(d.name);
    v.typeIndex = d.type_index;
    v.token     = d.token;
    return v;
}

Parser::ImageDefView FromDc(const DcImageDef& d) {
    Parser::ImageDefView v;
    v.index       = d.index;
    v.name        = ToString(d.name);
    v.typeStart   = d.type_start;
    v.typeCount   = d.type_count;
    v.token       = d.token;
    v.methodStart = d.method_start;
    v.methodEnd   = d.method_end;
    return v;
}

// Shared shape of the dc_md_enumerate_* calls: size the buffer from the
// caller's count hint, call once, and call again only if Rust reports more
// rows than the hint (the hint is an upper bound today, so that is a guard,
// not an expected path).
template <typename Dc, typename View, typename Call>
std::vector<View> Enumerate(std::size_t hint, Call&& call) {
    std::vector<Dc> rows(hint);
    std::size_t total = call(rows.data(), rows.size());
    if (total > rows.size()) {
        rows.resize(total);
        total = std::min(call(rows.data(), rows.size()), rows.size());
    }
    std::vector<View> out;
    out.reserve(total);
    for (std::size_t i = 0; i < total; ++i) out.push_back(FromDc(rows[i]));
    return out;
}

}  // namespace

// --- lifetime ----------------------------------------------------------------

Parser::~Parser() {
    dc_md_free(rs_);
}

Parser::Parser(Parser&& other) noexcept
    : loaded_(other.loaded_), version_(other.version_), rs_(other.rs_) {
    other.rs_      = nullptr;
    other.loaded_  = false;
    other.version_ = 0;
}

Parser& Parser::operator=(Parser&& other) noexcept {
    if (this != &other) {
        dc_md_free(rs_);
        loaded_  = std::exchange(other.loaded_, false);
        version_ = std::exchange(other.version_, 0);
        rs_      = std::exchange(other.rs_, nullptr);
    }
    return *this;
}

bool Parser::LoadFromPath(const std::wstring& path) {
    // A fresh handle per load; logging (incl. the "empty path" / "failed to
    // read" lines) happens on the Rust side through the RustCore log sink.
    dc_md_free(rs_);
    static_assert(sizeof(wchar_t) == sizeof(std::uint16_t), "UTF-16 wchar_t expected");
    rs_ = dc_md_load_path_w(reinterpret_cast<const std::uint16_t*>(path.data()), path.size());
    loaded_  = dc_md_loaded(rs_) != 0;
    version_ = dc_md_version(rs_);
    return loaded_;
}

// --- header / strings --------------------------------------------------------

bool Parser::tablesDegraded() const noexcept {
    return dc_md_tables_degraded(rs_) != 0;
}

std::string Parser::v24SubVersionLabel() const {
    return ToString(dc_md_v24_sub_version_label(rs_));
}

std::uint32_t Parser::StringLiteralCount() const noexcept {
    return dc_md_string_literal_count(rs_);
}

std::optional<std::string> Parser::GetStringLiteral(std::uint32_t idx) const {
    DcBytes b{};
    if (dc_md_get_string_literal(rs_, idx, &b) == 0) return std::nullopt;
    return ToString(b);
}

std::vector<std::string> Parser::EnumerateStringLiterals(std::uint32_t maxCount) const {
    std::vector<std::string> out;
    if (!loaded_) return out;
    const std::uint32_t total = StringLiteralCount();
    const std::uint32_t limit = (maxCount == 0) ? total : std::min(maxCount, total);
    out.reserve(limit);
    for (std::uint32_t i = 0; i < limit; ++i) {
        if (auto s = GetStringLiteral(i)) out.push_back(std::move(*s));
        else                              out.emplace_back();
    }
    return out;
}

std::string Parser::GetHeapString(std::int32_t nameIndex) const {
    return ToString(dc_md_get_heap_string(rs_, nameIndex));
}

// --- type definitions / fields ------------------------------------------------

std::uint32_t Parser::TypeDefinitionCount() const noexcept {
    return dc_md_type_definition_count(rs_);
}

std::optional<Parser::TypeDefView> Parser::GetTypeDefinition(std::int32_t idx) const {
    DcTypeDef d{};
    if (dc_md_get_type_definition(rs_, idx, &d) == 0) return std::nullopt;
    return FromDc(d);
}

std::optional<Parser::TypeDefView>
Parser::FindTypeByName(const std::string& ns, const std::string& name) const {
    DcTypeDef d{};
    if (dc_md_find_type_by_name(rs_,
            reinterpret_cast<const std::uint8_t*>(ns.data()), ns.size(),
            reinterpret_cast<const std::uint8_t*>(name.data()), name.size(),
            &d) == 0) {
        return std::nullopt;
    }
    return FromDc(d);
}

std::vector<Parser::FieldDefView> Parser::EnumerateTypeFields(const TypeDefView& type) const {
    const DcTypeDef t = ToDc(type);
    return Enumerate<DcFieldDef, FieldDefView>(type.fieldCount,
        [&](DcFieldDef* out, std::size_t cap) {
            return dc_md_enumerate_type_fields(rs_, &t, out, cap);
        });
}

std::optional<Parser::FieldDefaultBlob> Parser::FindFieldDefault(std::int32_t globalFieldIndex) const {
    DcFieldDefault d{};
    if (dc_md_find_field_default(rs_, globalFieldIndex, &d) == 0) return std::nullopt;
    FieldDefaultBlob b;
    b.fieldIndex = d.field_index;
    b.typeIndex  = d.type_index;
    b.dataIndex  = d.data_index;
    b.data       = ToBytes(d.data);
    return b;
}

std::optional<std::string> Parser::TryDecodeStringDefault(std::int32_t dataIndex) const {
    DcBytes b{};
    if (dc_md_try_decode_string_default(rs_, dataIndex, &b) == 0) return std::nullopt;
    return ToString(b);
}

std::int32_t Parser::GetDeclaringTypeIndex(std::int32_t typeDefIdx) const {
    return dc_md_get_declaring_type_index(rs_, typeDefIdx);
}

// --- methods / parameters ------------------------------------------------------

std::uint32_t Parser::MethodDefinitionCount() const noexcept {
    return dc_md_method_definition_count(rs_);
}

std::optional<Parser::MethodDefView> Parser::GetMethodDefinition(std::int32_t idx) const {
    DcMethodDef d{};
    if (dc_md_get_method_definition(rs_, idx, &d) == 0) return std::nullopt;
    return FromDc(d);
}

std::vector<Parser::MethodDefView> Parser::EnumerateTypeMethods(const TypeDefView& type) const {
    const DcTypeDef t = ToDc(type);
    return Enumerate<DcMethodDef, MethodDefView>(type.methodCount,
        [&](DcMethodDef* out, std::size_t cap) {
            return dc_md_enumerate_type_methods(rs_, &t, out, cap);
        });
}

std::uint32_t Parser::ParameterCount() const noexcept {
    return dc_md_parameter_count(rs_);
}

std::optional<Parser::ParameterDefView> Parser::GetParameter(std::int32_t idx) const {
    DcParameterDef d{};
    if (dc_md_get_parameter(rs_, idx, &d) == 0) return std::nullopt;
    return FromDc(d);
}

std::vector<Parser::ParameterDefView>
Parser::EnumerateMethodParameters(const MethodDefView& method) const {
    const DcMethodDef m = ToDc(method);
    return Enumerate<DcParameterDef, ParameterDefView>(method.parameterCount,
        [&](DcParameterDef* out, std::size_t cap) {
            return dc_md_enumerate_method_parameters(rs_, &m, out, cap);
        });
}

// --- images ----------------------------------------------------------------------

std::uint32_t Parser::ImageCount() const noexcept {
    return dc_md_image_count(rs_);
}

std::optional<Parser::ImageDefView> Parser::GetImage(std::int32_t idx) const {
    DcImageDef d{};
    if (dc_md_get_image(rs_, idx, &d) == 0) return std::nullopt;
    return FromDc(d);
}

std::vector<Parser::ImageDefView> Parser::EnumerateImages() const {
    return Enumerate<DcImageDef, ImageDefView>(ImageCount(),
        [&](DcImageDef* out, std::size_t cap) {
            return dc_md_enumerate_images(rs_, out, cap);
        });
}

std::optional<Parser::ImageDefView>
Parser::FindImageContainingMethod(std::int32_t globalMethodIdx) const {
    DcImageDef d{};
    if (dc_md_find_image_containing_method(rs_, globalMethodIdx, &d) == 0) return std::nullopt;
    return FromDc(d);
}

}  // namespace Discrete::IL2CPP::Metadata
