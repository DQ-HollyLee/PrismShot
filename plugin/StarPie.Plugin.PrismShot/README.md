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

这是唯一一个可能**明确失败**的动作，原因值得说明白。

需求要的是「用 PrismShot 完成选区，让 StarPie 认这段区域里的字」。
而当前 StarPie SDK 对外部开放的 OCR 入口只有 `IHostScreenCaptureService.CaptureAndRecognize()` ——
它会**再弹一次 StarPie 自己的框选窗口**，也就是说用户要框两次，一次给 PrismShot、一次给 StarPie。
这显然不是需求要的样子。

真正需要的是「拿一张图、返回文字」这种形态的入口，它不在已发布的契约里。
于是这个动作采取的策略是：**按约定去试，试不到就说实话**。

- 宿主未来若实现了 `IHostOcrService`（提案见 [upstream/ocr-seam.md](upstream/ocr-seam.md)），
  插件**无需重新编译**就能直接认出并用上 —— 那一侧是反射探测，不是编译期引用
- 现在没有时，动作会把选区图保存下来，并返回一条清楚的提示，而不是假装成功
- 保存下来的图可以用 StarPie 自带的「截屏识字」打开识别，链路不断，只是多了半步手工

刻意**不做**的事：不写一个自己认为"大概能用"的替代 OCR 实现进去。
OCR 是 StarPie 的强项，插件重复实现它只会得到一份更差的结果，还把版本号绕晕。

## 已知边界

- PrismShot 常驻实例没在跑时，动作会尝试用宿主的 `Launch` 把它拉起来；启动本身失败会原话报出
- 若 PrismShot 因环境原因（远程桌面会话、锁屏、部分虚拟显卡）降级到 GDI 通道，
  图片仍然出得来，但那一张没有 HDR；插件会在宿主日志里留一条 `WARN`，带上当次走的通道
- 圈选模式输出 PNG —— 透明通道只有 PNG 装得下，此时 `format` 填别的值会被 PrismShot 侧忽略

## 许可

MIT，与 PrismShot 主仓库一致。
