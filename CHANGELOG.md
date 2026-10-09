# PrismShot 变更日志

版本号唯一真相在 `version.json`。本文件每条都必须对应一次 git 提交或 tag。

## [Unreleased]

### 修复：WGC 抓帧静默降级到 GDI

第一次实机跑通后复查日志，发现 `pipeline` 其实是 GDI、WGC 早就失败了：

```
[WRN] \\.\DISPLAY1：WGC 抓帧失败，改用 GDI。
      Failed to create a CCW for object of type 'System.__ComObject'
      for interface with IID 'A37624AB-8D5F-4650-9D3E-9EAE3D9BC670'
```

`A37624AB-8D5F-4650-9D3E-9EAE3D9BC670` 正是 WinRT `IDirect3DDevice` 的 IID。
根因是 `CreateDirect3D11DeviceFromDXGIDevice` 的结果被 `Marshal.GetObjectForIUnknown`
包成了裸 `__ComObject`（Vortice 的泛型助手内部也是这条），拿去当接口用没问题，
可一旦再交回 COM（`Direct3D11CaptureFramePool.CreateFreeThreaded` 就要交一次），
CLR 会尝试为它造 CCW 然后失败。

改用 `WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(ptr)` 走 C#/WinRT 自己的 RCW 工厂后
实测 `pipeline = WGC`，浮点帧到手，360 尼特白电平下的曝光正确。

⚠️ 这个坑的可怕之处在于：**失败是静默降级**。图能出、看起来正常，
只是回到了 8bit GDI 那条路 —— 也就是我们要取代的那条。以后任何「看起来对」的验证，
都必须同时核对日志里的 `通道=` 是 WGC 而不是 GDI。

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
