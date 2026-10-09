# PrismShot（棱采）· 项目总则与文件清单

一个「像 Windows 自带截图工具一样不存在，但能正确截 HDR」的 Windows 截图小工具。

## 一句话定位

**先是一个完整的、够好用的截图工具；HDR 正确只是它最硬的那张牌，而不是全部。**

## 设计红线（改动前先读）

1. **存在感必须极低**：默认无托盘图标、无任务栏按钮、无主窗口。唯一常驻的是一根监听两个热键的低级键盘钩子线程和一个命名管道服务端。
2. **开机即用**：安装时写 HKCU 自启，之后用户永远不需要打开它的界面。
3. **只截图**：不录屏、不做 OCR、不做编辑器、不做贴图批注。需要 OCR 时由 StarPie 插件接过 here 产出的图片。
4. **默认输出 JPG**：为了直接喂给 StarPie 的 AI OCR 接口，SDR 的 8bit JPG 就是它们吃得最稳的格式。
5. **HDR 路径绕开 GDI**：任何 `BitBlt` / `CopyFromScreen` / `Graphics.CopyFromScreen` 都拿的是 DWM 为兼容 GDI 而降级过的 8bit SDR 副本。本项目主路径走 WGC（Windows Graphics Capture）取 `R16G16B16A16Float` scRGB。
6. **必须有兜底**：WGC 失败（远程桌面、锁屏、部分虚拟显卡、驱动异常）时自动降级到 GDI，功能不残废，只是这一张没有 HDR。
7. **零第三方本机依赖**：不打包 native dll，不引 Win2D / WinUI。运行时依赖只有系统自带的 d3d11.dll / user32.dll / gdi32.dll。

## 目录约定

```
hdrshot-screenshot/
├── version.json              # 单一版本源（版本号唯一真相）
├── CHANGELOG.md              # 每次提交都要在这里留痕
├── README.md                 # 面向使用者
├── LICENSE                   # MIT
├── PROJECT.md                # 本文件：规范 + 待办
├── build.ps1                 # 编译、产出发行包
├── src/
│   ├── PrismShot.sln
│   └── PrismShot/            # 主程序（.NET 8 WPF + WinRT 投影）
│       ├── Assets/           # 图标
│       ├── Core/             # 与 UI 无关的引擎层
│       │   ├── Capture/      # WGC 抓帧、监视器 HDR 探测、GDI 兜底
│       │   ├── Imaging/      # 色调映射、格式编码、色域转换
│       │   ├── Interop/      # P/Invoke 与 COM 互操作
│       │   └── System/       # 热键、自启、单实例、IPC、录制宏、配置
│       └── UI/               # 覆盖层、工具栏、缩略图提示
├── plugin/
│   └── StarPie.Plugin.PrismShot/   # 给 StarPie 轮盘用的插件
├── docs/                     # 设计说明、排障手册
├── reference/                # 参考仓库克隆（被 .gitignore 忽略，不入版本库）
│   ├── Starshot/
│   └── StarPie/
└── .cache/                   # 中间产物（被 .gitignore 忽略）
    └── dotnet/               # 构建脚本自己下载的隔离 .NET SDK
```

## 版本管理纪律

上一个 TTS 项目吃过「改到最后没有备份」的亏，本项目执行以下硬规则：

1. `git init` 在写第一行代码之前完成。
2. 每完成一个**可独立验证**的功能点立即提交，不允许攒到「整体完成再提交」。
3. 里程碑（能跑通一条主链路）打 `vX.Y.Z` tag。
4. `version.json` 与 `CHANGELOG.md` 必须与最新 tag 一致。
5. 任何**破坏性重构**之前先打个 `pre-refactor-XXX` tag，方便回退。
6. 不做 `--force` 推送，不做改写历史的 rebase。

## 数据流（主路径）

```
热键 (Win+Shift+S / PrintScreen)
   └─> LL 键盘钩子拦截 (不再派发给 Explorer)
        └─> 通知常驻 UI 线程
             ├─ 1. 枚举所有显示器，查 DisplayInformation.GetAdvancedColorInfo()
             ├─ 2. 每个 HDR 显示器用 R16G16B16A16Float，SDR 显示器用 R8G8B8A8 抓帧
             ├─ 3. D3D11 回读到 CPU
             ├─ 4. HDR 帧按该屏 SdrWhiteLevelInNits 做 scRGB→sRGB 色调映射，得到 8bit BGRA
             ├─ 5. 按虚拟桌面坐标合成一张冻结帧
             ├─ 6. 全屏/此屏模式：直接走第 8 步
             └─ 7. 区域模式：显示覆盖层，等用户选区（Esc 取消 / Enter 或松手确认）
                  └─ 8. 按配置编码（默认 JPG）→ 保存 / 或写剪贴板 / 或返回给调用方
```

## 待办

- [x] 项目骨架、版本管理、技术选型
- [ ] 抓帧引擎（WGC float + GDI 兜底）
- [ ] 色调映射 + 多格式编码
- [ ] 覆盖层 UI（四种选区模式）
- [ ] 热键接管 + 自启
- [ ] 单实例 IPC + CLI + StarPie 插件
- [ ] 录制宏
- [ ] 实机验证（SDR 环境 + HDR 环境）
