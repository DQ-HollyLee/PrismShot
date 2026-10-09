using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using PrismShot.Core;
using PrismShot.Core.Capture;
using PrismShot.Core.Imaging;
using PrismShot.Core.Interop;
using IoDirectory = System.IO.Directory;

namespace PrismShot;

/// <summary>
/// 命令行入口。
/// </summary>
/// <remarks>
/// 三条通道这里分岔：
/// <list type="number">
/// <item>无参数 → 常驻模式，进入 <c>App</c> 接管热键。</item>
/// <item><c>--capture</c> → 抓一张并存盘，给 StarPie 插件和自测用。</item>
/// <item><c>--probe</c> → 把显示器的 HDR 状态写成文本，排障用。</item>
/// </list>
/// 命令行通道是刻意留的产品能力而不是临时脚手架：插件要靠它调用截图，
/// 而用户报「HDR 截图不对」时，第一件事就是让他跑一次 <c>--probe</c> 看看 WhiteLevel 到底多少。
/// </remarks>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        AppPaths.EnsureCreated();
        Log.Prune();

        string command = args.Length > 0 ? args[0] : string.Empty;
        try
        {
            return command switch
            {
                "--capture" => Capture(args),
                "--probe" => Probe(args),
                "--version" => Version(),
                _ => LaunchResidentMode(),
            };
        }
        catch (Exception ex)
        {
            Log.Error("命令行执行失败。", ex);
            return 2;
        }
    }

    /// <summary>常驻模式。App 一旦就位，这里换成 <c>App.Main</c>。</summary>
    private static int LaunchResidentMode()
    {
        App app = new();
        return app.Run();
    }

    private static int Version()
    {
        Console.WriteLine($"PrismShot {BuildStamp.Version}");
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
            ?? Path.Combine(AppPaths.DataDirectory, "capture.jpg");
        ImageOutputFormat format = ImageWriter.Parse(
            GetArgument(args, "--format") ?? Path.GetExtension(output).TrimStart('.')
        );
        int quality = int.TryParse(GetArgument(args, "--quality"), out int parsed)
            ? parsed
            : 92;

        IoDirectory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");

        ScreenGrabber grabber = new(TimeSpan.FromSeconds(5));
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
