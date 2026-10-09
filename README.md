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

**单次自测**（不打扰常驻实例，在本进程里跑完）：

```
PrismShot.exe --capture --out D:\a.jpg       # 抓一张虚拟桌面并存盘
PrismShot.exe --probe                        # 打印每块屏的尺寸 / HDR / SDR 白电平
PrismShot.exe --version
```

`--capture` 与 `--probe` 会在同目录下写一份 `.txt` 报告，里面带 `pipeline : WGC | GDI 兜底`
和 `sdr white nits` —— **排查任何"颜色不对"的问题，第一件事就是跑一次 `--probe`**。

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

- WGC 在**远程桌面会话、锁屏、部分虚拟显卡**上建立不了会话。
  此时自动降级到 GDI：图照样出得来，功能不残废，只是那一张没有 HDR。
  每次截图会在日志里写下当次走的通道（`WGC` / `GDI`），出问题时先看它。
- 日志文件在 `%LOCALAPPDATA%\PrismShot\logs\`。
- 只截图：不录屏、不做 OCR、不做编辑器、不做贴图批注。这是刻意的范围限制，不是删减功能。

## 许可

MIT。见 [LICENSE](LICENSE)。
