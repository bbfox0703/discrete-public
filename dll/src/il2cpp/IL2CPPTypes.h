// IL2CPPTypes.h — opaque forward decls for IL2CPP runtime objects.
#pragma once

namespace Discrete::IL2CPP {

struct Il2CppDomain;
struct Il2CppThread;
struct Il2CppAssembly;
struct Il2CppImage;
struct Il2CppClass;
struct Il2CppType;
struct FieldInfo;
struct MethodInfo;
// Phase 19 — managed-method invocation. Boxed object + exception + string
// types stay opaque; we only ever pass pointers and read fixed-offset
// header bytes via SafeReadBytes for unbox / message extraction.
struct Il2CppObject;
struct Il2CppException;
struct Il2CppString;

}  // namespace Discrete::IL2CPP
