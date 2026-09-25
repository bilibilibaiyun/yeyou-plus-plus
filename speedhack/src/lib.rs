//! 内置变速齿轮（IAT hook 版）：注入到 Flash 子进程后，
//! 修改 pepflashplayer.dll 导入表（IAT）里的时间函数指针，
//! 只影响 Flash 自己的时间读取，原子替换指针、无代码 patch、无线程竞态。
//!
//! 倍率通过命名共享内存 `Local\YeyouSpeedHack`（8 字节 f64）从主程序下发。

use std::sync::atomic::{AtomicPtr, AtomicU64, Ordering};
use std::sync::Mutex;

use winapi::shared::minwindef::{BOOL, DWORD, TRUE};
use winapi::um::handleapi::CloseHandle;
use winapi::um::libloaderapi::{GetModuleHandleW, GetProcAddress};
use winapi::um::memoryapi::{MapViewOfFile, OpenFileMappingW, FILE_MAP_READ};
use winapi::um::profileapi::{QueryPerformanceCounter, QueryPerformanceFrequency};
use winapi::um::synchapi::{Sleep, SleepEx};
use winapi::um::sysinfoapi::{GetSystemTimeAsFileTime, GetTickCount, GetTickCount64};
use winapi::um::timeapi::timeGetTime;
use winapi::um::winnt::HANDLE;

// ============ 日志 ============

fn log_line(msg: &str) {
    // 写到程序目录 speedhack.log，便于诊断（ppapi 子进程无 stderr）。
    let _ = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open("speedhack.log")
        .and_then(|mut f| {
            use std::io::Write;
            writeln!(f, "{}", msg)
        });
}

// ============ 共享内存倍率 ============

static SHARED_VIEW: AtomicPtr<f64> = AtomicPtr::new(std::ptr::null_mut());
static SHARED_HANDLE: AtomicU64 = AtomicU64::new(0);

fn to_wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

fn ensure_shared() -> *mut f64 {
    let p = SHARED_VIEW.load(Ordering::Relaxed);
    if !p.is_null() {
        return p;
    }
    unsafe {
        let name = to_wide("Local\\YeyouSpeedHack");
        let h: HANDLE = OpenFileMappingW(FILE_MAP_READ, 0, name.as_ptr());
        if h.is_null() {
            return std::ptr::null_mut();
        }
        let view = MapViewOfFile(h, FILE_MAP_READ, 0, 0, 8) as *mut f64;
        if view.is_null() {
            CloseHandle(h);
            return std::ptr::null_mut();
        }
        SHARED_HANDLE.store(h as u64, Ordering::Relaxed);
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

// ============ 时间缩放（毫秒域，锚定式） ============

#[derive(Clone, Copy)]
struct TimeScaler {
    base_real_ms: u64,
    base_scaled_ms: u64,
    speed: f64,
}

impl TimeScaler {
    const fn new() -> Self {
        Self {
            base_real_ms: 0,
            base_scaled_ms: 0,
            speed: 1.0,
        }
    }

    fn scale(&mut self, real_ms: u64) -> u64 {
        let speed = get_speed();
        if self.base_real_ms == 0 {
            self.base_real_ms = real_ms;
            self.base_scaled_ms = real_ms;
            self.speed = speed;
            return real_ms;
        }
        if speed != self.speed {
            let elapsed = (real_ms.wrapping_sub(self.base_real_ms)) as f64;
            let current_scaled = self.base_scaled_ms + (elapsed * self.speed) as u64;
            self.base_real_ms = real_ms;
            self.base_scaled_ms = current_scaled;
            self.speed = speed;
        }
        let elapsed = (real_ms.wrapping_sub(self.base_real_ms)) as f64;
        self.base_scaled_ms + (elapsed * speed) as u64
    }
}

// 每个时间源用独立缩放器（各时间 API 的基准/回绕特性不同，混用同一锚点会因
// wrapping_sub 产生巨大值导致时间爆炸）。
static SCALER_QPC: Mutex<TimeScaler> = Mutex::new(TimeScaler::new());
static SCALER_GTC: Mutex<TimeScaler> = Mutex::new(TimeScaler::new());
static SCALER_GTC64: Mutex<TimeScaler> = Mutex::new(TimeScaler::new());
static SCALER_TGT: Mutex<TimeScaler> = Mutex::new(TimeScaler::new());
static SCALER_GSATFT: Mutex<TimeScaler> = Mutex::new(TimeScaler::new());

fn scale_with(scaler: &Mutex<TimeScaler>, real_ms: u64) -> u64 {
    match scaler.try_lock() {
        Ok(mut s) => s.scale(real_ms),
        Err(_) => real_ms, // 锁被占用（极短暂），直接透传本此次调用。
    }
}

// ============ 原函数地址（IAT 解析时保存） ============

static ORIG_QPC: AtomicU64 = AtomicU64::new(0);
static ORIG_GTC: AtomicU64 = AtomicU64::new(0);
static ORIG_GTC64: AtomicU64 = AtomicU64::new(0);
static ORIG_TGT: AtomicU64 = AtomicU64::new(0);
static ORIG_GSATFT: AtomicU64 = AtomicU64::new(0);
static ORIG_SLEEP: AtomicU64 = AtomicU64::new(0);
static ORIG_SLEEPEX: AtomicU64 = AtomicU64::new(0);

static FREQ: AtomicU64 = AtomicU64::new(0);

fn qpc_freq() -> u64 {
    let f = FREQ.load(Ordering::Relaxed);
    if f != 0 {
        return f;
    }
    unsafe {
        let mut v: i64 = 0;
        QueryPerformanceFrequency(&mut v as *mut i64 as *mut winapi::um::winnt::LARGE_INTEGER);
        if v > 0 {
            FREQ.store(v as u64, Ordering::Relaxed);
        }
        v as u64
    }
}

// ============ Hook 函数（仅 Flash 调用走这里） ============

unsafe extern "system" fn hooked_qpc(out: *mut i64) -> BOOL {
    let orig = ORIG_QPC.load(Ordering::Relaxed);
    if orig == 0 {
        return QueryPerformanceCounter(out as *mut winapi::um::winnt::LARGE_INTEGER);
    }
    let mut counter: i64 = 0;
    let ret = (std::mem::transmute::<u64, unsafe extern "system" fn(*mut i64) -> BOOL>(orig))(&mut counter);
    let freq = qpc_freq();
    if freq > 0 {
        // 计数域 → 毫秒域缩放（u128 避免溢出），再转回计数域。
        let real_ms = (counter as u128 * 1000 / freq as u128) as u64;
        let scaled_ms = scale_with(&SCALER_QPC, real_ms);
        let v = scaled_ms as u128 * freq as u128 / 1000;
        *out = (v.min(i64::MAX as u128)) as i64;
    } else {
        *out = counter;
    }
    ret
}

unsafe extern "system" fn hooked_get_tick_count() -> DWORD {
    let orig = ORIG_GTC.load(Ordering::Relaxed);
    if orig == 0 {
        return GetTickCount();
    }
    let real = (std::mem::transmute::<u64, unsafe extern "system" fn() -> DWORD>(orig))() as u64;
    scale_with(&SCALER_GTC, real) as DWORD
}

unsafe extern "system" fn hooked_get_tick_count64() -> u64 {
    let orig = ORIG_GTC64.load(Ordering::Relaxed);
    if orig == 0 {
        return GetTickCount64();
    }
    let real = (std::mem::transmute::<u64, unsafe extern "system" fn() -> u64>(orig))();
    scale_with(&SCALER_GTC64, real)
}

unsafe extern "system" fn hooked_time_get_time() -> u32 {
    let orig = ORIG_TGT.load(Ordering::Relaxed);
    if orig == 0 {
        return timeGetTime();
    }
    let real = (std::mem::transmute::<u64, unsafe extern "system" fn() -> u32>(orig))() as u64;
    scale_with(&SCALER_TGT, real) as u32
}

unsafe extern "system" fn hooked_get_system_time_as_file_time(out: *mut u64) {
    let orig = ORIG_GSATFT.load(Ordering::Relaxed);
    if orig == 0 {
        GetSystemTimeAsFileTime(out as *mut winapi::shared::minwindef::FILETIME);
        return;
    }
    let mut real: u64 = 0;
    (std::mem::transmute::<u64, unsafe extern "system" fn(*mut u64)>(orig))(&mut real);
    // FILETIME = 100ns 单位 → 毫秒缩放 → 转回 100ns。
    let real_ms = real / 10000;
    let scaled_ms = scale_with(&SCALER_GSATFT, real_ms);
    *out = scaled_ms * 10000;
}

unsafe extern "system" fn hooked_sleep(ms: DWORD) {
    let orig = ORIG_SLEEP.load(Ordering::Relaxed);
    if orig == 0 {
        Sleep(ms);
        return;
    }
    let speed = get_speed();
    let scaled = if speed > 0.0 && speed.is_finite() && speed > 1.0 {
        (ms as f64 / speed) as DWORD
    } else {
        ms
    };
    (std::mem::transmute::<u64, unsafe extern "system" fn(DWORD)>(orig))(scaled);
}

unsafe extern "system" fn hooked_sleep_ex(ms: DWORD, alertable: BOOL) -> DWORD {
    let orig = ORIG_SLEEPEX.load(Ordering::Relaxed);
    if orig == 0 {
        return SleepEx(ms, alertable);
    }
    let speed = get_speed();
    let scaled = if speed > 0.0 && speed.is_finite() && speed > 1.0 {
        (ms as f64 / speed) as DWORD
    } else {
        ms
    };
    (std::mem::transmute::<u64, unsafe extern "system" fn(DWORD, BOOL) -> DWORD>(orig))(scaled, alertable)
}

// ============ PE 解析：找 IAT 槽位（纯 RVA，内存解析） ============

/// 从内存里读一个以 NUL 结尾的 ASCII 字符串。
unsafe fn cstr_at(p: *const u8) -> String {
    let mut end = p;
    while *end != 0 {
        end = end.add(1);
    }
    let len = end as usize - p as usize;
    String::from_utf8_lossy(std::slice::from_raw_parts(p, len)).into_owned()
}

/// 在目标模块的导入表里找 `import_dll!func_name` 对应的 IAT 槽位地址。
///
/// 关键：module_base 是**内存基址**，所有 RVA 直接用 `base + rva` 访问，
/// 不要再做「RVA→文件偏移」的换算（文件偏移只对磁盘文件有意义）。
unsafe fn find_iat_slot(module_base: usize, import_dll: &str, func_name: &str) -> Option<*mut usize> {
    let base = module_base as *const u8;
    if *base != b'M' || *base.add(1) != b'Z' {
        return None;
    }
    let e_lfanew = base.add(0x3C).cast::<u32>().read() as usize;
    let pe = base.add(e_lfanew);
    if *pe != b'P' || *base.add(e_lfanew + 1) != b'E' {
        return None;
    }
    let opt_off = e_lfanew + 24;
    let magic = base.add(opt_off).cast::<u16>().read();
    let pe32plus = magic == 0x20b;
    // 数据目录起始：PE32+ 在 opt_off+112，PE32 在 opt_off+96。
    let dd_off = opt_off + if pe32plus { 112 } else { 96 };
    // Import 目录是数据目录**索引 1**，即 dd_off + 8（索引 0 是 Export）。
    let import_rva = base.add(dd_off + 8).cast::<u32>().read() as usize;
    if import_rva == 0 {
        return None;
    }

    // 遍历 IMAGE_IMPORT_DESCRIPTOR（每个 20 字节，全 0 结束）。
    let mut desc = import_rva;
    for _ in 0..64 {
        let int_rva = base.add(desc).cast::<u32>().read() as usize;      // OriginalFirstThunk (INT)
        let name_rva = base.add(desc + 12).cast::<u32>().read() as usize; // DLL 名 RVA
        let ft_rva = base.add(desc + 16).cast::<u32>().read() as usize;   // FirstThunk (IAT)
        if name_rva == 0 && ft_rva == 0 {
            return None;
        }

        let dll_name = cstr_at(base.add(name_rva));
        let matched = dll_name.eq_ignore_ascii_case(import_dll)
            || (dll_name.starts_with("API-MS-Win-Core-") && import_dll.eq_ignore_ascii_case("kernel32.dll"));

        if matched && ft_rva != 0 {
            // 函数名表用 INT（OriginalFirstThunk），INT 为 0 时回退用 IAT。
            let name_table = if int_rva != 0 { int_rva } else { ft_rva };
            let mut idx = 0usize;
            loop {
                let thunk = base.add(name_table + idx * 8).cast::<u64>().read();
                if thunk == 0 {
                    break; // 数组结束
                }
                // 名称导入：最高位为 0，低 31 位是 RVA 指向 IMAGE_IMPORT_BY_NAME（前 2 字节是 Hint）。
                if thunk & 0x8000_0000_0000_0000 == 0 {
                    let fname_rva = (thunk & 0xFFFF_FFFF) as usize;
                    let fname = cstr_at(base.add(fname_rva + 2));
                    if fname.eq_ignore_ascii_case(func_name) {
                        // IAT 槽位地址 = base + ft_rva + idx*8（内存地址）。
                        return Some(base.add(ft_rva + idx * 8) as *mut usize);
                    }
                }
                idx += 1;
                if idx > 1024 {
                    break;
                }
            }
        }
        desc += 20;
    }
    None
}

unsafe fn patch_iat(slot: *mut usize, hook_fn: usize) -> Option<usize> {
    // IAT 是数据页，写 8 字节对齐指针是原子的；用 VirtualProtect 临时开写。
    use winapi::um::memoryapi::VirtualProtect;
    let mut old_prot: DWORD = 0;
    if VirtualProtect(slot as *mut winapi::ctypes::c_void, 8, 0x40 /*PAGE_EXECUTE_READWRITE*/, &mut old_prot) == 0 {
        return None;
    }
    let orig = *slot;
    *slot = hook_fn;
    let mut tmp: DWORD = 0;
    VirtualProtect(slot as *mut winapi::ctypes::c_void, 8, old_prot, &mut tmp);
    Some(orig)
}

// ============ 安装（IAT hook） ============

unsafe fn find_flash_module() -> Option<usize> {
    // 在本进程里枚举模块，找 pepflashplayer.dll 基址。
    let kernel32 = GetModuleHandleW(to_wide("kernel32.dll").as_ptr());
    if kernel32.is_null() {
        return None;
    }
    let create_snap: extern "system" fn(DWORD, DWORD) -> *mut winapi::ctypes::c_void = {
        let f = GetProcAddress(kernel32, b"CreateToolhelp32Snapshot\0".as_ptr() as *const _);
        if f.is_null() { return None; }
        std::mem::transmute(f)
    };
    #[repr(C)]
    struct MODULEENTRY32W {
        dwSize: DWORD,
        th32ModuleID: DWORD,
        th32ProcessID: DWORD,
        glblcntUsage: DWORD,
        proccntUsage: DWORD,
        modBaseAddr: *mut u8,
        modBaseSize: DWORD,
        hModule: *mut winapi::ctypes::c_void,
        szModule: [u16; 256],
        szExePath: [u16; 260],
    }
    let module32_first: extern "system" fn(*mut winapi::ctypes::c_void, *mut MODULEENTRY32W) -> BOOL = {
        let f = GetProcAddress(kernel32, b"Module32FirstW\0".as_ptr() as *const _);
        if f.is_null() { return None; }
        std::mem::transmute(f)
    };
    let module32_next: extern "system" fn(*mut winapi::ctypes::c_void, *mut MODULEENTRY32W) -> BOOL = {
        let f = GetProcAddress(kernel32, b"Module32NextW\0".as_ptr() as *const _);
        if f.is_null() { return None; }
        std::mem::transmute(f)
    };

    let snap = create_snap(0x8, 0);
    if snap.is_null() {
        return None;
    }
    let mut me: MODULEENTRY32W = std::mem::zeroed();
    me.dwSize = std::mem::size_of::<MODULEENTRY32W>() as DWORD;
    let mut found = None;
    if module32_first(snap, &mut me) != 0 {
        loop {
            let name: String = String::from_utf16_lossy(
                &me.szModule[..me.szModule.iter().position(|&c| c == 0).unwrap_or(0)],
            );
            if name.eq_ignore_ascii_case("pepflashplayer.dll") {
                found = Some((me.modBaseAddr as *mut u8) as usize);
                break;
            }
            if module32_next(snap, &mut me) == 0 {
                break;
            }
        }
    }
    CloseHandle(snap);
    found
}

unsafe fn install_iat_hooks() -> Result<String, String> {
    // 原生 QPC 频率先取好。
    let _ = qpc_freq();

    let flash_base = find_flash_module().ok_or("pepflashplayer.dll 模块未找到")?;

    let mut hooked: Vec<&str> = Vec::new();
    if let Some(slot) = find_iat_slot(flash_base, "kernel32.dll", "QueryPerformanceCounter") {
        if let Some(orig) = patch_iat(slot, hooked_qpc as usize) {
            ORIG_QPC.store(orig as u64, Ordering::Relaxed);
            hooked.push("QueryPerformanceCounter");
        }
    }
    if let Some(slot) = find_iat_slot(flash_base, "kernel32.dll", "GetTickCount") {
        if let Some(orig) = patch_iat(slot, hooked_get_tick_count as usize) {
            ORIG_GTC.store(orig as u64, Ordering::Relaxed);
            hooked.push("GetTickCount");
        }
    }
    if let Some(slot) = find_iat_slot(flash_base, "kernel32.dll", "GetTickCount64") {
        if let Some(orig) = patch_iat(slot, hooked_get_tick_count64 as usize) {
            ORIG_GTC64.store(orig as u64, Ordering::Relaxed);
            hooked.push("GetTickCount64");
        }
    }
    if let Some(slot) = find_iat_slot(flash_base, "winmm.dll", "timeGetTime") {
        if let Some(orig) = patch_iat(slot, hooked_time_get_time as usize) {
            ORIG_TGT.store(orig as u64, Ordering::Relaxed);
            hooked.push("timeGetTime");
        }
    }
    // 副本内计时常用 GetSystemTimeAsFileTime（100ns 时间戳）+ Sleep 等待，
    // 之前只 hook 三个函数导致「主界面变速有效、进入副本失效」。
    if let Some(slot) = find_iat_slot(flash_base, "kernel32.dll", "GetSystemTimeAsFileTime") {
        if let Some(orig) = patch_iat(slot, hooked_get_system_time_as_file_time as usize) {
            ORIG_GSATFT.store(orig as u64, Ordering::Relaxed);
            hooked.push("GetSystemTimeAsFileTime");
        }
    }
    if let Some(slot) = find_iat_slot(flash_base, "kernel32.dll", "Sleep") {
        if let Some(orig) = patch_iat(slot, hooked_sleep as usize) {
            ORIG_SLEEP.store(orig as u64, Ordering::Relaxed);
            hooked.push("Sleep");
        }
    }
    if let Some(slot) = find_iat_slot(flash_base, "kernel32.dll", "SleepEx") {
        if let Some(orig) = patch_iat(slot, hooked_sleep_ex as usize) {
            ORIG_SLEEPEX.store(orig as u64, Ordering::Relaxed);
            hooked.push("SleepEx");
        }
    }
    if hooked.is_empty() {
        return Err("没有任何 IAT 槽位被 patch".into());
    }
    Ok(hooked.join(","))
}

// ============ DllMain ============

static INIT_DONE: AtomicU64 = AtomicU64::new(0);

#[no_mangle]
pub extern "system" fn DllMain(_hinst: *mut core::ffi::c_void, reason: DWORD, _reserved: *mut core::ffi::c_void) -> BOOL {
    if reason == 1 {
        if INIT_DONE.swap(1, Ordering::SeqCst) == 0 {
            std::thread::spawn(|| unsafe {
                // 等 Flash 模块加载完成（ppapi 进程加载 pepflashplayer 后再 hook）。
                // 等待时间放宽到 5 分钟：进入副本时 Flash 插件进程可能较慢创建/加载，
                // 过早放弃会永久错过 hook（表现为变速失效）。
                let mut flash_found = false;
                for _ in 0..300 {
                    if find_flash_module().is_some() {
                        flash_found = true;
                        break;
                    }
                    // 低频轮询（1 秒）：Toolhelp 模块快照会短暂挂起本进程全部线程，
                    // 高频（100ms）轮询会让 Chromium 渲染进程反复被挂起，
                    // 合成帧无法提交，表现为浏览器区域永久空白。
                    std::thread::sleep(std::time::Duration::from_millis(1000));
                }
                if !flash_found {
                    log_line("[speedhack] 等待 pepflashplayer.dll 超时");
                    return;
                }
                match install_iat_hooks() {
                    Ok(list) => log_line(&format!("[speedhack] IAT hook 成功: {}", list)),
                    Err(e) => log_line(&format!("[speedhack] IAT hook 失败: {}", e)),
                }
            });
        }
    }
    TRUE
}
