// MonoTypeParser.cpp — implementation.

#include "MonoTypeParser.h"

#include "MonoResolver.h"

#include <windows.h>

namespace UnityCEDumper::Mono {

namespace {

#pragma warning(push)
#pragma warning(disable: 4509)  // SEH used in function with object unwinding
char* SehTypeGetName(fn_type_get_name fn, MonoType* type) {
    __try {
        return fn(type);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}
#pragma warning(pop)

}  // namespace

std::string TypeName(MonoType* type) {
    if (type == nullptr) return {};
    const auto& r = GetResolver();
    if (r.type_get_name == nullptr) return {};

    char* raw = SehTypeGetName(r.type_get_name, type);
    if (raw == nullptr) return {};

    std::string out(raw);
    if (r.free != nullptr) r.free(raw);
    return out;
}

}  // namespace UnityCEDumper::Mono
