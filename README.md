# PrismShot（棱采）

一个「像 Windows 自带截图工具一样不存在，但能在 HDR 屏上截出正确颜色」的 Windows 截图工具。

MIT 协议。HDR 处理思路参考了 [Starshot](https://github.com/loliri/Starshot)（用 WGC 取浮点帧，
而不是让 DWM 把 HDR 降级成 8bit SDR），低占用与可扩展的取向参考了
[StarPie](https://github.com/Star-Pie/StarPie)。两者都是 MIT。

## 它想解决的问题

Windows 自带截图工具，以及绝大多数依赖 `BitBlt` / `Graphics.CopyFromScreen` 的第三方工具，
拿到的是 **DWM 为兼容 GDI 而生成的 8bit SDR 副本**。在开着 HDR 的屏上：
SDR 白电平被调到 360 尼特（而不是标准的 80）时，浮点帧里一个普通白色像素的值是 4.5，
按 SDR 的 1.0 去解读就是教科书级的过曝 —— 白底界面整个糊成一团，
这正是很多人的日常。

PrismShot 走的是另一条路：**WGC（Windows.Graphics.Capture）取 `R16G16B16A16Float` scRGB 帧**，
按这块屏自己的 SDR 白电平把 scRGB 映射回 sRGB，再输出 8bit 图片。
口径与 Chromium、Kodi 一致：`SdrWhiteLevel / 1000 * 80` 尼特。

## 一分钟上手

1. 下载发行包并解压到任意位置（建议 `%LOCALAPPDATA%\Programs\PrismShot`）
2. 运行 `PrismShot.exe` —— 它不会弹任何窗口，只会把自己写进开机自启
3. 从此按 **`Win+Shift+S`** 区域截图、**`PrintScreen`** 全屏截图
4. **（建议）执行一次 `PrismShot.exe --elevate`** —— 弹一次 UAC，之后常驻在管理员权限下

第 4 步不是可选项里的"高级玩法"，它修的是一个会稳定复现的问题：
**焦点在任务管理器上时按 `Win+Shift+S`，弹出来的是 Windows 原生截图工具，再按一次才好。**
原因是 UIPI（用户界面特权隔离）—— 前台是管理员窗口时，系统根本不会去调用普通权限进程
装的那根键盘钩子。以管理员常驻之后，任何前台窗口都拦不住它。详见下面「已知边界」。

它就这样消失在日常里了：没有托盘图标、没有任务栏按钮、没有主窗口。
常驻的全部东西是一根监听两个热键的低级键盘钩子线程和一个命名管道服务端。

要改配置就直接编辑 `%LOCALAPPDATA%\PrismShot\settings.json`
（便携模式是程序目录下的 `Data\settings.json`），改完跑一次 `PrismShot.exe --reload` 让它重新读。
刻意不给常驻 UI —— "能被看见"本身就与它的目标冲突。

便携模式：在程序目录里放一个空的 `portable.txt`，配置、日志、默认截图就全部落在同目录的 `Data\` 下，
整个文件夹拷进 U 盘就能带走，不必先「卸载」掉写进 AppData 的那些东西。

## 默认行为

| 项 | 默认值 |
| --- | --- |
| 区域截图 | `Win+Shift+S` |
| 全屏截图 | `PrintScreen` |
| 窗口截图 / 重复上次选区 | 不绑定（自己配） |
| 输出格式 | PNG（无损）—— jpg / bmp / tif / gif 也都支持；圈选只能用 PNG，因为它需要透明通道 |
| 保存位置 | 图片库\PrismShot |
| 剪贴板 | 同时放一份位图，可直接粘贴进聊天框 |
| 截图后提示 | 右下角浮出缩略图，3 秒消失（可关） |
| 托盘图标 | 无 |
| 常驻权限 | 普通（跑一次 `--elevate` 后升为管理员，见上） |
| 抓帧引擎 | `auto`：HDR 屏走 WGC，纯 SDR 屏走 GDI |

其它可用输出格式：`png` / `bmp` / `tif` / `gif`。
圈选（套索）模式强制输出 PNG —— 透明通道只有 PNG 装得下。

## 覆盖层里能做什么

区域 / 圈选模式下会冻结当前屏幕并铺一层遮罩，此时：

- 拖拽成选区，`Enter` 或松手确认，`Esc` 取消
- 右上角可切成「矩形 / 圈选 / 窗口 / 全屏」四种模式，与 Windows 截图工具一致
- 有放大镜（默认开）用于像素级对齐
- 可配置的**录像宏按钮**：一键向 NVIDIA / AMD / Xbox 录屏发启停热键。
  预设三个：`Alt+F9`（NVIDIA）、`Ctrl+Shift+R`（AMD）、`Win+Alt+R`（Xbox Game Bar），
  也可以改成「启动某个程序」—— 给没有全局热键的录屏方案用

## 配置（settings.json）

改完跑一次 `PrismShot.exe --reload` 生效。绝大多数项保持默认就好，这里只说两个
值得知道自己在改什么的：

```jsonc
{
  "Behavior": {
    // auto（默认）| wgc | gdi
    // auto：只有探测到需要浮点帧的屏（HDR）才建 D3D11 走 WGC，否则直接 GDI
    // wgc ：总是 WGC —— HDR 正确，代价是常驻多出约 820MB（见「已知边界」）
    // gdi ：总是 GDI —— 省下那 820MB，代价是 HDR 内容按 SDR 解读而过曝
    "CaptureEngine": "auto"
  }
}
```

`CaptureEngine` 是给排查和极端场景留的口子。**正常用 `auto`**：SDR 屏上它会自动
绕开 WGC，实测常驻只有 ~70MB；只有真的开着 HDR 才会付那笔 D3D11 的钱。

## 命令行与 IPC

第二次启动 `PrismShot.exe` 不会起第二个实例，而是把请求转交给已经在跑的那个 ——
也因此它天生支持这两种被外部驱动的方式。

**发给常驻实例**（走命名管道 `PrismShot.Control`，一行一条 JSON）：

```
PrismShot.exe --screen                       # 全屏
PrismShot.exe --window                       # 选一个窗口
PrismShot.exe --freeform                     # 圈选        （都不写就默认 --region 区域）
PrismShot.exe --repeat                       # 重复上一次的选区
PrismShot.exe --out D:\a.png --format png    # 指定落点与格式
PrismShot.exe --quality 95 --noclipboard     # JPG 质量、不写剪贴板
PrismShot.exe --ping                         # 常驻实例是否在线
PrismShot.exe --reload                       # 让它重读 settings.json
PrismShot.exe --quit                         # 退出常驻实例
```

**权限与自启**（动的是计划任务 / 注册表，会切掉当前常驻实例）：

```
PrismShot.exe --elevate              # 注册「最高权限」计划任务并以管理员重启（首次弹一次 UAC）
PrismShot.exe --unelevate            # 撤掉计划任务，退回普通权限
PrismShot.exe --uninstall            # 干净卸载：退出常驻实例、删掉两条自启
PrismShot.exe --uninstall --purge    # 连配置与日志一起删（截图目录永不动）
```

`--elevate` 之后：开机自启由计划任务负责，注册表 `Run` 里的自启项会被删掉（避免起两个实例）；
此后再重启走 `schtasks /Run`，**不再弹 UAC**。便携模式不注册。

已在 2560×1440 @125% 的本机实机验证过：跑完 `--elevate` 后日志从「权限=普通」变成
「权限=管理员」，任务管理器聚焦时 `Win+Shift+S` 不再弹原生截图。唯一还没验的是
**注销 / 重启后是否仍静默以管理员起**（不再弹 UAC），要等下一次开机才算数。

管理员常驻**不会**让命令行失效：常驻实例的管道会把完整性标签压回 Medium，
普通权限的命令行照样能连（`--reload` / `--quit` / `--screen` 都走这条）。
万一看到「连不上常驻实例：它以管理员身份在跑」，说明那一步退回了 ——
用管理员身份重开一个命令行即可，日志里会有原因。

**要卸载就用 `--uninstall`，别直接删文件夹。** 两条自启都不在程序目录里 ——
一个在注册表 `Run` 键、一个在任务计划程序。文件夹删掉之后，那两条引用着一个
早已不存在的 exe 的条目还留在原处，而它们在「任务管理器 → 启动」里是一个
看不出指向哪里的条目。命令会把每一步做了什么打出来，并告诉你数据目录在哪。
截图目录（默认 `图片\PrismShot`）永远不动 —— 那是你的文件。

**单次自测**（不打扰常驻实例，在本进程里跑完）：

```
PrismShot.exe --capture --out D:\a.png       # 抓一张虚拟桌面并存盘
PrismShot.exe --capture --engine gdi         # 强制走 GDI 通道（对照用）
PrismShot.exe --probe                        # 打印每块屏的尺寸 / HDR / SDR 白电平
PrismShot.exe --memtest                      # 连抓几帧，逐帧打内存快照
PrismShot.exe --pipetest                     # 抓帧流水线分段记内存
PrismShot.exe --version
```

`--capture` 与 `--probe` 会在同目录下写一份 `.txt` 报告，里面带 `pipeline : WGC | GDI 兜底`
和 `sdr white nits` —— **排查任何"颜色不对"的问题，第一件事就是跑一次 `--probe`**。

排查内存问题用 `--memtest`：它把任务管理器那一列拆成
`ws / managed / committed / frag / loh / gdi / user`，一眼能看出涨的是托管堆、
大对象堆碎片，还是非托管窗口与画刷。全程不落盘、不写剪贴板、不弹通知。

第三方程序想直接连管道的话，照 `src/PrismShot/Core/Ipc/IpcProtocol.cs` 的字段发 JSON 即可：
请求是 `{action, mode, outputPath, format, quality, noSave, skipClipboard}`。
注意 `canceled` 与 `ok` 是两个字段 —— 用户按 Esc 属于正常取消，不该被当成错误弹红叉。

## StarPie 插件

如果你用 [StarPie](https://github.com/SoftBlack42/StarPie) 的轮盘，可以让它的扇区直接调用 PrismShot：
四个截图动作 + 一个「截图识字」。安装与参数说明见
[plugin/StarPie.Plugin.PrismShot/README.md](plugin/StarPie.Plugin.PrismShot/README.md)。

> 关于 OCR：插件**不做 OCR**。当前 StarPie SDK 没有开放「识别一张已有图片」的入口，
> 于是 `captureOcr` 会保存选区图并返回一句清楚的提示（而不是假装成功）。
> 上游实现 `IHostOcrService` 之后插件无需改代码即可生效，提案见
> [upstream/ocr-seam.md](plugin/StarPie.Plugin.PrismShot/upstream/ocr-seam.md)。

## 自己编译

```powershell
# 一次性：拉一套隔离的 .NET 8 SDK 到 ~/.uba-dotnet（不污染系统）
./build.ps1 -BootstrapSdk

./build.ps1            # 编译主程序 + 插件，产物进 artifacts/
./build.ps1 -Run       # 编译后立刻启动，验证钩子有没有接管热键
```

SDK 8.0 以上、Windows SDK 契约 `10.0.22621`（编译期用，不影响运行在哪版 Windows），
运行时不需要任何第三方本机 dll —— 只用系统自带的 `d3d11.dll` / `user32.dll` / `gdi32.dll`。
NuGet 只有一个纯托管的 `Vortice.Direct3D11`（手写完整 ID3D11DeviceContext vtable 有 120 多个槽位，
错一个就是随机崩溃，换它的代价远小于收益）。

## 已知边界

### 热键会被系统抢走 —— 前台是管理员窗口时

不是冲突，是 **UIPI（用户界面特权隔离）**：完整性级别高的窗口在前台时，
系统不会调用低完整性进程安装的低级键盘钩子。所以它表现得很"挑窗口"：

| 前台窗口 | 普通权限常驻 | 管理员常驻（`--elevate` 后） |
| --- | --- | --- |
| 资源管理器、浏览器、编辑器 | ✅ | ✅ |
| **任务管理器、注册表编辑器**（任何以管理员运行的程序） | ❌ 落到 Windows 原生截图 | ✅ |

"再按一次就好了"不是玄学：原生截图工具本身不是管理员，它一弹出来前台完整性就降回去，
钩子恢复 —— 于是第二下归 PrismShot。

**跑一次 `PrismShot.exe --elevate` 即可根治**（只弹一次 UAC）。代价是常驻进程跑在管理员权限下；
如果你不接受这个代价，`--unelevate` 随时退回，只是那几个窗口里热键仍会被抢。

### 开着 HDR 时，常驻内存约 1 GB

这是**设计上的代价，不是内存泄漏**，值得说清楚：

- 其中约 **820 MB 是一个 D3D11 设备本身的固定成本**。用裸 `D3D11CreateDevice`
  建一个什么都不做的设备，工作集就从 42 MB 涨到 864 MB，释放后只归还约 13 MB ——
  驱动和 D3D 运行时常驻，非托管，GC 完全看不见
- 建/释放循环**不会持续上涨**，所以不是泄漏，它只是保留着
- 它是 WGC 的入场券：**想要浮点帧、想要 HDR 颜色正确，就得付这笔钱**

实测（2560×1440 @125%，HDR 开启，SDR 白电平 360 尼特）：

| 场景 | 工作集 |
| --- | --- |
| 空闲基线 | ~51 MB |
| 第 1 次截图后 | ~1.06 GB |
| 第 2 / 3 / 4 次后 | 1.166 / 1.168 / 1.165 GB |
| 强制 `--engine gdi` | ~70 MB |

托管堆这一侧是干净的（`--memtest` 里 `managed` 能归零）。
**SDR 屏 / 关掉 HDR 的屏不会遇到这个数字** —— `auto` 会自动走 GDI，实测 ~70 MB。
若你在本机就是想把它压下来，`Behavior.CaptureEngine = "gdi"` 能降到 ~70 MB，
代价是 HDR 内容过曝。这是逃生舱，不是推荐值。

> 注意别把两份报告里的 `hdr` 看混：`--probe` 的 `hdr enabled : True` 是**屏开没开 HDR**，
> 而报告末尾的 `real hdr content : False` 是**这一帧里有没有超过 SDR 范围的像素**。
> 屏开着 HDR、画面却全是 SDR 内容时，那 820MB 照样得付 —— 这不是浪费，
> 只有走浮点通道，将来真出现高亮内容时才不会过曝。

### 其它

- WGC 在**远程桌面会话、锁屏、部分虚拟显卡**上建立不了会话。
  此时自动降级到 GDI：图照样出得来，功能不残废，只是那一张没有 HDR。
  每次截图会在日志里写下当次走的通道（`WGC` / `GDI`），出问题时先看它。
- **圈选粘贴出去仍是矩形**：只有**支持 alpha 的接收方**才拿得到形状。
  我们同时投了 PNG 和 `CF_DIBV5`，但只认老 `CF_DIB` 的程序拿不到 ——
  那种情况下它拿到的是一份**合成到白底**的矩形（而不是原图，避免把你圈掉的内容泄漏出去）。
  存盘的 PNG 永远是对的。
- **换了位置之后开机自启会自己纠正**：自启项与计划任务里都写死了 exe 路径，
  PrismShot 每次启动会比对一次，发现不是当前这个就重写。所以直接把文件夹
  拷到新位置再跑一次就行，不用先卸载。
- 日志文件在 `%LOCALAPPDATA%\PrismShot\logs\`。
- 只截图：不录屏、不做 OCR、不做编辑器、不做贴图批注。这是刻意的范围限制，不是删减功能。

## 许可

MIT。见 [LICENSE](LICENSE)。
