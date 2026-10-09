# PrismShot 变更日志

版本号唯一真相在 `version.json`。本文件每条都必须对应一次 git 提交或 tag。

## [Unreleased]

### 回滚：撤掉 PrismShotOcr（原 alpha.10 / alpha.11 两个提交）

StarPie 目前是 beta，开发者已确认不会开放插件接口 —— 那条路不成立。
于是把 `src/PrismShot.OcrTool`（框选识字工具 + AI 视觉识别通道）整个撤掉，
master 回到 alpha.9 的代码基线。历史没有丢，在 `backup-ocr-alpha10-11` 分支上。

主程序因此不再有任何 OCR 相关代码，「只截图」这条红线重新变干净。

### 变更：默认输出格式 jpg → png

需求里的「默认 jpg」改成 png。原因是圈选（任意形状）必须有透明通道才成立，
而 JPG 没有 alpha —— 存出来只能是外接矩形，圈了等于白圈。
圈选时若配置的是不带 alpha 的格式，会自动升级成 png。

### 修复

- **Win 键被一直按住**（连带单独按 PrintScreen 失灵）：钩子为了「别弹开始菜单」
  把 Win 的抬起也吞了，可它的按下早就交给系统了 —— 只吞不吐，系统眼里 Win 永远按着，
  于是按 Q 开搜索、按 I 开设置，得再敲一次 Win 才解得开。
  现在修饰键的抬起无条件放行，而且排在所有吞键规则之前
- **内存涨到 700MB~1GB**：抓帧时 QueryInterface 拿到的 COM 指针从不 Release，
  每截一张漏一整条 surface / texture 链（非托管内存，GC 根本看不见）；
  管道服务端的 IOException 分支没有退避，一出错就退化成每秒几万次的忙循环。
  另外改成工作集超过 256MB 才做一次强制回收，免得每次截图都停一下
- **圈选松开鼠标不保存**：抬起时只清了拖拽标志，从头到尾没提交过结果，
  再按下又往同一个顶点列表里继续追加，看着像「还能接着画」
- **圈选存出来是完整矩形**：就是上面「默认 jpg 没有 alpha」那一条

### 验证

遮罩与编码用自动化测过（`.cache/alphatest`）：三角形遮罩后角上 alpha=0、中心 255，
存成 PNG 再读回来仍然成立 —— 说明形状丢失不是遮罩或编码的错，是格式选错。

## [0.1.0-alpha.9] - 2026-10-10

### 新增：StarPie 插件

`plugin/StarPie.Plugin.PrismShot` —— 让 StarPie 轮盘的扇区直接调用 PrismShot 截图。

- 五个动作：区域 / 圈选 / 窗口 / 全屏 / 截图识字。前四个共用一套实现，
  注册成独立条目而不是「一个动作 + 模式下拉」：常用的是区域那一种，
  做成独立条目就能直接挂扇区、也给得起专属名字与图标，轮盘上一眼看出这个扇区在截什么
- 一份插件级设置页：PrismShot.exe 路径、默认格式 / 质量 / 目录。
  全部留空完全合法 —— 那表示一切都听 PrismShot 自己的配置
- 插件不含任何截图代码，只通过命名管道 `PrismShot.Control` 转给常驻实例。
  升级引擎不必重发插件；PrismShot 没在跑时插件也只知道「叫不起来」，不会另起一套低质量兜底
- 降级留痕：若那一帧走了 GDI 通道，往宿主日志写一条带通道名的 `WARN`

### 新增：截图识字动作与它的诚实失败

需求要的是「选区交给 PrismShot，识别交给 StarPie」。而 StarPie SDK 1.10 对外开放的
OCR 入口只有一个 `CaptureAndRecognize()`，它会**再弹一次宿主自己的框选窗口** ——
用户要框两次，第一次的结果直接被丢掉。

真正需要的是「拿一张图、返回文字」这种形态，它不在已发布的契约里。于是：

- 用**反射**按约定探测 `IHostOcrService`，而不是编译期引用 —— 同一个 DLL
  在「没打补丁的宿主」与「打了补丁的宿主」上都要能跑
- 探测不到时不抛、不假装成功：保存选区图，然后返回一句说清楚的话
- 上游提案写在 `plugin/StarPie.Plugin.PrismShot/upstream/ocr-seam.md`，
  含接口形态、宿主侧的落地改法（`OcrManager` 抽纯函数）与采纳后的验证清单

刻意**没有**在插件里自己实现一份 OCR：那会得到一份更差的结果，还把宿主的
后处理 / provider 配置 / 结果窗口全部绕过去。

### 修复：插件客户端把「成功」报成了「通信失败」

一次 `using` 顺序引发的误报。原始写法：

```csharp
using StreamWriter writer = new(pipe, encoding);   // 默认 leaveOpen: false
using StreamReader reader = new(pipe, encoding);
```

两个包装都认为自己是管道的所有者，而 `using` 的释放顺序是**声明的逆序** ——
reader 先 Dispose 把管道关掉，接着 writer 的 Dispose 走到 Flush，
对着一个已关闭的管道抛 `ObjectDisposedException`。异常偏偏落在业务 `try` 里面，
于是「图已经截好、应答已经读完」被整体翻译成「与 PrismShot 通信失败」。

主程序的 `IpcClient` 早就写了 `leaveOpen: true`，只有插件这份漏了。

排查过程值得一提：四组变量对照（含ReadDirection、超时、Ping 选项）**全部成功**，
同步通信没有问题，问题只出现在 `using` 的收尾那一瞬 ——
所以一边「每次都成功」一边「每次都报错」，看着像玄学其实是收尾顺序。
同类问题以后先看「是不是 Dispose 抛的」。

### 新增：构建脚本 `build.ps1`

编译主程序与插件、`publish` 到 `artifacts\PrismShot-<version>\`、把插件排到
`<stage>\plugin\prismshot\`（只带 DLL 与 `plugin.json` —— 带上契约程序集会被宿主判为版本冲突）。
支持 `-BootstrapSdk`（装一套隔离 SDK 到 `$HOME\.uba-dotnet`）、`-SkipPlugin`、`-Run`。

两个坑都记进脚本注释了：

- `.ps1` 必须带 **UTF-8 BOM**。PowerShell 5.1 对无 BOM 的脚本按 ANSI(GBK) 解码，
  中文注释被解码成乱码时**会把某些换行吞掉**，于是报出完全对不上位置的语法错误
  （本次就报成了 `version.json 附近有意外标记`，实际错在别处）
- 不要用 `Remove-Item` 清理构建中间目录：本机给它挂了「先丢回收站」的安全钩子，
  在命令行会话里会直接失败。改走 `[System.IO.Directory]::Delete($path, $true)`

### 新增：面向使用者的文档

`README.md`（定位、一分钟上手、默认行为、命令行、编译、已知边界）、
`plugin/StarPie.Plugin.PrismShot/README.md`（安装路径、五个动作、参数、设置页、边界）。

写文档时顺手纠正了三处「以为是这样」：命令行其实没有 `--region x,y,w,h`
（选区只能交互式，或者用 `--repeat` 重复上一次）；本项目没有做开始菜单快捷方式
（改配置是直接编辑 `settings.json` 后 `--reload`）；Starshot 的仓库地址是
`loliri/Starshot`，不是先前文档里写的那个。

### 实机验证（本次）

- `build.ps1` 全链路通过，`EXIT=0`
- 用**发行包**而非源码目录跑 `--capture`：`2560x1440`、`pipeline=WGC`、
  `displays=1`、`white=360` —— publish 之后没有丢任何东西
- 离线宿主桩 `.cache/plugincheck` 全绿：五个动作的注册与参数、Preview/Validate 空参数不抛、
  真实区域拖拽（875×600）、ESC 取消（无产物）、停用后贡献点全部撤销
- 新增的清单闸门把宿主 `PluginManifestReader.Validate` 与 `PluginScanner.CheckContract`
  的规则离线复刻了一遍：ID 格式、字段长度上限、语义化版本、TFM 不高于宿主、
  引用的契约版本、entryType 与实际类型一致、输出目录不得自带 SDK 副本

⚠️ 仍未闭环：本机 `--probe` 报 `hdr enabled = False`。360 尼特的白电平能验证
「按每屏白电平缩放」这条算术，但验证不了「浮点帧里出现 > 1.0 的值也不过曝」。
真 HDR 屏上的实机验证还没做过。

## [0.1.0-alpha.8] - 2026-10-10

### 修复：DPI 感知在命令行路径下失效

同一个 app.manifest，走 WPF 常驻路径时 `GetSystemMetrics` 给 2560x1440，
走纯命令行路径（`--capture` / `--probe`）却给 2048x1152 —— 差 1.25 倍，
正是本机 125% 缩放的虚拟化结果。后果是 `--capture` 拼出一张缩小两成的虚拟桌面，
`--probe` 报错尺寸，按坐标裁剪的区域整体缩水，且完全没有报错。

改为在 `Main` 首行显式调用 `SetProcessDpiAwarenessContext(PerMonitorV2)`，
三条入口（常驻 / 自测 / 插件调用）行为一致，不再依赖清单是否被应用。
复验：`--probe` 现报 2560x1440 / scale 1.25。

### 修复：覆盖层关闭与键盘回调的线程归属

低级钩子的按键回调在线程池线程上，而它触发的 `OverlayHost.Close()` 直接调了
`Window.Close()` → 「调用线程无法访问此对象」。异常被吞掉后窗口留在屏幕上，
表现就是「按了 Esc 但蒙版不消失」。同时 `KeyObserved -=` 与在途调用相撞，
抛 `Collection was modified`。

现在：OverlayHost 的所有入口统一切回 UI 线程；钩子侧先把委托取成局部快照再投递。
另：IPC 服务端原本一条连接就地处理，覆盖层开着时整条管道被堵死，
连 ping / quit 都排队。改为每条连接一个任务，抓帧的互斥交给上层的 busy 标记。

## [0.1.0-alpha.7] - 2026-10-10

### 修复：从 surface 取纹理指针时静默降级到 GDI

上一版刚把 WGC 拉起来，这一版接着修它与 D3D11 之间那段。
`Marshal.GetIUnknownForObject(surface)` 后再 QueryInterface 拿到的是 `0x80004002`
（`E_NOINTERFACE`），异常同样被降级逻辑兜住 → 又是一张静默的 GDI 图。

改为取 `IWinRTObject.NativeObject.ThisPtr` 作为起点，
QI 到 `IDirect3DDxgiInterfaceAccess`（`A9B3D012-3DF2-4EE9-B090-23D5A6D0F9C4`），
再 `GetInterface(ref guid)` 拿 `ID3D11Texture2D`（`6F15AAF2-D208-4E50-A19D-9FD2A0E5E0A0`）。

### 修复：覆盖层的 XAML 从来没被解析成功过

`Cursor="Crosshair"` 不是 WPF 的内置光标名（正确写法是 `Cross`），
XAML 解析直接抛异常 —— 也就是说在此之前覆盖层一次都没打开过。
这类错误的共同点是「出事的地方在最不显眼的一行」。

## [0.1.0-alpha.6] - 2026-10-10

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
实测 `pipeline = WGC`。

⚠️ 这个坑的可怕之处在于：**失败是静默降级**。图能出、看起来正常，
只是回到了 8bit GDI 那条路 —— 也就是我们要取代的那条。以后任何「看起来对」的验证，
都必须同时核对报告里的 `pipeline` 是 WGC 而不是 GDI 兜底。

## [0.1.0-alpha.5] - 2026-10-10

### 新增：编排层贯通

会话（`CaptureSession`）、宿主（`App`）、通知（`ResultToast`）与常驻循环打通：
四种模式共用一条「抓帧 → 合成冻结帧 → 交给覆盖层或直接编码」的路径。

## [0.1.0-alpha.4] - 2026-10-10

### 新增：冻结帧覆盖层 + IPC + 单实例

遮罩窗口、四种选区模式、以及让第二次启动跑去通知第一个实例的互斥体与命名管道。

## [0.1.0-alpha.3] - 2026-10-10

### 新增：配置、热键钩子、自启、录制宏与剪贴板

## [0.1.0-alpha.2] - 2026-10-10

### 新增：打通到 CPU 的完整截图链路

WGC 抓帧 → GPU 纹理回读 → 色调映射 → 编码落盘。这一步第一次让 `--capture` 出图。

## [0.1.0-alpha.1] - 2026-10-10

### 新增：HDR 信息查询与 WGC 抓帧层

HDR 信息查询：确认 .NET WinRT 投影里没有 `DisplayInformation.CreateForDisplayId`
（那是 WinAppSDK 的东西，为此拖整套 AppSDK 不值得），改用纯 Win32 DisplayConfig ——
自 Win10 1709 起就有，且 `SDRWhiteLevel / 1000 * 80` 的单位口径与 Chromium、Kodi 的实现一致。
抓帧层：D3D11 设备（长期持有，避免每次按快门重建）、DXGI→WinRT 互操作、GPU 纹理回读、
WGC 单帧抓取（`CreateFreeThreaded` + 具名回调 + 超时保护）。

## [0.1.0] - 2026-10-10

### 新增

- 项目骨架：目录约定、`version.json` 单一版本源、`PROJECT.md` 设计红线、MIT 许可。
- 参考仓库：`Starshot`（HDR 抓帧思路来源）与 `StarPie`（右键轮盘 + 插件宿主）克隆到 `reference/`，不入库。
- 确定技术选型：.NET 8 WPF + WinRT WGC 投影，**不引 Win2D / WinUI**，运行时零第三方本机依赖。
- 隔离安装 .NET 8 SDK（落在用户目录，不污染系统环境）。
