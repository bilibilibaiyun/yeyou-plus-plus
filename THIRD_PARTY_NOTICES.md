# 第三方组件声明 (THIRD_PARTY_NOTICES)

本软件基于以下第三方组件构建，感谢原作者：

## CefSharp
- 用途：.NET Chromium 嵌入框架（WinForms 版）
- 版本：84.4.10
- 许可：MIT License
- 来源：https://github.com/cefsharp/CefSharp

## Chromium Embedded Framework (CEF)
- 用途：浏览器内核运行时（Chromium 84.0.4147.105）
- 版本：84.4.1
- 许可：BSD-style License（Copyright (c) 2008-2020, Marshall A. Greenblatt）
- 来源：https://bitbucket.org/chromiumembedded/cef
- 注：Chromium 本身使用众多第三方库，完整列表见 https://chromium.googlesource.com/chromium/src/+/84.0.4147.105/LICENSE

## Adobe Flash Player (PPAPI)
- 用途：Flash 内容播放插件（pepflashplayer.dll，34.0.0.330 x64）
- 版权：Copyright (c) 1996-2020 Adobe. All rights reserved.
- 注：该二进制为 Adobe 公司财产，随安装包分发仅供个人使用；Adobe 已于 2020 年底停止对 Flash Player 的支持。

## Newtonsoft.Json
- 用途：JSON 序列化
- 版本：13.0.3
- 许可：MIT License
- 来源：https://github.com/JamesNK/Newtonsoft.Json

## NAudio
- 用途：无（本组件为历史遗留依赖说明，当前未使用）

## MinHook
- 用途：变速齿轮 DLL 的函数 Hook 基础库（早期方案，现方案为自实现 IAT Hook）
- 许可：BSD 2-Clause License
- 来源：https://github.com/TsudaKageworthy/minhook

---

以上各组件的许可副本可在其官方仓库获取。若本声明有遗漏或错误，请通过 Issue 联系修正。
