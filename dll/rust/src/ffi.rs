//! FFI plumbing shared by every C ABI entry point: the log sink, the panic
//! guard and the crate version string.

use std::any::Any;
use std::ffi::{c_char, c_void};
use std::panic::{self, AssertUnwindSafe};
use std::sync::Once;
use std::sync::atomic::{AtomicUsize, Ordering};

/// Log categories. Values mirror the C header (`DC_LOG_*`); the C++ sink maps
/// them onto `MagicStrings::LogCategory*`, so the category *names* stay in the
/// C++ magic-strings file and this crate never spells a log path.
#[repr(u32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LogCategory {
    Init = 0,
    Scan = 1,
    Metadata = 2,
    Pipe = 3,
    Walk = 4,
}

/// `void (*)(uint32_t category, const char* msg, size_t len)` — `msg` is UTF-8,
/// not NUL-terminated, valid only for the duration of the call.
pub type LogSinkFn = unsafe extern "C" fn(category: u32, msg: *const c_char, len: usize);

/// The registered sink, stored as a plain address (0 = none). An atomic keeps
/// registration and use race-free without a lock on the logging path.
static LOG_SINK: AtomicUsize = AtomicUsize::new(0);
static PANIC_HOOK: Once = Once::new();

/// Writes one line through the registered sink. Silently drops the line when
/// no sink is registered — the same contract as the C++ `Log::Write` before
/// `Log::Init()` (metadata-cli never initialises logging).
pub fn log(category: LogCategory, msg: &str) {
    let addr = LOG_SINK.load(Ordering::Acquire);
    if addr == 0 {
        return;
    }
    // SAFETY: a non-zero value was stored by `dc_core_set_log_sink` from a
    // valid `LogSinkFn`; function pointers round-trip through usize losslessly.
    let sink: LogSinkFn = unsafe { std::mem::transmute::<usize, LogSinkFn>(addr) };
    // SAFETY: the sink contract only requires (ptr, len) to describe readable
    // bytes for the duration of the call.
    unsafe { sink(category as u32, msg.as_ptr().cast(), msg.len()) };
}

fn panic_message(payload: &(dyn Any + Send)) -> &str {
    if let Some(s) = payload.downcast_ref::<&str>() {
        s
    } else if let Some(s) = payload.downcast_ref::<String>() {
        s.as_str()
    } else {
        "<non-string panic payload>"
    }
}

/// Runs `f`, converting a panic into `fallback`. Every `extern "C"` function
/// wraps its body in this: a panic must never unwind across the FFI boundary.
/// `name` identifies the entry point in the log line.
pub fn guard<R>(name: &str, fallback: R, f: impl FnOnce() -> R) -> R {
    // AssertUnwindSafe: on panic we discard everything `f` touched and return
    // `fallback`; handles mutated mid-call are only ever read afterwards.
    match panic::catch_unwind(AssertUnwindSafe(f)) {
        Ok(v) => v,
        Err(payload) => {
            log(
                LogCategory::Init,
                &format!("discrete_core: panic caught in {name}: {}", panic_message(&*payload)),
            );
            fallback
        }
    }
}

/// Registers (or, with `None`, clears) the log sink. The first call also
/// installs a panic hook that routes panic messages to the sink instead of
/// stderr — an injected DLL has no console, and the default hook's stderr
/// write would just vanish.
///
/// # Safety
/// `sink`, when non-null, must stay callable from any thread for as long as
/// this library is loaded.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_core_set_log_sink(sink: Option<LogSinkFn>) {
    guard("dc_core_set_log_sink", (), || {
        LOG_SINK.store(sink.map_or(0, |f| f as usize), Ordering::Release);
        PANIC_HOOK.call_once(|| {
            panic::set_hook(Box::new(|info| {
                let loc = info
                    .location()
                    .map(|l| format!(" at {}:{}", l.file(), l.line()))
                    .unwrap_or_default();
                log(
                    LogCategory::Init,
                    &format!("discrete_core: panic: {}{loc}", panic_message(info.payload())),
                );
            }));
        });
    });
}

/// `int32_t (*)(void* ctx, uintptr_t addr, uint8_t* dst, size_t len)` — the
/// Phase R2 SEH read shim (C++ `Discrete::RustCore::SafeReadShim`). Rust never
/// dereferences foreign memory: an access violation unwinding through Rust
/// frames is undefined behaviour, so every read of host-process memory goes
/// through this callback, which copies under SEH and reports failure.
pub type ReadFn = unsafe extern "C" fn(ctx: *mut c_void, addr: usize, dst: *mut u8, len: usize) -> i32;

/// Safe wrapper around a [`ReadFn`] + its context.
#[derive(Clone, Copy)]
pub struct Reader {
    read: ReadFn,
    ctx: *mut c_void,
}

impl Reader {
    /// # Safety
    /// `read` must honour the `dc_read_fn` contract for every call, and `ctx`
    /// must stay valid for as long as this `Reader` is used.
    pub unsafe fn new(read: ReadFn, ctx: *mut c_void) -> Self {
        Self { read, ctx }
    }

    /// Copies `dst.len()` bytes from host address `addr`. Returns false on any
    /// fault (contents of `dst` are then unspecified).
    pub fn read(&self, addr: usize, dst: &mut [u8]) -> bool {
        if dst.is_empty() {
            return true;
        }
        // SAFETY: dst is a valid writable buffer of dst.len() bytes; the
        // callback contract (see `new`) covers the rest.
        unsafe { (self.read)(self.ctx, addr, dst.as_mut_ptr(), dst.len()) == 1 }
    }
}

const VERSION: &str = concat!("discrete_core ", env!("CARGO_PKG_VERSION"), "\0");

/// Static, NUL-terminated version banner (`"discrete_core 0.1.0"`). Logged at
/// DLL init so every per-PID log shows the Rust core actually ran.
#[unsafe(no_mangle)]
pub extern "C" fn dc_core_version() -> *const c_char {
    // Cannot panic, but stays inside guard so "every extern fn is guarded"
    // holds without exceptions to remember.
    guard("dc_core_version", c"discrete_core".as_ptr(), || VERSION.as_ptr().cast())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn guard_returns_value_when_no_panic() {
        assert_eq!(guard("t", -1, || 7), 7);
    }

    #[test]
    fn guard_converts_panic_to_fallback() {
        // Proves unwinding works for this target + CRT flavour; run under
        // both --target x86_64 and i686 (see build.ps1 Run-Tests).
        let got = guard("t", -1, || -> i32 { panic!("boom") });
        assert_eq!(got, -1);
    }

    #[test]
    fn version_is_nul_terminated() {
        let p = dc_core_version();
        // SAFETY: points at a static NUL-terminated string.
        let s = unsafe { std::ffi::CStr::from_ptr(p) };
        assert!(s.to_str().unwrap().starts_with("discrete_core "));
    }
}
