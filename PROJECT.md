# PrismShot（棱采）· 项目总则与文件清单

一个「像 Windows 自带截图工具一样不存在，但能正确截 HDR」的 Windows 截图小工具。

## 一句话定位

**先是一个完整的、够好用的截图工具；HDR 正确只是它最硬的那张牌，而不是全部。**

## 设计红线（改动前先读）

1. **存在感必须极低**：默认无托盘图标、无任务栏按钮、无主窗口。唯一常驻的是一根监听两个热键的低级键盘钩子线程和一个命名管道服务端。
2. **开机即用**：默认写 HKCU `Run` 自启；执行 `--elevate` 之后改由**「最高权限」计划任务**
   接管（注册表 `Run` 让位，否则开机会起两个实例），之后用户永远不需要打开它的界面。
   之所以要有这条提权路径：UIPI 会让**前台为管理员窗口时**普通权限进程的低级键盘钩子
   一次都不被调用（任务管理器聚焦时 `Win+Shift+S` 落到系统原生截图），
   热键接管这条红线在那种前台下直接失效。详见 CHANGELOG alpha.14。
3. **只截图**：不录屏、不做 OCR、不做编辑器、不做贴图批注。需要 OCR 时由 StarPie 插件接过它产出的图片。
4. **多输出格式，默认 PNG**：png / jpg / bmp / tif / gif 任选。默认取 PNG 是因为它无损，
   且是唯一装得下圈选（任意形状）透明通道的格式 —— JPG 没有 alpha，
   会把多边形外面那圈一起存进去，圈了也白圈。
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
│       │   ├── Capture/      # WGC 抓帧、监视器 HDR 探测、GDI 兜底、引擎选择
│       │   ├── Diagnostics/  # 内存报告、--memtest、抓帧流水线探针 --pipetest
│       │   ├── Imaging/      # 色调映射、格式编码、色域转换
│       │   ├── Interop/      # P/Invoke 与 COM 互操作
│       │   ├── Ipc/          # 命名管道协议 / 服务端 / 客户端 / 单实例
│       │   ├── Platform/     # DPI 感知、录制宏
│       │   ├── System/       # 热键、自启 / 提权、剪贴板、模拟按键
│       │   └── MemoryPressure.cs  # LOH 压缩式回收（唯一能把内存真正还回去的入口）
│       └── Ui/               # 覆盖层、工具栏、缩略图提示
├── plugin/
│   └── StarPie.Plugin.PrismShot/   # 给 StarPie 轮盘用的插件
│       ├── plugin.json             # 宿主清单（必须与 DLL 一起分发）
│       ├── README.md               # 安装与参数
│       └── upstream/               # 给 StarPie 上游的提案（OCR 接缝）
├── docs/                     # 设计说明、排障手册
├── reference/                # 参考仓库克隆（被 .gitignore 忽略，不入版本库）
│   ├── Starshot/
│   └── StarPie/
└── .cache/                   # 中间产物（被 .gitignore 忽略）
    └── plugincheck/          # 离线宿主桩：不装 StarPie 也能验插件契约
```

## 验证手段

- `PrismShot.exe --probe`：打印每块屏的尺寸 / HDR / SDR 白电平，并写一份报告
- `PrismShot.exe --capture`：单次抓虚拟桌面，报告里带 `pipeline` —— **只看图是不够的，
  必须确认这一行是 `WGC` 而不是 `GDI 兜底`**
- `PrismShot.exe --memtest`：内存问题**唯一**的入口。它把任务管理器那一列拆成
  `ws / managed / committed / frag / loh / gdi / user`，先分清涨的是托管堆、
  大对象堆碎片，还是非托管的东西，再动手 —— 别凭一个总数猜
- `PrismShot.exe --pipetest`：抓帧流水线按 `device / frame / texture / readback /
  tonemap / dispose / collect` 分段记内存，以及 `--d3dmin` 用裸 `D3D11CreateDevice`
  跑参数矩阵。用来回答"这内存是我们自己用的，还是运行时/驱动的固定成本"
- `.cache/cliptest`：链 `ClipboardWriter` / `Raster` / `ImageWriter` 的真实源码，
  写进剪贴板再按接收方的方式读回来查 —— BITMAPV5HEADER 的 124 字节布局与四个通道掩码
  写错了不报错、只会让对方画出怪东西，**只能靠这种写读闭环抓住**
- `.cache/plugincheck`：复刻宿主的安装闸门（清单字段 / 入口类型 / 契约引用 / TFM）
  与运行时契约（五个动作的注册、参数校验、Preview、ESC 取消、停用后留有什么），
  还能在 PrismShot 常驻实例在线时跑通真实截图链路。不需要装 StarPie
- `.\build.ps1`：编译 + publish + 排发行包到 `artifacts\PrismShot-<version>\`

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
                  └─ 8. 按配置编码（默认 PNG）→ 保存 / 或写剪贴板 / 或返回给调用方
```

## 待办

- [x] 项目骨架、版本管理、技术选型
- [x] 抓帧引擎（WGC float + GDI 兜底）
- [x] 色调映射 + 多格式编码
- [x] 覆盖层 UI（四种选区模式）
- [x] 热键接管 + 自启
- [x] 单实例 IPC + CLI + StarPie 插件
- [x] 录制宏
- [x] 构建脚本 `build.ps1`（publish + 排发行包）
- [x] 面向使用者的文档（README / 插件 README / OCR 上游提案）
- [x] 实机验证：本机 2560×1440 @125%，`hdr enabled = True`、10bit、SDR 白电平 360 尼特、
      `capture as float = True`、`pipeline = WGC`
      （早期文档里写的 `hdr enabled = False` 是过时的旧记录，已更正）
- [ ] **实机验证：浮点帧里真的出现 >1.0 的像素时不过曝**。屏已开 HDR、浮点通道已跑通，
      360 尼特的白电平也验证了「按每屏白电平回缩放」这条算术；但屏幕上多数内容仍落在
      SDR 范围，还没抓到一帧真正的高亮内容。需要放一段 HDR 视频或开一张 HDR 图再截一次
- [ ] **实机验证 `--elevate`（管理员常驻）**：注册计划任务要调 `schtasks.exe`，
      在受限环境里被安全策略拦下，未能自测。需要在普通命令行跑一次 `PrismShot.exe --elevate`
      （弹一次 UAC），再验证两件事：① 任务管理器聚焦时 `Win+Shift+S` 不再弹原生截图；
      ② 重启 / 注销后仍以管理员起、且不再弹 UAC
- [ ] 在真实 StarPie 1.8.0 主程序里加载插件（当前只有离线宿主桩的验证）
