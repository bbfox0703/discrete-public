// IL2CPPTypeParser.cpp — implementation.

#include "IL2CPPTypeParser.h"

#include "../shared/SehFilter.h"
#include "IL2CPPResolver.h"

#include <windows.h>

namespace Discrete::IL2CPP {

namespace {

#pragma warning(push)
#pragma warning(disable: 4509)
char* SehTypeGetName(fn_type_get_name fn, const Il2CppType* type) {
    __try { return fn(type); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
#pragma warning(pop)

}  // namespace

std::string TypeName(const Il2CppType* type) {
    if (type == nullptr) return {};
    const auto& r = GetResolver();
    if (r.type_get_name == nullptr) return {};

    char* raw = SehTypeGetName(r.type_get_name, type);
    if (raw == nullptr) return {};

    std::string out(raw);
    if (r.free != nullptr) r.free(raw);
    return out;
}

}  // namespace Discrete::IL2CPP
