//! FFI plumbing shared by every C ABI entry point: the log sink, the panic
//! guard and the crate version string.

use std::any::Any;
use std::collections::HashSet;
use std::ffi::{c_char, c_void};
use std::fmt::Write as _;
use std::hash::Hash;
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

/// Fixed-capacity stack string buffer for OOM-safe logging on panic paths (D-L3).
/// Truncates cleanly on UTF-8 character boundaries and never allocates on the heap.
pub(crate) struct StackBuf<const N: usize> {
    buf: [u8; N],
    len: usize,
}

impl<const N: usize> StackBuf<N> {
    pub(crate) fn new() -> Self {
        Self { buf: [0u8; N], len: 0 }
    }

    pub(crate) fn as_str(&self) -> &str {
        // SAFETY: only valid UTF-8 chunks are pushed via write_str.
        unsafe { std::str::from_utf8_unchecked(&self.buf[..self.len]) }
    }
}

impl<const N: usize> std::fmt::Write for StackBuf<N> {
    fn write_str(&mut self, s: &str) -> std::fmt::Result {
        let avail = N - self.len;
        if avail == 0 {
            return Ok(());
        }
        let mut take = s.len().min(avail);
        while take > 0 && !s.is_char_boundary(take) {
            take -= 1;
        }
        self.buf[self.len..self.len + take].copy_from_slice(&s.as_bytes()[..take]);
        self.len += take;
        Ok(())
    }
}

/// Fallible `Vec::push` (D-L3): reserves 1 slot via `try_reserve` before pushing
/// so OOM truncates gracefully instead of aborting the host game process.
pub(crate) fn try_push<T>(vec: &mut Vec<T>, item: T) -> bool {
    if vec.len() == vec.capacity() && vec.try_reserve(1).is_err() {
        return false;
    }
    vec.push(item);
    true
}

/// Fallible `HashSet::insert` (D-L3): returns `None` on OOM instead of aborting,
/// `Some(true)` if newly inserted, `Some(false)` if already present.
pub(crate) fn try_insert<T: Eq + Hash>(set: &mut HashSet<T>, item: T) -> Option<bool> {
    if set.contains(&item) {
        return Some(false);
    }
    if set.len() == set.capacity() && set.try_reserve(1).is_err() {
        return None;
    }
    Some(set.insert(item))
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
            let mut msg = StackBuf::<512>::new();
            let _ = write!(msg, "discrete_core: panic caught in {name}: {}", panic_message(&*payload));
            log(LogCategory::Init, msg.as_str());
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
                let mut msg = StackBuf::<512>::new();
                let _ = write!(msg, "discrete_core: panic: {}", panic_message(info.payload()));
                if let Some(l) = info.location() {
                    let _ = write!(msg, " at {}:{}", l.file(), l.line());
                }
                log(LogCategory::Init, msg.as_str());
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

/// `int32_t (*)(void* ctx)` — optional cooperative cancellation callback (D-L6).
/// Returns non-zero when the enclosing scan should stop early.
pub type CancelFn = unsafe extern "C" fn(ctx: *mut c_void) -> i32;

/// Safe wrapper around an optional [`CancelFn`] + its context (D-L6).
#[derive(Clone, Copy)]
pub struct CancelCheck {
    cancel: Option<CancelFn>,
    ctx: *mut c_void,
}

impl Default for CancelCheck {
    fn default() -> Self {
        Self::none()
    }
}

impl CancelCheck {
    pub const fn none() -> Self {
        Self { cancel: None, ctx: std::ptr::null_mut() }
    }

    /// # Safety
    /// If `cancel` is `Some`, it must be callable with `ctx` for the duration
    /// of the scan without unwinding.
    pub unsafe fn new(cancel: Option<CancelFn>, ctx: *mut c_void) -> Self {
        Self { cancel, ctx }
    }

    #[inline]
    pub fn is_cancelled(&self) -> bool {
        match self.cancel {
            // SAFETY: caller contract in `new`.
            Some(f) => unsafe { f(self.ctx) != 0 },
            None => false,
        }
    }
}

/// Safe wrapper around a [`ReadFn`] + its context.
#[derive(Clone, Copy)]
pub struct Reader {
    read: ReadFn,
    ctx: *mut c_void,
}

/// OS page granularity used for D-M1 readable-span fallback when a 1 MiB chunk
/// read fails on an anti-cheat stripped (`PAGE_NOACCESS` / `PAGE_GUARD`) page.
pub(crate) const PAGE_SIZE: usize = 4096;

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

    /// Walks `[sec_va, sec_va + sec_size)` in chunks of up to `chunk_size` bytes
    /// (+ `overlap` tail bytes), invoking `on_span(span_va, span_bytes, owned_len)`
    /// for each contiguous readable span.
    ///
    /// Fast path: reads the full chunk (+ overlap) in one `read()` call — bit-for-bit
    /// identical to the legacy loop on fully-readable sections.
    /// Fallback (D-M1): when a whole chunk read fails, walks that chunk page-by-page
    /// (4 KiB), coalescing consecutive readable pages into contiguous spans (with
    /// overlap tail into the next chunk when readable) so NO readable pages before
    /// or after a `PAGE_NOACCESS` page are lost. Increments `*failed_pages` once per
    /// unreadable page.
    /// Cancellation (D-L6): polls `cancel` at every chunk start and inside the
    /// page-by-page fallback loop; sets `*cancelled = true` and stops early.
    ///
    /// `on_span` returns `true` to keep scanning or `false` to stop early.
    #[allow(clippy::too_many_arguments)]
    pub(crate) fn scan_readable_spans<F>(
        &self,
        sec_va: usize,
        sec_size: usize,
        chunk_size: usize,
        overlap: usize,
        buf: &mut [u8],
        failed_pages: &mut usize,
        cancel: CancelCheck,
        cancelled: &mut bool,
        mut on_span: F,
    ) -> bool
    where
        F: FnMut(usize, &[u8], usize) -> bool,
    {
        debug_assert!(buf.len() >= chunk_size + overlap);
        let mut pos = 0usize;
        while pos < sec_size {
            if cancel.is_cancelled() {
                *cancelled = true;
                return false;
            }
            let want = (sec_size - pos).min(chunk_size);
            let read_want = (want + overlap).min(sec_size - pos);

            // Fast path: whole chunk + overlap readable in one call.
            if self.read(sec_va.wrapping_add(pos), &mut buf[..read_want]) {
                if !on_span(sec_va.wrapping_add(pos), &buf[..read_want], want) {
                    return false;
                }
                pos = pos.wrapping_add(want);
                continue;
            }

            // Fallback (D-M1): walk [pos, pos + want) page-by-page, coalescing
            // adjacent readable pages so readable code before/after the bad page
            // is never dropped.
            let chunk_end = pos + want;
            let mut cursor = pos;
            let mut span_start = pos;
            let mut span_len = 0usize;

            while cursor < chunk_end {
                if cancel.is_cancelled() {
                    *cancelled = true;
                    return false;
                }
                let va = sec_va.wrapping_add(cursor);
                let page_rem = PAGE_SIZE - (va & (PAGE_SIZE - 1));
                let step = (chunk_end - cursor).min(page_rem);

                if self.read(va, &mut buf[span_len..span_len + step]) {
                    if span_len == 0 {
                        span_start = cursor;
                    }
                    span_len += step;
                } else {
                    *failed_pages += 1;
                    if span_len > 0 {
                        if !on_span(
                            sec_va.wrapping_add(span_start),
                            &buf[..span_len],
                            span_len,
                        ) {
                            return false;
                        }
                        span_len = 0;
                    }
                }
                cursor += step;
            }

            // Flush any trailing readable span at the end of this chunk,
            // opportunistically appending readable `overlap` bytes past
            // `chunk_end` so instructions straddling the chunk boundary still decode.
            if span_len > 0 {
                let mut total_len = span_len;
                let tail_avail = (sec_size - chunk_end).min(overlap);
                if tail_avail > 0
                    && self.read(
                        sec_va.wrapping_add(chunk_end),
                        &mut buf[span_len..span_len + tail_avail],
                    )
                {
                    total_len += tail_avail;
                }
                if !on_span(
                    sec_va.wrapping_add(span_start),
                    &buf[..total_len],
                    span_len,
                ) {
                    return false;
                }
            }

            pos = chunk_end;
        }
        true
    }
}

/// `DC_BUILD_BANNER` comes from build.rs: the C++ `DISCRETE_BUILD_BANNER`
/// format, fed by the same CMake values (see build.rs).
const VERSION: &str = concat!("discrete_core ", env!("DC_BUILD_BANNER"), "\0");

/// Static, NUL-terminated version banner, e.g.
/// `"discrete_core 0.1.0.1469 9636b00 (Release, 2026-11-14T09:00:00Z)"` —
/// `"discrete_core " + DISCRETE_BUILD_BANNER` of the build that compiled it
/// (`"discrete_core 0.1.0 (cargo)"` outside CMake). Logged at DLL init so
/// every per-PID log shows the Rust core ran and which build it came from.
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

    #[test]
    fn version_tracks_the_package_version() {
        // "discrete_core 0.1.0.<build> ..." under CMake, "discrete_core 0.1.0 (cargo)"
        // otherwise; build.rs refuses a Cargo.toml / project(VERSION) mismatch.
        let banner = VERSION.trim_end_matches('\0');
        let ver = banner.strip_prefix("discrete_core ").unwrap();
        let pkg = env!("CARGO_PKG_VERSION");
        assert!(ver == format!("{pkg} (cargo)") || ver.starts_with(&format!("{pkg}.")), "{banner}");
    }

    #[test]
    fn stack_buf_truncates_on_utf8_boundary_without_alloc() {
        // D-L3: panic log formatting uses StackBuf and never overflows or splits
        // a multi-byte UTF-8 sequence.
        let mut s = StackBuf::<8>::new();
        let _ = write!(s, "abcde😀xyz");
        assert_eq!(s.as_str(), "abcde");
        assert!(s.as_str().len() <= 8);

        let mut s2 = StackBuf::<16>::new();
        let _ = write!(s2, "ok: {}", 42);
        assert_eq!(s2.as_str(), "ok: 42");
    }
}
