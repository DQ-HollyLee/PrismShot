using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PrismShot.Core;
using PrismShot.Core.Capture;
using PrismShot.Core.Diagnostics;
using PrismShot.Core.Imaging;
using PrismShot.Core.Interop;
using PrismShot.Core.Ipc;
using PrismShot.Core.Platform;
using IoDirectory = System.IO.Directory;

namespace PrismShot;

/// <summary>
/// 命令行入口。
/// </summary>
/// <remarks>
/// 三条通道在这里分岔：
/// <list type="number">
/// <item>无参数 → 常驻模式；若已经有实例在跑，则把命令转交过去然后自己退出。</item>
/// <item><c>--capture</c> / <c>--probe</c> → 单次执行的自测与排障通道。</item>
/// <item><c>--region</c> / <c>--screen</c> / <c>--window</c> / <c>--quit</c> → 控制常驻实例。</item>
/// </list>
/// 命令行不是临时脚手架而是产品能力：StarPie 插件靠它调用截图，
/// 用户来报「HDR 截图不对」时第一件事也是让他跑一次 <c>--probe</c>，看白电平到底是多少。
/// </remarks>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 必须抢在任何窗口、任何屏幕度量之前。理由见 DpiAwareness 的注释：
        // 清单里的声明在纯命令行路径下实测不生效，差 1.25 倍就是一张缩小两成的虚拟桌面。
        DpiAwareness.Enable();

        AppPaths.EnsureCreated();
        Log.Prune();

        string command = args.Length > 0 ? args[0] : string.Empty;

        try
        {
            // 自测 / 排障这几条要在本进程里跑完，不参与「转交给常驻实例」这一步。
            return command switch
            {
                "--capture" => Capture(args),
                "--probe" => Probe(args),
                "--memtest" => MemoryTest.Run(args),
                "--pipetest" => CapturePipelineProbe.Run(args),
                "--elevate" => Elevate(),
                "--unelevate" => Unelevate(),
                "--uninstall" => Uninstall(args),
                "--version" => Version(),
                _ => ForwardOrReside(args),
            };
        }
        catch (Exception ex)
        {
            Log.Error("命令行执行失败。", ex);
            return 2;
        }
    }

    /// <summary>
    /// 已经有一个实例在跑就把请求转过去，没有则自己成为常驻实例。
    /// </summary>
    private static int ForwardOrReside(string[] args)
    {
        bool fromTask = Array.Exists(
            args,
            argument => string.Equals(argument, Elevation.FromTaskFlag, StringComparison.OrdinalIgnoreCase)
        );

        using SingleInstanceGuard guard = new();

        // 由计划任务拉起时多等一会儿：上一个实例可能正在退出（比如刚刚被
        // --elevate 或一次手动双击换掉），抢在它前面拿互斥体会让新实例误判「已经有实例在跑」
        // 然后自己退出 —— 结果是用户既没有管理员实例，也没有普通实例。
        if (!guard.TryAcquire(fromTask ? TimeSpan.FromSeconds(6) : TimeSpan.Zero))
        {
            if (args.Length == 0)
            {
                // 双击图标启动了第二次。老老实实告诉用户一声，
                // 而不是再起一个进程去抢互斥体 —— 那样会留下两个都半死不活的实例。
                Console.WriteLine(
                    "PrismShot 已在运行。要截图请按 Win+Shift+S，或加 --screen / --region 参数。"
                );
                return 0;
            }

            IpcRequest request = Translate(args);
            IpcResponse response = IpcClient.SendAsync(request).GetAwaiter().GetResult();

            Console.WriteLine(IpcProtocol.Describe(response));
            return response.Ok && string.IsNullOrEmpty(response.Error) ? 0 : 1;
        }

        // 注册过最高权限计划任务，但这次不是被它拉起来的（用户双击了图标）：
        // 转交给任务去拉起管理员实例，否则前台是管理员窗口时热键会失灵。
        if (
            !fromTask
            && !Elevation.IsElevated
            && Elevation.IsTaskRegistered()
            && Elevation.StartViaTask(out string note)
        )
        {
            Log.Info(note);
            return 0;
        }

        App app = new();
        return app.Run();
    }

    /// <summary>
    /// 让 PrismShot 以后都以管理员身份常驻。
    /// </summary>
    /// <remarks>
    /// 只有这一步要过 UAC，之后的开机自启和手动启动都由计划任务静默完成。
    /// 所以这里刻意把话说清楚：按下去会弹一次确认框，那不是出错。
    /// </remarks>
    private static int Elevate()
    {
        if (!Elevation.Register(out string message))
        {
            Console.WriteLine(message);
            return 1;
        }

        Console.WriteLine(message);

        // 注册表那条自启要让位，否则开机时任务和 Run 键会各起一个实例。
        AutoStart.Remove();

        // 现在跑着的这一份还是普通权限，让它退下去，换成管理员实例。
        IpcResponse quit = IpcClient.SendAsync(new IpcRequest { Action = IpcActions.Quit })
            .GetAwaiter()
            .GetResult();
        _ = quit;

        Thread.Sleep(600);

        if (Elevation.StartViaTask(out string note))
        {
            Console.WriteLine(note);
            Console.WriteLine("PrismShot 现在以管理员身份常驻。");
            return 0;
        }

        Console.WriteLine(note);
        return 1;
    }

    private static int Unelevate()
    {
        if (!Elevation.Unregister(out string message))
        {
            Console.WriteLine(message);
            return 1;
        }

        Console.WriteLine(message);
        Console.WriteLine("下次启动会回到普通权限。开机自启按配置重新写回注册表。");

        IpcResponse quit = IpcClient.SendAsync(new IpcRequest { Action = IpcActions.Quit })
            .GetAwaiter()
            .GetResult();
        _ = quit;
        return 0;
    }

    /// <summary>
    /// 卸载：退出常驻实例、删掉开机自启（计划任务 + 注册表），可选清掉本程序自己的数据目录。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么必须有这条命令：两条自启都不在程序目录里 —— 一个在注册表 Run 键、
    /// 一个在任务计划程序。直接把程序文件夹删掉，那两处**引用着一个早已不存在的 exe**
    /// 的条目会原样留下来：开机时 Windows 试着启动它、失败、多半一声不响，
    /// 而用户在「任务管理器 → 启动」里看到的就是一个删不掉、也看不出指向哪里的条目。
    /// 卸载这种事只能由程序自己来做，它才知道自己往哪些地方写过东西。
    /// </para>
    /// <para>
    /// 顺序上**先让常驻实例退出**：它还活着的话，下一次启动自检（或者用户随手双击一下）
    /// 就会把自启又写回去，等于白卸一次。
    /// </para>
    /// </remarks>
    private static int Uninstall(string[] args)
    {
        bool purge = Array.Exists(
            args,
            argument => string.Equals(argument, "--purge", StringComparison.OrdinalIgnoreCase)
        );

        Console.WriteLine("PrismShot 卸载");
        Console.WriteLine();

        // 1) 常驻实例
        try
        {
            IpcResponse quit = IpcClient.SendAsync(new IpcRequest { Action = IpcActions.Quit })
                .GetAwaiter()
                .GetResult();
            Console.WriteLine(quit.Ok ? "常驻实例已退出。" : "没有在运行的常驻实例。");
        }
        catch (Exception ex)
        {
            // 没在跑的时候管道连不上，这是正常情形，不是失败。
            Console.WriteLine($"常驻实例未在运行（{ex.Message}）");
        }

        Thread.Sleep(400);

        // 2) 计划任务（管理员常驻那条）
        if (Elevation.IsTaskRegistered())
        {
            Elevation.Unregister(out string note);
            Console.WriteLine(note);
        }
        else
        {
            Console.WriteLine("没有注册计划任务，跳过。");
        }

        // 3) 注册表 Run 键
        string? before = AutoStart.Current();
        if (before is null)
        {
            Console.WriteLine("注册表里没有开机自启项，跳过。");
        }
        else
        {
            AutoStart.Remove();
            Console.WriteLine(
                AutoStart.Current() is null
                    ? $"已删除注册表自启项（原先指向 {before}）。"
                    : $"注册表自启项删除失败，请手动检查：{before}"
            );
        }

        // 4) 数据目录
        Console.WriteLine();
        Console.WriteLine($"配置与日志：{AppPaths.DataDirectory}");
        if (purge)
        {
            try
            {
                if (IoDirectory.Exists(AppPaths.DataDirectory))
                {
                    IoDirectory.Delete(AppPaths.DataDirectory, recursive: true);
                    Console.WriteLine("已删除（--purge）。");
                }
                else
                {
                    Console.WriteLine("不存在，无需删除。");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"删除失败：{ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("已保留。要连它一起删，重跑一次并加 --purge。");
        }

        // 截图是用户的东西，卸载不该碰。明确说出来，免得以为被一起删了。
        Console.WriteLine($"截图目录（你的文件，永不动）：{AppPaths.DefaultScreenshotDirectory}");
        Console.WriteLine();
        Console.WriteLine("自启已清干净，现在可以删除 PrismShot 的程序文件夹了。");
        return 0;
    }

    /// <summary>把命令行参数翻成统一的 IPC 请求。</summary>
    private static IpcRequest Translate(string[] args)
    {
        IpcRequest request = new();

        foreach (string argument in args)
        {
            switch (argument.TrimStart('-').ToLowerInvariant())
            {
                case "screen":
                case "full":
                case "fullscreen":
                    request.Mode = "screen";
                    break;

                case "window":
                    request.Mode = "window";
                    break;

                case "freeform":
                case "polygon":
                    request.Mode = "freeform";
                    break;

                case "repeat":
                    request.Mode = "repeat";
                    break;

                case "ping":
                    request.Action = IpcActions.Ping;
                    break;

                case "reload":
                    request.Action = IpcActions.Reload;
                    break;

                case "quit":
                case "exit":
                    request.Action = IpcActions.Quit;
                    break;

                case "nosave":
                    request.NoSave = true;
                    break;

                case "noclipboard":
                    request.SkipClipboard = true;
                    break;
            }
        }

        if (request.Action != IpcActions.Capture)
        {
            return request;
        }

        request.OutputPath = GetArgument(args, "--out");
        request.Format = GetArgument(args, "--format");

        if (int.TryParse(GetArgument(args, "--quality"), out int quality))
        {
            request.Quality = quality;
        }

        return request;
    }

    private static int Version()
    {
        // Full 而不是 Version：只报 0.1.0 就看不出这是 alpha 还是 beta，
        // 而「我装的到底是哪个版本」恰恰是排障时第一个要问的问题。
        Console.WriteLine($"PrismShot {BuildStamp.Full}");
        return 0;
    }

    private static int Probe(string[] args)
    {
        string output = GetArgument(args, "--out")
            ?? Path.Combine(AppPaths.DataDirectory, "probe.txt");

        List<string> lines = new();
        Rect32 virtualScreen = Win32.GetVirtualScreen();
        lines.Add($"OS build           : {Environment.OSVersion.Version}");
        lines.Add($"Float capture      : {MonitorEnumerator.SupportsFloatCapture}");
        lines.Add($"Virtual screen     : {virtualScreen}");
        lines.Add(string.Empty);

        foreach (DisplayTarget target in MonitorEnumerator.Enumerate())
        {
            AdvancedColorProbe? probe = DisplayConfig.Probe(target.DeviceName);
            lines.Add($"Display            : {target.DeviceName}");
            lines.Add($"  bounds           : {target.Bounds}");
            lines.Add($"  scale            : {target.Scale:0.##}");
            lines.Add($"  primary          : {target.IsPrimary}");
            lines.Add($"  hdr enabled      : {target.IsHdrEnabled}");
            lines.Add($"  hdr supported    : {probe?.SupportsHdr.ToString() ?? "-"}");
            lines.Add($"  bits per color   : {probe?.BitsPerColor.ToString() ?? "-"}");
            lines.Add(
                $"  sdr white nits   : {target.SdrWhiteLevelNits.ToString("0.##", CultureInfo.InvariantCulture)}"
            );
            lines.Add($"  color info known : {target.IsColorInfoKnown}");
            lines.Add($"  capture as float : {target.ShouldCaptureAsFloat}");
            lines.Add(string.Empty);
        }

        WriteReport(output, lines);
        return 0;
    }

    private static int Capture(string[] args)
    {
        string output = GetArgument(args, "--out")
            ?? Path.Combine(AppPaths.DataDirectory, "capture.png");
        ImageOutputFormat format = ImageWriter.Parse(
            GetArgument(args, "--format") ?? Path.GetExtension(output).TrimStart('.')
        );
        int quality = int.TryParse(GetArgument(args, "--quality"), out int parsed)
            ? parsed
            : 92;

        IoDirectory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");

        ScreenGrabber grabber = new(
            CaptureEngines.Parse(
                GetArgument(args, "--engine") ?? SettingsStore.Load().Behavior.CaptureEngine
            ),
            TimeSpan.FromSeconds(5)
        );
        using (grabber)
        {
            CaptureBundle bundle = grabber.CaptureAsync().GetAwaiter().GetResult();
            if (bundle.Captures.Count == 0)
            {
                Console.WriteLine("capture failed: no display captured");
                Log.Error("自测抓帧：一块屏都没抓到。");
                return 1;
            }

            Raster composite = bundle.ComposeVirtualScreen();
            string saved = ImageWriter.Save(composite, output, quality);

            List<string> lines = new()
            {
                $"status             : ok",
                $"file               : {saved}",
                $"size               : {composite.Width}x{composite.Height}",
                $"bytes              : {new FileInfo(saved).Length}",
                $"pipeline           : {(bundle.UsedHdrPipeline ? "WGC" : "GDI 兜底")}",
                $"real hdr content   : {bundle.AnyHdrContent}",
                $"displays           : {bundle.Captures.Count}",
            };
            foreach (DisplayCapture capture in bundle.Captures)
            {
                lines.Add(
                    $"  {capture.Target.DeviceName} {capture.Image.Width}x{capture.Image.Height} hdr={capture.HasHdrContent} white={capture.Target.SdrWhiteLevelNits:0}"
                );
            }

            WriteReport(Path.ChangeExtension(saved, ".txt"), lines);
            Console.WriteLine($"saved: {saved}");
        }

        return 0;
    }

    private static void WriteReport(string path, List<string> lines)
    {
        File.WriteAllLines(path, lines);
        Console.WriteLine($"report: {path}");
    }

    private static string? GetArgument(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
