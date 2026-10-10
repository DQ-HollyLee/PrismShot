# StarPie.Plugin.PrismShot

让 StarPie 轮盘（悬浮菜单）直接调用 [PrismShot](../README.md) 截图：在 HDR 屏幕上颜色不过曝，
在 SDR 屏幕上与系统工具一致。截图结果可以写文件、可以进剪贴板，也可以交给 StarPie 识别文字。

插件本身**不含任何截图代码** —— 它通过命名管道把请求转给 PrismShot 常驻实例，
由那边的 WGC 抓帧引擎完成实际工作。这样做的好处是：升级引擎不必重发插件，
而 PrismShot 不在运行时，插件也只知道「叫不起来」，不会自己另起一套低质量的兜底。

## 前提条件

- StarPie ≥ **1.8.0-beta.1**（插件契约 1.6，运行时兼容到 SDK 1.10）
- PrismShot 已安装并至少启动过一次（它会写自启，之后常驻）
- Windows 10 1809 及以上 / x64

## 安装

StarPie 用一个「目录 + `plugin.json`」认插件：目录里只要有这两样东西，
它就会在启动时被扫瞄发现。所以安装就是拷两个文件：

1. 编译：`dotnet build -c Release -p:Platform=x64`（或直接取发行包里的 `plugin` 目录）
2. 在 `%LOCALAPPDATA%\StarPie\plugin-data\` 下建一个子目录，目录名随意，例如 `prismshot`
3. 把 `StarPie.Plugin.PrismShot.dll` 和 `plugin.json` 拷进它（**不要**带 `StarPie.Plugin.Abstractions.dll`，
   契约程序集由宿主统一提供，多带一份会被判为版本冲突；`deps.json` / `pdb` 也不必拷）
4. 重启 StarPie → 插件管理页里会出现「PrismShot HDR 截图」，新发现的插件默认**停用**，启用即可

> 便携版 StarPie 的可写宿主区在程序目录下的 `plugin-data\`，只有这一处路径不同，其余步骤一样。

## 提供的动作

在轮盘扇区里选择「插件」类型，就能看到这五个动作：

| 动作 | ID | 说明 |
| --- | --- | --- |
| PrismShot 区域截图 | `captureRegion` | 拖拽矩形选区。最常用的一个 |
| PrismShot 任意圈选 | `captureFreeform` | 套索画出任意形状，输出带透明的 PNG |
| PrismShot 窗口截图 | `captureWindow` | 单击一个窗口，截取它的完整矩形 |
| PrismShot 全屏截图 | `captureScreen` | 整个虚拟桌面（或光标所在的那一块屏） |
| PrismShot 截图识字 | `captureOcr` | 框选后交给 StarPie 的 OCR 引擎认字 |

四个截图动作共用这三个可选参数；**留空一律跟随插件设置页，再没有就跟随 PrismShot 自己的配置**：

| 参数 | 类型 | 取值 |
| --- | --- | --- |
| `format` | 枚举 | `jpg`（默认） / `png` / `bmp` / `tif` / `gif` |
| `quality` | 数字 | 1–100，只对 JPG 生效 |
| `directory` | 目录 | 留空用 PrismShot 的输出目录（默认 图片库\PrismShot） |

## 插件设置页

插件管理卡片上的「设置」里可以改四个默认值：`PrismShot.exe` 路径、默认格式、
默认 JPG 质量、默认保存目录。全部留空是完全合法的 —— 那表示一切都听 PrismShot 的。

`PrismShot.exe` 路径一般用不着填：插件会按顺序查注册表卸载项、
`PRIMSSHOT_HOME` 环境变量、开始菜单快捷方式，最后是几个常见安装位置。
只有便携版或装在非标准位置时才需要显式指定。

## 关于「截图识字」

分工是：**PrismShot 负责框选，StarPie 负责认字。**

宿主 SDK 对外开放的 OCR 入口只有一个 `IHostScreenCaptureService.CaptureAndRecognize()`，
它会**再弹一次 StarPie 自己的框选窗口** —— 用户得框两次，第一次框的结果直接被丢掉。
这不是能凑合的取舍，是明显的缺陷。

于是插件把 OCR 那一整段**照搬**了进来：`upstream/OcrManager.cs` 与 StarPie 的
`WinPieGestures/OcrManager.cs` 逐行同源，改动只有三处，且都是为了"离开宿主还能活"：

| 上游写的是 | 插件换成 | 为什么必须是它 |
| --- | --- | --- |
| `ConfigManager.CurrentConfig.OcrConfig` | 读 `%LOCALAPPDATA%\StarPie\config.json` | 宿主内部类型不可引用；读盘反而保证了**与宿主同一份配置** —— 用户在 StarPie 里改引擎，插件立刻跟着变 |
| `AppLogger.LogXxx` | `OcrLog`（绑到插件日志） | 同上 |
| `I18n.T(...)` | 字面中文 | 插件拿不到宿主的词条表 |

被**删掉**的只有两处，正是"框选"这一半：`ShowSettingsDialog` 与
`StartCaptureAndRecognize`（后者会弹宿主的 `ScreenSnipWindow`）—— 它们的位置由 PrismShot 顶上。
识别本体一个字没动：四个引擎（本地离线 / AI 视觉 / 自定义 HTTP / 云端）、
几何重排、文案后处理、识别前的图像预处理全在。

配置完全跟着宿主走。本机实测：StarPie 里配的是 `Provider=Ai` + DeepSeek 视觉模型，
插件读同一个 `config.json`，动作跑起来日志就是
`engine='🤖 AI 视觉大模型 (deepseek-v4-flash-vision-exp)'`。
**用户不需要在插件里再配一遍密钥。**

宿主若哪天把 `IHostOcrService` 补进 SDK（提案见 [upstream/ocr-seam.md](upstream/ocr-seam.md)），
插件**无需重新编译**就会改走宿主的引擎 —— 那一侧是反射探测。
重复实现永远不如唯一真相，照搬的那份本来就只是代班。

## 已知边界

- PrismShot 常驻实例没在跑时，动作会尝试用宿主的 `Launch` 把它拉起来；启动本身失败会原话报出
- 若 PrismShot 因环境原因（远程桌面会话、锁屏、部分虚拟显卡）降级到 GDI 通道，
  图片仍然出得来，但那一张没有 HDR；插件会在宿主日志里留一条 `WARN`，带上当次走的通道
- 圈选模式输出 PNG —— 透明通道只有 PNG 装得下，此时 `format` 填别的值会被 PrismShot 侧忽略

## 许可

MIT，与 PrismShot 主仓库一致。
