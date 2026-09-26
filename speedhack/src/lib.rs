//! 内置变速齿轮（MinHook inline hook 版，参考 OpenSpeedy）。
//!
//! 关键设计：
//! 1. 用 MinHook inline hook（改函数入口机器码），而非 IAT hook——
//!    IAT hook 只对「通过导入表静态调用」生效，Flash 副本内通过
//!    GetProcAddress 动态获取时间函数地址后调用，会完全绕过 IAT hook，
//!    这正是「主界面变速有效、进入副本失效」的根因。
//! 2. 在 DllMain（DLL_PROCESS_ATTACH）里同步安装所有 hook——此时 Flash
//!    尚未运行，MinHook 挂起线程 patch 无竞态；之前「等 Flash 加载后再
//!    异步 hook」会撞上高频调用导致 ppapi 进程崩溃。
//! 3. 覆盖 17 个时间/等待函数：时间读取用「锚定式 delta*倍率」，等待
//!    函数用「时间/倍率」。
//!
//! 倍率通过命名共享内存 `Local\YeyouSpeedHack`（8 字节 f64）从主程序下发。

use std::os::raw::{c_char, c_void};
use std::sync::atomic::{AtomicBool, AtomicPtr, AtomicU64, Ordering};

use minhook_sys::*;

type BOOL = i32;
type DWORD = u32;

// ============ 日志（仅在 hook 函数首次被调用时写，避开 DllMain 的 loader lock） ============

static LOGGED: AtomicBool = AtomicBool::new(false);
static HOOK_COUNT: AtomicU64 = AtomicU64::new(0);

fn log_line(msg: &str) {
    let _ = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open("speedhack.log")
        .and_then(|mut f| {
            use std::io::Write;
            writeln!(f, "{}", msg)
        });
}

fn log_once_if_needed() {
    if !LOGGED.swap(true, Ordering::Relaxed) {
        let n = HOOK_COUNT.load(Ordering::Relaxed);
        let speed = get_speed();
        log_line(&format!(
            "[speedhack] active, hooked={} funcs, speed={}",
            n, speed
        ));
    }
}

// ============ 共享内存倍率 ============

static SHARED_VIEW: AtomicPtr<f64> = AtomicPtr::new(std::ptr::null_mut());

fn to_wide_static(s: &str, buf: &mut [u16; 64]) -> *const u16 {
    let mut i = 0;
    for c in s.encode_utf16() {
        if i >= 63 {
            break;
        }
        buf[i] = c;
        i += 1;
    }
    buf[i] = 0;
    buf.as_ptr()
}

fn ensure_shared() -> *mut f64 {
    let p = SHARED_VIEW.load(Ordering::Relaxed);
    if !p.is_null() {
        return p;
    }
    unsafe {
        let mut name_buf = [0u16; 64];
        let name = to_wide_static("Local\\YeyouSpeedHack", &mut name_buf);
        let h = OpenFileMappingW(FILE_MAP_READ, 0, name);
        if h.is_null() {
            return std::ptr::null_mut();
        }
        let view = MapViewOfFile(h, FILE_MAP_READ, 0, 0, 8) as *mut f64;
        if view.is_null() {
            CloseHandle(h);
            return std::ptr::null_mut();
        }
        SHARED_VIEW.store(view, Ordering::Relaxed);
        view
    }
}

fn get_speed() -> f64 {
    let p = ensure_shared();
    if p.is_null() {
        return 1.0;
    }
    let v = unsafe { *p };
    if v <= 0.0 || !v.is_finite() {
        return 1.0;
    }
    v
}

// ============ 缩放器（原子无锁，锚定式） ============

struct Scaler {
    base_real: AtomicU64,
    base_hook: AtomicU64,
    last_real: AtomicU64,
    last_hook: AtomicU64,
    last_speed_bits: AtomicU64,
}

const fn scaler_new() -> Scaler {
    Scaler {
        base_real: AtomicU64::new(0),
        base_hook: AtomicU64::new(0),
        last_real: AtomicU64::new(0),
        last_hook: AtomicU64::new(0),
        last_speed_bits: AtomicU64::new(0),
    }
}

/// 锚定式缩放：返回 `base_hook + (now - base_real) * speed`。
/// 倍率变化时，在本次调用里先把锚点重置为「上次的 last_real/last_hook」，
/// 实现无缝切换（避免时间跳变）。
fn scale(s: &Scaler, now: u64, speed: f64) -> u64 {
    let speed_bits = speed.to_bits();
    let prev = s.last_speed_bits.swap(speed_bits, Ordering::Relaxed);
    if prev == 0 {
        // 首次调用：初始化锚点。
        s.base_real.store(now, Ordering::Relaxed);
        s.base_hook.store(now, Ordering::Relaxed);
        s.last_real.store(now, Ordering::Relaxed);
        s.last_hook.store(now, Ordering::Relaxed);
        return now;
    }
    if prev != speed_bits {
        // 倍率变化：重锚定（用上次的 last_real/last_hook 作为新锚点）。
        let lr = s.last_real.load(Ordering::Relaxed);
        let lh = s.last_hook.load(Ordering::Relaxed);
        s.base_real.store(lr, Ordering::Relaxed);
        s.base_hook.store(lh, Ordering::Relaxed);
    }
    let base_real = s.base_real.load(Ordering::Relaxed);
    let base_hook = s.base_hook.load(Ordering::Relaxed);
    let delta = ((now as f64 - base_real as f64) * speed) as u64;
    let result = base_hook + delta;
    s.last_real.store(now, Ordering::Relaxed);
    s.last_hook.store(result, Ordering::Relaxed);
    result
}

// 7 个时间读取函数，各自独立缩放器（基准/回绕特性不同）。
static S_TIMEGETTIME: Scaler = scaler_new();
static S_GETMESSAGETIME: Scaler = scaler_new();
static S_GETTICKCOUNT: Scaler = scaler_new();
static S_GETTICKCOUNT64: Scaler = scaler_new();
static S_QPC: Scaler = scaler_new();
static S_GSATFT: Scaler = scaler_new();
static S_GSPAFT: Scaler = scaler_new();

// ============ 原函数（trampoline） ============

static ORIG_SLEEP: AtomicU64 = AtomicU64::new(0);
static ORIG_SLEEPEX: AtomicU64 = AtomicU64::new(0);
static ORIG_WFSO: AtomicU64 = AtomicU64::new(0);
static ORIG_WFSOEX: AtomicU64 = AtomicU64::new(0);
static ORIG_WFMO: AtomicU64 = AtomicU64::new(0);
static ORIG_WFMOEX: AtomicU64 = AtomicU64::new(0);
static ORIG_SETTIMER: AtomicU64 = AtomicU64::new(0);
static ORIG_TIMEGETTIME: AtomicU64 = AtomicU64::new(0);
static ORIG_TIMESETEVENT: AtomicU64 = AtomicU64::new(0);
static ORIG_GETMESSAGETIME: AtomicU64 = AtomicU64::new(0);
static ORIG_GETTICKCOUNT: AtomicU64 = AtomicU64::new(0);
static ORIG_GETTICKCOUNT64: AtomicU64 = AtomicU64::new(0);
static ORIG_QPC: AtomicU64 = AtomicU64::new(0);
static ORIG_GSATFT: AtomicU64 = AtomicU64::new(0);
static ORIG_GSPAFT: AtomicU64 = AtomicU64::new(0);
static ORIG_SETWAITABLETIMER: AtomicU64 = AtomicU64::new(0);
static ORIG_SETWAITABLETIMEREX: AtomicU64 = AtomicU64::new(0);

// ============ Hook 函数 ============

// --- 等待类（时间 / 倍率） ---

unsafe extern "system" fn hooked_sleep(ms: DWORD) {
    log_once_if_needed();
    let orig = ORIG_SLEEP.load(Ordering::Relaxed);
    if orig == 0 {
        return;
    }
    let speed = get_speed();
    let scaled = (ms as f64 / speed) as DWORD;
    (std::mem::transmute::<u64, unsafe extern "system" fn(DWORD)>(orig))(scaled);
}

unsafe extern "system" fn hooked_sleep_ex(ms: DWORD, alertable: BOOL) -> DWORD {
    log_once_if_needed();
    let orig = ORIG_SLEEPEX.load(Ordering::Relaxed);
    if orig == 0 {
        return 0;
    }
    let speed = get_speed();
    let scaled = (ms as f64 / speed) as DWORD;
    (std::mem::transmute::<u64, unsafe extern "system" fn(DWORD, BOOL) -> DWORD>(orig))(scaled, alertable)
}

unsafe extern "system" fn hooked_wait_for_single_object(h: *mut c_void, ms: DWORD) -> DWORD {
    log_once_if_needed();
    let orig = ORIG_WFSO.load(Ordering::Relaxed);
    if orig == 0 {
        return 0;
    }
    let speed = get_speed();
    let scaled = (ms as f64 / speed) as DWORD;
    (std::mem::transmute::<u64, unsafe extern "system" fn(*mut c_void, DWORD) -> DWORD>(orig))(h, scaled)
}

unsafe extern "system" fn hooked_wait_for_single_object_ex(h: *mut c_void, ms: DWORD, alertable: BOOL) -> DWORD {
    log_once_if_needed();
    let orig = ORIG_WFSOEX.load(Ordering::Relaxed);
    if orig == 0 {
        return 0;
    }
    let speed = get_speed();
    let scaled = (ms as f64 / speed) as DWORD;
    (std::mem::transmute::<u64, unsafe extern "system" fn(*mut c_void, DWORD, BOOL) -> DWORD>(orig))(h, scaled, alertable)
}

unsafe extern "system" fn hooked_wait_for_multiple_objects(
    count: DWORD,
    handles: *const *mut c_void,
    wait_all: BOOL,
    ms: DWORD,
) -> DWORD {
    log_once_if_needed();
    let orig = ORIG_WFMO.load(Ordering::Relaxed);
    if orig == 0 {
        return 0;
    }
    let speed = get_speed();
    let scaled = (ms as f64 / speed) as DWORD;
    (std::mem::transmute::<u64, unsafe extern "system" fn(DWORD, *const *mut c_void, BOOL, DWORD) -> DWORD>(orig))(
        count, handles, wait_all, scaled,
    )
}

unsafe extern "system" fn hooked_wait_for_multiple_objects_ex(
    count: DWORD,
    handles: *const *mut c_void,
    wait_all: BOOL,
    ms: DWORD,
    alertable: BOOL,
) -> DWORD {
    log_once_if_needed();
    let orig = ORIG_WFMOEX.load(Ordering::Relaxed);
    if orig == 0 {
        return 0;
    }
    let speed = get_speed();
    let scaled = (ms as f64 / speed) as DWORD;
    (std::mem::transmute::<u64, unsafe extern "system" fn(DWORD, *const *mut c_void, BOOL, DWORD, BOOL) -> DWORD>(orig))(
        count, handles, wait_all, scaled, alertable,
    )
}

unsafe extern "system" fn hooked_set_timer(
    hwnd: *mut c_void,
    n_id: usize,
    elapse: DWORD,
    timer_proc: *mut c_void,
) -> usize {
    log_once_if_needed();
    let orig = ORIG_SETTIMER.load(Ordering::Relaxed);
    if orig == 0 {
        return 0;
    }
    let speed = get_speed();
    let scaled = (elapse as f64 / speed) as DWORD;
    (std::mem::transmute::<u64, unsafe extern "system" fn(*mut c_void, usize, DWORD, *mut c_void) -> usize>(orig))(
        hwnd, n_id, scaled, timer_proc,
    )
}

unsafe extern "system" fn hooked_time_set_event(
    delay: DWORD,
    resolution: DWORD,
    time_proc: *mut c_void,
    user: usize,
    event: DWORD,
) -> DWORD {
    log_once_if_needed();
    let orig = ORIG_TIMESETEVENT.load(Ordering::Relaxed);
    if orig == 0 {
        return 0;
    }
    let speed = get_speed();
    let scaled = (delay as f64 / speed) as DWORD;
    (std::mem::transmute::<u64, unsafe extern "system" fn(DWORD, DWORD, *mut c_void, usize, DWORD) -> DWORD>(orig))(
        scaled, resolution, time_proc, user, event,
    )
}

unsafe extern "system" fn hooked_set_waitable_timer(
    timer: *mut c_void,
    due_time: *const i64,
    period: i32,
    completion_routine: *mut c_void,
    arg: *mut c_void,
    resume: BOOL,
) -> BOOL {
    log_once_if_needed();
    let orig = ORIG_SETWAITABLETIMER.load(Ordering::Relaxed);
    if orig == 0 {
        return 0;
    }
    if due_time.is_null() {
        return 0;
    }
    let speed = get_speed();
    let scaled = (*due_time as f64 / speed) as i64;
    (std::mem::transmute::<u64, unsafe extern "system" fn(*mut c_void, *const i64, i32, *mut c_void, *mut c_void, BOOL) -> BOOL>(orig))(
        timer, &scaled, period, completion_routine, arg, resume,
    )
}

unsafe extern "system" fn hooked_set_waitable_timer_ex(
    timer: *mut c_void,
    due_time: *const i64,
    period: i32,
    completion_routine: *mut c_void,
    arg: *mut c_void,
    wake_context: *mut c_void,
    tolerable_delay: DWORD,
) -> BOOL {
    log_once_if_needed();
    let orig = ORIG_SETWAITABLETIMEREX.load(Ordering::Relaxed);
    if orig == 0 {
        return 0;
    }
    if due_time.is_null() {
        return 0;
    }
    let speed = get_speed();
    let scaled = (*due_time as f64 / speed) as i64;
    (std::mem::transmute::<u64, unsafe extern "system" fn(*mut c_void, *const i64, i32, *mut c_void, *mut c_void, *mut c_void, DWORD) -> BOOL>(orig))(
        timer, &scaled, period, completion_routine, arg, wake_context, tolerable_delay,
    )
}

// --- 时间读取类（锚定式 delta * 倍率） ---

unsafe extern "system" fn hooked_time_get_time() -> DWORD {
    log_once_if_needed();
    let orig = ORIG_TIMEGETTIME.load(Ordering::Relaxed);
    if orig == 0 {
        return 0;
    }
    let real = (std::mem::transmute::<u64, unsafe extern "system" fn() -> DWORD>(orig))() as u64;
    scale(&S_TIMEGETTIME, real, get_speed()) as DWORD
}

unsafe extern "system" fn hooked_get_message_time() -> i32 {
    log_once_if_needed();
    let orig = ORIG_GETMESSAGETIME.load(Ordering::Relaxed);
    if orig == 0 {
        return 0;
    }
    let real = (std::mem::transmute::<u64, unsafe extern "system" fn() -> i32>(orig))() as i64;
    scale(&S_GETMESSAGETIME, real as u64, get_speed()) as i32
}

unsafe extern "system" fn hooked_get_tick_count() -> DWORD {
    log_once_if_needed();
    let orig = ORIG_GETTICKCOUNT.load(Ordering::Relaxed);
    if orig == 0 {
        return 0;
    }
    let real = (std::mem::transmute::<u64, unsafe extern "system" fn() -> DWORD>(orig))() as u64;
    scale(&S_GETTICKCOUNT, real, get_speed()) as DWORD
}

unsafe extern "system" fn hooked_get_tick_count64() -> u64 {
    log_once_if_needed();
    let orig = ORIG_GETTICKCOUNT64.load(Ordering::Relaxed);
    if orig == 0 {
        return 0;
    }
    let real = (std::mem::transmute::<u64, unsafe extern "system" fn() -> u64>(orig))();
    scale(&S_GETTICKCOUNT64, real, get_speed())
}

unsafe extern "system" fn hooked_query_performance_counter(out: *mut i64) -> BOOL {
    log_once_if_needed();
    let orig = ORIG_QPC.load(Ordering::Relaxed);
    if orig == 0 {
        return 0;
    }
    let mut real: i64 = 0;
    let ret = (std::mem::transmute::<u64, unsafe extern "system" fn(*mut i64) -> BOOL>(orig))(&mut real);
    let scaled = scale(&S_QPC, real as u64, get_speed());
    *out = scaled as i64;
    ret
}

unsafe extern "system" fn hooked_get_system_time_as_file_time(out: *mut u64) {
    log_once_if_needed();
    let orig = ORIG_GSATFT.load(Ordering::Relaxed);
    if orig == 0 {
        return;
    }
    let mut real: u64 = 0;
    (std::mem::transmute::<u64, unsafe extern "system" fn(*mut u64)>(orig))(&mut real);
    let scaled = scale(&S_GSATFT, real, get_speed());
    *out = scaled;
}

unsafe extern "system" fn hooked_get_system_time_precise_as_file_time(out: *mut u64) {
    log_once_if_needed();
    let orig = ORIG_GSPAFT.load(Ordering::Relaxed);
    if orig == 0 {
        return;
    }
    let mut real: u64 = 0;
    (std::mem::transmute::<u64, unsafe extern "system" fn(*mut u64)>(orig))(&mut real);
    let scaled = scale(&S_GSPAFT, real, get_speed());
    *out = scaled;
}

// ============ Hook 安装（DllMain 同步） ============

/// 用 MH_CreateHookApiEx 创建 hook，记录 trampoline 与 target，返回是否成功。
unsafe fn create_hook(
    module: &str,
    proc: &'static [u8],
    detour: *mut c_void,
    orig_slot: &AtomicU64,
    targets: &mut [*mut c_void; 32],
    n: &mut usize,
) {
    let mut mbuf = [0u16; 64];
    let m = to_wide_static(module, &mut mbuf);
    let mut orig: *mut c_void = std::ptr::null_mut();
    let mut target: *mut c_void = std::ptr::null_mut();
    let st = MH_CreateHookApiEx(
        m,
        proc.as_ptr() as *const c_char,
        detour,
        &mut orig,
        &mut target,
    );
    if st == MH_OK {
        orig_slot.store(orig as u64, Ordering::Relaxed);
        targets[*n] = target;
        *n += 1;
    }
}

#[no_mangle]
pub extern "system" fn DllMain(
    _hinst: *mut c_void,
    reason: u32,
    _reserved: *mut c_void,
) -> i32 {
    if reason == 1 {
        // DLL_PROCESS_ATTACH：同步安装所有 hook（此时 Flash 尚未运行，无竞态）。
        unsafe {
            MH_Initialize();
            let mut targets: [*mut c_void; 32] = [std::ptr::null_mut(); 32];
            let mut n: usize = 0;

            create_hook("kernel32.dll", b"Sleep\0", hooked_sleep as *mut c_void, &ORIG_SLEEP, &mut targets, &mut n);
            create_hook("kernel32.dll", b"SleepEx\0", hooked_sleep_ex as *mut c_void, &ORIG_SLEEPEX, &mut targets, &mut n);
            create_hook("kernel32.dll", b"WaitForSingleObject\0", hooked_wait_for_single_object as *mut c_void, &ORIG_WFSO, &mut targets, &mut n);
            create_hook("kernel32.dll", b"WaitForSingleObjectEx\0", hooked_wait_for_single_object_ex as *mut c_void, &ORIG_WFSOEX, &mut targets, &mut n);
            create_hook("kernel32.dll", b"WaitForMultipleObjects\0", hooked_wait_for_multiple_objects as *mut c_void, &ORIG_WFMO, &mut targets, &mut n);
            create_hook("kernel32.dll", b"WaitForMultipleObjectsEx\0", hooked_wait_for_multiple_objects_ex as *mut c_void, &ORIG_WFMOEX, &mut targets, &mut n);
            create_hook("user32.dll", b"SetTimer\0", hooked_set_timer as *mut c_void, &ORIG_SETTIMER, &mut targets, &mut n);
            create_hook("winmm.dll", b"timeGetTime\0", hooked_time_get_time as *mut c_void, &ORIG_TIMEGETTIME, &mut targets, &mut n);
            create_hook("winmm.dll", b"timeSetEvent\0", hooked_time_set_event as *mut c_void, &ORIG_TIMESETEVENT, &mut targets, &mut n);
            create_hook("user32.dll", b"GetMessageTime\0", hooked_get_message_time as *mut c_void, &ORIG_GETMESSAGETIME, &mut targets, &mut n);
            create_hook("kernel32.dll", b"GetTickCount\0", hooked_get_tick_count as *mut c_void, &ORIG_GETTICKCOUNT, &mut targets, &mut n);
            create_hook("kernel32.dll", b"GetTickCount64\0", hooked_get_tick_count64 as *mut c_void, &ORIG_GETTICKCOUNT64, &mut targets, &mut n);
            create_hook("kernel32.dll", b"QueryPerformanceCounter\0", hooked_query_performance_counter as *mut c_void, &ORIG_QPC, &mut targets, &mut n);
            create_hook("kernel32.dll", b"GetSystemTimeAsFileTime\0", hooked_get_system_time_as_file_time as *mut c_void, &ORIG_GSATFT, &mut targets, &mut n);
            create_hook("kernel32.dll", b"GetSystemTimePreciseAsFileTime\0", hooked_get_system_time_precise_as_file_time as *mut c_void, &ORIG_GSPAFT, &mut targets, &mut n);
            create_hook("kernel32.dll", b"SetWaitableTimer\0", hooked_set_waitable_timer as *mut c_void, &ORIG_SETWAITABLETIMER, &mut targets, &mut n);
            create_hook("kernel32.dll", b"SetWaitableTimerEx\0", hooked_set_waitable_timer_ex as *mut c_void, &ORIG_SETWAITABLETIMEREX, &mut targets, &mut n);

            HOOK_COUNT.store(n as u64, Ordering::Relaxed);

            // 批量启用（一次性挂起线程 patch 全部，比逐个 EnableHook 更安全）。
            for i in 0..n {
                MH_QueueEnableHook(targets[i]);
            }
            MH_ApplyQueued();
        }
    }
    1 // TRUE
}

// ============ Win32 FFI 导入 ============

extern "system" {
    fn OpenFileMappingW(dwDesiredAccess: u32, bInheritHandle: i32, lpName: *const u16) -> *mut c_void;
    fn MapViewOfFile(
        hFileMappingObject: *mut c_void,
        dwDesiredAccess: u32,
        dwFileOffsetHigh: u32,
        dwFileOffsetLow: u32,
        dwNumberOfBytesToMap: usize,
    ) -> *mut c_void;
    fn CloseHandle(hObject: *mut c_void) -> i32;
}

const FILE_MAP_READ: u32 = 0x0004;
