// MonoTypes.h — opaque forward decls for Mono runtime objects.
// We never instantiate these; they exist only to give type safety to function
// pointers declared in MonoResolver.
#pragma once

namespace UnityCEDumper::Mono {

struct MonoDomain;
struct MonoThread;
struct MonoAssembly;
struct MonoAssemblyName;
struct MonoImage;
struct MonoClass;
struct MonoClassField;
struct MonoMethod;
struct MonoMethodSignature;
struct MonoType;
struct MonoVTable;
// Phase 19 — managed-method invocation. Boxed object + exception + string
// types stay opaque; the invoker only ever passes pointers and reads
// fixed-offset header bytes via SafeReadBytes for unbox / message extraction.
struct MonoObject;
struct MonoException;
struct MonoString;

}  // namespace UnityCEDumper::Mono
