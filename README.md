<div align="center">

<img src="assets/icon/app-256.png" width="128" alt="页游++ 图标"/>

# 页游++（YeyouPlusPlus）

**Windows Flash 页游浏览器 —— 真·Flash 内核 + 游戏变速齿轮，开箱即用**

![Version](https://img.shields.io/badge/version-2.0.9-blue) ![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-lightgrey) ![Flash](https://img.shields.io/badge/Flash-34.0.0.330%20PPAPI-red) ![License](https://img.shields.io/badge/license-MIT-green)

主页：快捷入口 + 必应搜索，一目了然

<img src="docs/images/screenshot-home.png" width="860" alt="页游++ 主页"/>

游戏内变速（5x）与「影子」小号多开面板

<img src="docs/images/screenshot-gameplay.jpg" width="860" alt="页游++ 游戏变速与影子多开"/>

</div>

## ✨ 特性

- **真 Flash 内核**：内置 CEF 84（Chromium 84）+ Adobe Flash PPAPI 插件（34.0.0.330 x64），无需任何配置即可畅玩 4399 等平台的经典 Flash 页游（如造梦西游系列）
- **游戏变速齿轮**：基于 MinHook 内联挂钩，锚定式缩放 6 个时间读取函数（timeGetTime、GetTickCount、GetTickCount64、GetSystemTimeAsFileTime、GetSystemTimePreciseAsFileTime、GetMessageTime），让 Flash 游戏逻辑变速，主界面与游戏副本内均稳定生效，0.5x ~ 5x 自由调速
- **影子（小号多开）**：一键克隆当前页面到独立实例，Cookie / 缓存 / 会话完全隔离，大小号同屏双开
- **远程同屏游戏（手机盒子）**：桌面端生成加密联机码，手机安装配套 Android 客户端（手机盒子）输入联机码即可连接，实时投屏 + 虚拟按键操作，实现「一人键盘 + 一人手机」的同屏双人游戏。按键大小 / 位置 / 透明度 / 映射键均可自定义，手机端内置「检测更新 / 选择版本」。支持**局域网直连**与**跨网络联机**（内置 EasyTier P2P 组网，手机在外也能连家里电脑；多协议 + 公共节点中继兜底，校园网等对称 NAT / 防火墙环境也能穿透）

## 📥 下载

前往 [Releases](https://github.com/bilibilibaiyun/yeyou-plus-plus/releases) 页面下载 `YeyouPlusPlus_x.y.z_x64_Setup.exe`。

> 标注「**稳定版**」的 Release 推荐普通用户下载；标注「**测试版**」的为开发中的版本，可能不稳定。

## ⚡ 变速齿轮原理

变速模块以 Rust 编写，编译为独立 DLL 随主程序注入浏览器渲染进程：

1. 通过 `MH_CreateHookApiEx` 对 `kernel32` / `winmm` / `user32` 的 **6 个时间读取函数**建立内联跳板；
2. 时间读取类函数返回「锚定式缩放时间」：`base_hook + (now - base_real) × speed`，倍率变化时重新锚定，时间轴平滑无跳变；
3. 以锚定式缩放时间读取函数驱动 Flash 游戏逻辑，让游戏真正跑满目标倍率。

Flash 副本内通过 `GetProcAddress` 动态调用时间函数、绕过导入表，因此 IAT Hook 方案在副本内失效；内联挂钩直接改写函数序言，与调用方式无关，彻底解决该问题。

## 💻 系统要求

- Windows 10 / 11 x64
- 无需安装 .NET 运行时（安装包自包含）

## 🔨 从源码构建

```powershell
# 需要 .NET SDK 8+（通过 ReferenceAssemblies 包编译 net462 目标）
cd cs/src
dotnet build YeyouPlusPlus.csproj -c Release -p:Platform=x64
```

- 唯一运行时依赖：[CefSharp.WinForms 84.4.10](https://www.nuget.org/packages/CefSharp.WinForms)（NuGet 自动还原，含 CEF 二进制）
- 变速齿轮 DLL：`cd speedhack && cargo build --release`（Rust 工具链）
- Flash 插件（pepflashplayer.dll）不随源码分发，安装包内包含

## 📦 安装 / 卸载

- 默认安装到 `D:\YeyouPlusPlus`（可自定义），可选创建桌面快捷方式
- **卸载时会彻底清除本机数据**：安装目录、缓存、配置、收藏、快捷入口、自定义数据目录

## 🧱 目录结构

```
cs/src/            C# WPF 源码（net462 + CefSharp 84）
speedhack/         变速齿轮 DLL 源码（Rust + MinHook 内联挂钩）
android/           手机盒子 Android 客户端源码（Kotlin）
installer/         Inno Setup 打包脚本
assets/icon/       应用图标
docs/images/       截图
```

## 🛠 技术栈

- C# WPF（.NET Framework 4.6.2）+ CefSharp 84（Chromium 84）
- 变速齿轮：Rust + [MinHook](https://github.com/TsudaKageyu/minhook) 内联挂钩（hook 6 个时间读取函数）
- 手机盒子：Android 原生 Kotlin（MediaCodec 硬解 + SurfaceView）+ 自研 UDP RTP 推流 + OpenH264 软编码
- 打包：Inno Setup 7

## ⚠️ 免责声明

Adobe Flash Player 已于 2020 年底停止官方支持，本项目仅供个人学习与怀旧用途。Flash 插件版权归 Adobe 所有，不随源码分发；请勿将本项目用于任何商业用途或访问您无权访问的内容。

## 📄 许可

本项目代码以 [MIT](LICENSE) 许可发布。第三方组件见 [THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES.md)。
