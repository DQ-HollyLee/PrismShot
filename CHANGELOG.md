# PrismShot 变更日志

版本号唯一真相在 `version.json`。本文件每条都必须对应一次 git 提交或 tag。

## [Unreleased]

## [0.1.0-beta.1] - 2026-10-10

第一个 Beta。两件新东西：一个真正的安装程序，和一个能认字的 StarPie 插件。

### 新增：安装程序

`artifacts/PrismShot-<version>-Setup.exe`，单文件，程序内容嵌在里面 ——
分发的是一个 exe，不是「一个 exe 加一个文件夹」。后者迟早会被人只拷走 exe，
然后得到一个装不出东西的安装器。

- 默认装到 `%LOCALAPPDATA%\Programs\PrismShot`（`--dir` 可改）。
  **每用户**安装，装的过程不需要管理员权限：它的数据、配置、日志本来就在用户目录下，
  装到 Program Files 只会换来一次毫无必要的 UAC。它真正需要提权的地方是
  「热键要在管理员窗口上生效」，那走 `--elevate`，且只需要弹一次
- **安装目录里留下 `uninstall.exe`** —— 就是安装程序自己换了个名字
- 同时在 `HKCU\...\Uninstall\PrismShot` 登记，于是「设置 → 应用」里能看见、能卸载
- 开始菜单建两项：`PrismShot` 与 `卸载 PrismShot`
- 装完默认跑一次 `--elevate`（弹一次 UAC），`--no-elevate` 可跳过

卸载这条路径是照着「删不干净」那次事故写的，顺序不能反：

1. 先让常驻实例自己撤掉两条自启（注册表 Run 与计划任务）并退出 ——
   它还活着的话，下一次启动自检会把自启原样写回去
2. 再删开始菜单、卸载登记、安装目录
3. `--purge` 才删配置与日志；**截图目录永远不动**
4. `uninstall.exe` 删不掉正在运行的自己，所以往临时目录放一个自删脚本，
   等这个进程结束再清掉整个目录。只有确认「自己就是装在这个目录里的
   `uninstall.exe`」时才做 —— 万一有人把它拷到别处双击，绝不能顺手删掉那个目录

**为什么自己写而不是引 Inno Setup / NSIS**：那两家都是好东西，但它们是构建机上的
外部依赖，而本项目要求「拿到仓库就能构建」。安装器只做四件很确定的事 ——
解包、写卸载项、建快捷方式、叫起程序 —— 自己写比引第三方更可控。
快捷方式走 `WScript.Shell` 后期绑定而不是手写 `IShellLinkW` 的 COM 声明：
后者要求把 vtable 上 18 个方法按序一字不差排出来，排错一个就是内存损坏，
而且在本机上根本试不出来。

体积上踩了一个坑值得记：装了带平台版本号的 TFM（`net8.0-windows10.0.19041.0`）
会把 WinRT 投影程序集 `Microsoft.Windows.SDK.NET.dll`（约 25 MB）一起打进产物。
主程序需要它（WGC 抓帧与 HDR 探测走 WinRT），安装器一个 WinRT API 都不碰。
改用不带版本号的 `net8.0-windows` 之后 **32.8 MB → 7.4 MB**；
`Microsoft.Win32.Registry` 属于 `Microsoft.NETCore.App`，不需要版本号。

### 新增：StarPie 插件支持 AI OCR

需求是「用 PrismShot 完成选区，让 StarPie 认这段区域里的字」。
宿主 SDK 只开放了 `CaptureAndRecognize()`，它会**再弹一次自己的框选窗口**，
用户得框两次、第一次的结果直接被丢掉 —— 这不是取舍，是缺陷。

于是把 OCR 那一整段**照搬**进来：`plugin/.../upstream/OcrManager.cs` 与 StarPie 的
`WinPieGestures/OcrManager.cs` 逐行同源。改动只有三处，且都是为了「离开宿主还能活」：

| 上游写的是 | 插件换成 | 为什么必须是它 |
| --- | --- | --- |
| `ConfigManager.CurrentConfig.OcrConfig` | 读 `%LOCALAPPDATA%\StarPie\config.json` | 宿主内部类型不可引用；读盘反而保证了**与宿主同一份配置** |
| `AppLogger.LogXxx` | `OcrLog`（绑到插件日志） | 同上 |
| `I18n.T(...)` | 字面中文 | 插件拿不到宿主的词条表 |

被删掉的只有两处，正是「框选」这一半：`ShowSettingsDialog` 与
`StartCaptureAndRecognize`（后者会弹宿主的 `ScreenSnipWindow`）—— 它们的位置由 PrismShot 顶上。
识别本体一个字没动：四个引擎（本地离线 / AI 视觉 / 自定义 HTTP / 云端）、
几何重排、文案后处理、识别前的图像预处理全在。

- 插件 TFM 抬到 `net8.0-windows10.0.19041.0`：照搬的源码要用 `System.Drawing.Bitmap`
  与 `Windows.Media.Ocr` 的 WinRT 投影，两者都要求带平台版本号。
  宿主自己的 TFM 就是 19041，而插件闸门判定的是「插件不高于宿主」——
  持平通过，高一点就被拒载，所以这里是「恰好等于」而不是「尽量新」
- 清单补了 `Network` 能力：AI 与自定义 HTTP 引擎要真的发请求，
  声明了才诚实，也才可能被宿主的权限模型放行
- **实测**（`.cache/plugincheck`，链真实源码 + 真实宿主配置）：
  引擎 `Ai` / DeepSeek 视觉模型，`875×375` 的选区认出 347 字，2.6 秒，
  结果进了剪贴板。用户不需要在插件里再配一遍密钥
- 宿主若哪天把 `IHostOcrService` 补进 SDK（提案见 `upstream/ocr-seam.md`），
  插件**无需重新编译**就会改走宿主的引擎。重复实现永远不如唯一真相，
  照搬的那份本来就只是代班

### 顺带修：构建脚本不再因为产物目录被占用而失败

在装了 PrismShot 的机器上构建一定会撞上这一下：目录里有句柄，
`Directory.Delete` 直接抛，于是整个构建停住。现在三级降级：
**删 → 改名让开 → 直接覆盖**。硬失败在这里毫无收益 ——
构建脚本的职责是产出这一版，不是替用户做进程管理。

## [0.1.0-alpha.17] - 2026-10-10

### 改进：`--reload` 不再回一句「ok 0x0」

`--reload` 的应答是 `{ Ok = true }` —— 没有尺寸、没有路径，因为它本来就没有产物。
而 `Describe` 一律按截图的格式拼，于是命令行打出 `ok 0x0 `：
一句话等于没说，还让人以为「尺寸是 0，是不是出错了」。

按「这条应答有没有产物」分开说：reload / quit 各自报一句做了什么
（`ok 已重新加载配置：<settings.json 路径>`），截图仍报尺寸与路径，
用户取消就说 `canceled`。老服务端回的光秃秃 `Ok`（还没有 `Message` 字段）
退回一个 `ok`，不再冒出 `0x0`。

`IpcRequest.ToString()` 同理：`Mode` 只对截图有意义，日志里那句
「收到 IPC 命令：reload region」改成「收到 IPC 命令：reload」——
原来的写法让人以为有人按了区域截图。

验证：`.cache/desctest` 链真实源码，11 项断言覆盖每条分支
（含「没有 Message 时不写进 JSON」这条契约兼容性）。

## [0.1.0-alpha.16] - 2026-10-10

### 修复：管理员常驻之后，所有走 IPC 的命令全部失效

**症状**：`--elevate` 成功、任务管理器里也能截屏了，但从此
`--quit` / `--reload` / `--screen` / `--region` 一律只回一句
`error: Access to the path is denied.` —— 而热键截图照常工作，所以很容易隔很久才发现。

**原因是完整性策略的 `NoWriteUp`**，不是权限没给够：管理员进程建出来的管道对象是
High 完整性，普通权限的命令行连它既要读也要写，写访问被完整性检查直接挡下 ——
这是系统有意为之的那道墙，绕过它本来就不该是选项。

修法不是拆墙，而是**把管道对象的完整性标签压回 Medium**：`CreateNamedPipe` 时传入
一份安全描述符，`D:` 允许当前用户完全控制、`S:` 里挂一条 `ML` ACE（Medium + NoWriteUp）。
往下降标签不需要特权，往上抬才需要 —— 所以这既不用 UAC，也没有把门开给别的账户：
管道里传的本来就是「截一张图」「重新读配置」这类用户自己发起的事。

- 新增 `Core/Ipc/MediumLabelPipe.cs`。任何一步失败都**退回**原来的
  `NamedPipeServerStream` 构造 —— 宁可退回「只有管理员命令行能用」，
  也不能让管道整个起不来。这条回退被静默触发的话修复等于白做，
  所以 `IpcTest` 里第一条断言就是「不许静默回退」
- 客户端 catch 住 `UnauthorizedAccessException`，把那句谁也看不懂的
  `Access to the path is denied.` 换成「常驻实例以管理员身份在跑，而这个命令行不是」

**验证**（`.cache/ipctest`，链真实源码）：管道建起来了（未回退）、
安全描述符读得回来、SACL 里有 `ML` ACE、**标签实测 `0x2000`（Medium）**、
客户端连得上、消息往返 `pong`。

**实机验证（2026-10-10 17:50，本机）**：注销重登后由计划任务拉起的实例（管理员），
在一个**普通权限**的 PowerShell 里跑 `--reload` —— 日志里出现了
`收到 IPC 命令：reload`，命令正常返回。这条以前必然是
`Access to the path is denied.`，说明 High 服务端 + Medium 客户端确实被放行了。
同一次注销重登也验证了 `--elevate` 的第 ② 条：**开机以管理员静默起，不再弹 UAC**。

## [0.1.0-alpha.15] - 2026-10-10

### 新增：`--uninstall`（干净卸载）

直接把程序文件夹删掉是删不干净的：两条自启**都不在程序目录里** ——
一个在 HKCU 的 `Run` 键、一个在任务计划程序。文件夹没了，那两条引用着一个
早已不存在的 exe 的条目还留在原处：开机时 Windows 试着启动它、失败、一声不响，
而用户在「任务管理器 → 启动」里看到的就是一个删不掉、也看不出指向哪里的条目。

`PrismShot.exe --uninstall` 依次做四件事，并把每一步的结果打印出来：

1. 让常驻实例退出 —— **顺序不能反**。它还活着的话，下一次启动自检
   （或者用户随手双击一下）就把自启又写回去了，等于白卸一次
2. 删掉最高权限计划任务（若注册过）
3. 删掉注册表 `Run` 键里的自启项，并打印它原先指向哪
4. 报告数据目录在哪；加 `--purge` 才连它一起删

**截图目录永远不动**（默认 `图片\PrismShot`）—— 那是用户的文件，卸载不该碰，
这一点命令输出里会明说。

### 修复：圈选贴进剪贴板后还是一整个矩形

存出来的 PNG 是对的、粘贴出来却变回外接矩形 —— 遮罩没做错，
是**剪贴板格式装不下 alpha**。

原先只投一份 `CF_DIB`，而这个 1990 年代的格式根本没有 alpha 概念：32bpp 时
第 4 字节约定保留为 0，接收方一律忽略。于是多边形外那圈透明被当成不存在。

现在带透明时一次会话里投三份，按「接收方越现代越靠前」排：

| 格式 | 作用 |
| --- | --- |
| `PNG`（注册格式） | 字节级无损，透明原样保留。Office、Chrome、VS Code 优先吃它 |
| `CF_DIBV5` | 标准剪贴板格式里唯一正式支持 alpha 的一种，靠 `bV5AlphaMask` |
| `CF_DIB` | 给只认老格式的程序兜底 |

两种成分必须一次投放：`EmptyClipboard` 每开一次就清一次，分三次投的话
前两次会被自己清掉。

兜底那份刻意**合成到白底**而不是原样贴。老格式拿不到形状已成定局，
但原样贴意味着用户明明圈走了一块不想给人看的内容，粘贴出去却变成完整矩形 ——
那是泄漏，不是降级。

不带透明时（区域 / 全屏 / 窗口）行为完全不变，仍然只投一份 `CF_DIB`，零额外开销。

验证（`.cache/cliptest`，链真实源码、写进去再读回来查，19 项全通过）：
V5 头长度 124、位深 32、`BI_BITFIELDS`、alpha 掩码 `0xFF000000`、
RGB 掩码按 BGRA 排布；V5 与 PNG 都保留了「圈内 255 / 圈外 0」；
DIB 圈外确为白 `(255,255,255)`、圈内原色未被误改；普通截图没有多投 PNG。

> 附带一条以后会用到的知识：`CF_DIBV5` 不能拿来判「有没有投过」——
> Windows 会自动从 `CF_DIB` 合成一个。实测只投 `CF_DIB` 时，
> `EnumClipboardFormats` 照样报 `[8, 2, 17]`。

### 修复：开机自启一直指向那个旧 exe

用户报「开机自动启动的 exe 不是最新的，而是
`src\PrismShot\bin\Release\net8.0-windows10.0.22621.0\PrismShot.exe`」——
日志把这个 bug 的来龙去脉记得清清楚楚：

```
04:34:34 已写入开机自启："...\src\PrismShot\bin\Release\...\PrismShot.exe"   ← 早期从源码目录跑过一次
08:34:03 PrismShot 0.1.0-alpha 启动（Installed，权限=普通）                  ← 之后一直用发行包
13:12:49 PrismShot 0.1.0-alpha 启动（Installed，权限=普通）                  ← 但从没纠正过那条路径
```

根因是 `App.ApplyAutoStart` 只比「有没有这一条」：

```csharp
if (AutoStart.IsEnabled() != desired) { AutoStart.Apply(desired); }
```

`IsEnabled()` 只看那个值存不存在。条目存在（哪怕指向一个早被删掉的 exe），
它就等于 `desired`，于是 `Apply` 永不执行 —— 那条死路径永远不会被纠正。
而 `AutoStart.IsStale()` 早就写好了，却**一次都没被调用过**。

现在改成 `IsEnabled() != desired || IsStale()`。计划任务那条同样会过时
（`<Command>` 是注册那一刻写死的），一并加了自检：启动时发现任务指向的
不是当前 exe 就重新注册；已经是管理员就直接改（不再弹 UAC），不是就留一条警告。

实测：把自启手工改回旧路径后重启，它自己纠正回来了 ——
`BEFORE=...\bin\Release\...` → `AFTER="...\artifacts\PrismShot-0.1.0\PrismShot.exe"`。

### 顺带修：构建脚本撞上自己

常驻实例跑的往往**正是**上一版 `artifacts` 里的那个 exe，它把目录里所有 dll 都锁着，
于是排发行包时既删不掉也覆盖不了。这不是偶发：PrismShot 天生常驻，
只要在本机构建就一定会撞上。现在 `build.ps1` 排包前会先请它退出，
并等它把互斥体让出来；真删不掉时报一句「先执行 `PrismShot.exe --quit`」而不是一句
「对路径 xxx 的访问被拒绝」。

## [0.1.0-alpha.14] - 2026-10-10

### 新增：管理员常驻（`--elevate` / `--unelevate`）

**症状**：焦点在任务管理器上时按 `Win+Shift+S`，弹出来的是 Windows 原生截图工具；
再按一次才好。其它窗口都正常，只有管理员窗口稳定复现。

**原因不是热键冲突，是 UIPI（用户界面特权隔离）**。完整性级别高的窗口在前台时，
Windows 根本不会去调用低完整性进程装的那根 `WH_KEYBOARD_LL` 低级键盘钩子 —— 
钩子没被"抢"，它是**一次都没被叫到**，于是按键原样落到系统自己的截图工具上。
"再按一次就好"是因为原生截图工具本身不是管理员：它一出来，前台完整性就降回普通，
钩子恢复工作。

要根治，PrismShot 自己的完整性级别就必须不低于任何一个可能在前台的窗口，
也就是**以管理员常驻**。为此注册一条计划任务：

- `RunLevel=HighestAvailable`：任务以最高权限拉起，不弹 UAC
- `LogonType=InteractiveToken`：有桌面会话，覆盖层和剪贴板才正常
- `ExecutionTimeLimit=PT0S`：**必须**。计划任务默认 72 小时上限，
  常驻进程会在第三天被判定超时杀掉，而这条命令看着只跟"限时"有关
- `MultipleInstancesPolicy=IgnoreNew`、`Enabled=true`、`LogonTrigger` 开机触发
- 任务名 `PrismShot`，启动时带 `--from-task` 参数，用来区分是谁拉起来的

首次注册弹**一次** UAC；之后开机自启和 `PrismShot.exe --elevate` 重启都走 `schtasks /Run`，
静默提权、不再有弹窗。注册成功后**注册表 `Run` 里的自启项会被删掉**
（`AutoStart.Remove`），否则开机会起两个实例。

`--unelevate` 撤掉任务，退回普通权限 + 注册表自启（此时前台是管理员窗口时热键仍会被抢）。

配套改的两处：

- 单实例互斥体加了等待：计划任务拉起新实例时，上一个实例往往正在退出、
  互斥体还没释放。现在会每 120ms 重试、最多等 6 秒，避免出现"两边都没实例"的空窗
- 便携模式（`portable.txt`）不注册计划任务 —— 它本来就不该往系统里写东西

**实机验证（2026-10-10 17:08，本机）**：在普通命令行跑 `PrismShot.exe --elevate`，
弹了一次 UAC 批准后，日志连成一条完整证据链 ——

```
17:08:09 已移除注册表自启项（改由计划任务接管）
17:08:10 PrismShot 0.1.0-alpha 启动（Installed，权限=管理员）      ← 此前一直是「权限=普通」
17:08:10 开机自启由计划任务「PrismShot」负责（管理员身份）
```

随后在**任务管理器聚焦时按 `Win+Shift+S` 不再弹 Windows 原生截图**，由 PrismShot 接管 ——
UIPI 这条判断得到证实：钩子不是被抢优先级，而是根本没被调用一次。
读回 `C:\Windows\System32\Tasks\PrismShot`，落地的 XML 与上面设计完全一致
（`HighestAvailable` / `InteractiveToken` / `PT0S` / `IgnoreNew` / `--from-task`）。

**尚未验证**：注销或重启后是否仍以管理员静默起（不弹 UAC）。`LogonTrigger` 已配、
`PT0S` 也防住了 72 小时那条，但要等下一次开机才算数。

### 新增：抓帧引擎可配置（`Behavior.CaptureEngine`）

| 值 | 行为 |
| --- | --- |
| `auto`（默认） | 只有探测到需要浮点帧的屏（HDR）才建 D3D11 走 WGC；否则直接 GDI，**不建设备** |
| `wgc` | 总是 WGC |
| `gdi` | 总是 GDI。逃生舱 —— HDR 内容会按 SDR 解读而**过曝**，不是推荐值 |

命令行 `--engine gdi|wgc|auto` 只对当次生效，用来对照排查。

### 新增：内存诊断命令

```
PrismShot.exe --memtest [--count N] [--refresh N] [--trim] [--engine gdi|wgc|auto]
PrismShot.exe --pipetest [--devices] [--gdi] [--sdr] [--d3dmin]
```

`--memtest` 连抓若干帧，每帧后打一行快照：`ws / envws / priv / managed / heap /
committed / frag / loh / gdi / user`，把任务管理器那一列拆成托管堆、GC 提交量、
碎片、大对象堆、非托管窗口与画刷。`--refresh` 专门测覆盖层重画的单次分配量。
全程不落盘、不写剪贴板、不弹通知。

`--pipetest` 把抓帧流水线按 `device / frame / texture / readback / tonemap /
dispose / collect` 分段，每段停一下记一笔 —— 用来钉死"到底是哪一步涨的"。
`--d3dmin` 拿裸 `D3D11CreateDevice` 跑一组参数矩阵，用来判断成本出在我们的
创建参数还是设备本身。

### 修复

- **圈选（套索）时每移动一像素就分配 5.5 MB**：覆盖层每次刷新都从头重建整条折线的
  `PathGeometry`，几千个 `LineSegment` 全是新对象。改成只把**新增的顶点段**追加进同一个
  `PathFigure`。实测 `5,493,513 B/次 → 3,309 B/次`。一笔圈选要刷新几千次，
  光这一项就少掉几个 GB 的垃圾 —— 这也是"圈选时内存一路涨"的主因
- **截图后内存没真正还回去**：原先只 `GC.Collect`，而大对象堆默认**不压缩**。
  每张图尺寸都不同，几十张之后堆变成瑞士奶酪：空着的地方凑不出一块连续大缓冲，
  于是每次还得再向系统要新的，任务管理器上那个数字只涨不落，可堆里其实没几个活对象。
  改成 `LargeObjectHeapCompactionMode.CompactOnce` + 强制压缩，跑两轮
  （第二轮收终结器带出来的），并且工作集超过 192MB 才做，免得每次截图都停一下

### 实测：本机内存（2560×1440 @125%，`hdr enabled = True`，10bit，SDR 白电平 360 尼特）

| 场景 | 工作集 |
| --- | --- |
| 空闲基线 | ~51 MB |
| 第 1 次全屏截图后 | ~1.06 GB |
| 第 2 / 3 / 4 次后 | 1.166 / 1.168 / 1.165 GB |
| 同机强制 `--engine gdi` | ~70 MB |

**结论不美化**：这 ~1GB 里约 **820 MB 是一个 D3D11 设备本身的固定成本**。
用裸 `D3D11CreateDevice` 建一个什么都不做的设备，工作集就从 42 MB 涨到 864 MB；
释放之后只归还约 13 MB —— 驱动和 D3D 运行时常驻，非托管，GC 完全看不见。
而建/释放循环**不会持续上涨**，所以它不是泄漏，它只是**保留着**。
`--d3dmin` 的参数矩阵证明这笔钱跟我们的创建参数无关：
带不带特征级别数组、带不带 `BGRA_SUPPORT`，都是 820 MB 左右。

它是 WGC 的入场券 —— 只要想拿浮点帧，就得付。所以：

- **HDR 屏上要正确的颜色，就得接受约 1 GB**。这是"HDR 正确"的代价，不是 bug
- **SDR 屏 / 关掉 HDR 的屏**：`auto` 已经自动走 GDI，实测 ~70 MB
- 非要在本机降下来：`settings.json` 里 `Behavior.CaptureEngine = "gdi"`，
  降到 ~70 MB，代价是 HDR 内容过曝。逃生舱，不推荐

托管堆这一侧是干净的：`--memtest` 里 `managed` 能归零，`--trim`（`EmptyWorkingSet`）
实测**无效** —— 驱动把页面锁住了，命令保留在 `--memtest` 里仅作对照，别再指望它。

⚠️ **别把两份报告里的 `hdr` 看混了**：

- `--probe` 里的 `hdr enabled : True` —— 这块屏开没开 HDR（本机 True，10bit，白电平 360 尼特）
- `--capture` / `--probe` 报告末尾的 `real hdr content : False` 与
  每块屏那行的 `hdr=False` —— **这一帧里有没有超过 SDR 范围的像素**

也就是说：屏开着 HDR、但画面内容全是 SDR 范围时，浮点通道和那 820MB **照样得付**。
这不是浪费 —— 只有走浮点通道，将来真出现高亮内容时才不会过曝。

> 关于 alpha.12 那条"内存暴涨已修复"：那一半是对的 —— COM 指针不 Release 导致
> **每截一张漏一整条链**确实修好了，多截几张不再线性增长。但剩下这块 ~820 MB 的
> "地板"当时没测出来（那时还不知道它是 D3D 设备的固定成本）。这次补齐了测量手段与结论。

### 顺带修正

`PROJECT.md` 待办里"本机 `--probe` 报 `hdr enabled = False`"是过时的旧记录，
实测为 **True**（10bit / 360 尼特 / `capture as float = True`）。已改正。

## [0.1.0-alpha.13] - 2026-10-10

### 修复

- **老配置里写着的 `jpg` 不会自己变成 `png`**：默认格式改成 png 之后，
  只有新建配置才享受得到；已经跑过一次、settings 里写着 `"format": "jpg"` 的老用户，
  圈选存出来仍然是外接矩形。现在 `SettingsStore.Normalize` 里补了一次迁移

## [0.1.0-alpha.12] - 2026-10-10

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
  另外改成工作集超过 256MB 才做一次强制回收，免得每次截图都停一下。
  ⚠️ 这一条只修掉了「每截一张都涨」的部分，常驻的那 ~820MB 另有出处，见 alpha.14 的实测
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
