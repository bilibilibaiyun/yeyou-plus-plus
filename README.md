# 页游++ (YeyouPlusPlus)

一个专注于 **Flash 页游** 的 Windows 桌面浏览器：内置真·Flash 插件、游戏变速齿轮、快捷入口、自动缓存清理，开箱即用。

![Version](https://img.shields.io/badge/version-2.0.3-blue) ![Platform](https://img.shields.io/badge/platform-Windows%2010%2B%20x64-lightgrey)

## 特性

- **真 Flash 内核**：内置 CEF 84（Chromium 84）+ Adobe Flash PPAPI 插件（34.0.0.330 x64），完美运行 4399 等平台的 Flash 页游（如造梦西游系列）
- **游戏变速**：内置变速齿轮（IAT Hook，安全无注入崩溃），0.5x ~ 5x 自由调速
- **主页快捷入口**：主页 5 个快捷按钮，一键进入常玩的游戏网址；按钮图标自动抓取对应网站的 favicon
- **必应搜索**：主页大搜索框，网址直达 / 关键词必应搜索
- **自动缓存清理**：实时监测缓存体积，达到临界值提醒、达到危险值自动清理，告别「缓存爆满」
- **手动清缓存**：一键清除 cookies 与缓存
- **网页缩放**：50% ~ 200% 页面缩放
- **收藏夹**：收藏常用页面
- **检查更新**：启动自动检查，发现新稳定版时红点提醒；支持在设置中浏览全部版本并覆盖安装（配置与数据保留）
- **自定义路径**：数据存储路径、下载保存路径均可自定义
- **硬件加速**：默认启用 GPU 合成渲染
- **深色标题信息**：窗口标题栏实时显示渲染帧率与缓存用量

## 下载

前往 [Releases](https://github.com/bilibilibaiyun/yeyou-plus-plus/releases) 页面下载 `YeyouPlusPlus_x.y.z_x64_Setup.exe`。

> 标注「稳定版」的 Release 推荐普通用户下载；标注「测试版」的为开发中的版本，可能不稳定。

## 系统要求

- Windows 10 / 11 x64
- 无需安装 .NET 运行时（安装包自包含）

## 从源码构建

```powershell
# 需要 .NET SDK 8+（通过 ReferenceAssemblies 包编译 net462 目标）
cd cs/src
dotnet build YeyouPlusPlus.csproj -c Release -p:Platform=x64
```

- 唯一运行时依赖：[CefSharp.WinForms 84.4.10](https://www.nuget.org/packages/CefSharp.WinForms)（NuGet 自动还原，含 CEF 二进制）
- Flash 插件（pepflashplayer.dll）不随源码分发，安装包内包含

## 安装 / 卸载

- 默认安装到 `D:\页游++`（可自定义），可选创建桌面快捷方式
- **卸载时会彻底清除本机数据**：安装目录、缓存、配置、收藏、快捷入口、自定义数据目录

## 目录结构

```
cs/src/            C# WPF 源码（net462 + CefSharp 84）
installer/         Inno Setup 打包脚本
assets/icon/       应用图标
speedhack/         变速齿轮 DLL 源码（Rust，IAT Hook）
```

## 技术栈

- C# WPF（.NET Framework 4.6.2）+ CefSharp 84（Chromium 84）
- 变速齿轮：Rust 编写的 IAT Hook DLL（hook QueryPerformanceCounter / GetTickCount / timeGetTime）
- 打包：Inno Setup 7

## 许可

本项目代码以 [MIT](LICENSE) 许可发布。第三方组件见 [THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES.md)。
