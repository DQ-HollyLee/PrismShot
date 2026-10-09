# PrismShot 变更日志

版本号唯一真相在 `version.json`。本文件每条都必须对应一次 git 提交或 tag。

## [Unreleased]

### 已实机验证（HDR 环境）

本机为 Win11 build 26300 + 单屏 2048x1152 + **HDR 已开、SDR 白电平 360 尼特**。
在这个白电平下，浮点帧里一个普通的白色像素值是 4.5 ——
按 1.0 去当 SDR 解读就是教科书级的过曝，而原生工具正是栽在这里。
实测 `--capture` 出来的图曝光正确，验证了「按每屏白电平回缩放」这条路径成立。

### 进行中

- HDR 信息查询：确认 .NET WinRT 投影里没有 `DisplayInformation.CreateForDisplayId`（那是 WinAppSDK 的东西，
  为此拖整套 AppSDK 不值得），改用纯 Win32 DisplayConfig —— 自 Win10 1709 起就有，
  且 `SDRWhiteLevel / 1000 * 80` 的单位口径与 Chromium、Kodi 的实现一致。
- 抓帧层：D3D11 设备（长期持有，避免每次按快门重建）、DXGI→WinRT 互操作、GPU 纹理回读、
  WGC 单帧抓取（`CreateFreeThreaded` + 具名回调 + 超时保护）。

## [0.1.0] - 2026-10-10

### 新增

- 项目骨架：目录约定、`version.json` 单一版本源、`PROJECT.md` 设计红线、MIT 许可。
- 参考仓库：`Starshot`（HDR 抓帧思路来源）与 `StarPie`（右键轮盘 + 插件宿主）克隆到 `reference/`，不入库。
- 确定技术选型：.NET 8 WPF + WinRT WGC 投影，**不引 Win2D / WinUI**，运行时零第三方本机依赖。
- 隔离安装 .NET 8 SDK（`.cache/dotnet` 之外，落在用户目录，不污染系统环境）。
