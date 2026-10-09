using System;
using System.Globalization;
using System.IO;
using System.Linq;
using IoDirectory = System.IO.Directory;

namespace PrismShot.Core;

/// <summary>
/// 极简本地日志。刻意不引任何日志框架：这个进程要常驻在后台，
/// 每多一个依赖就多一分占用和启动开销，而它输出的量级根本不需要异步队列。
/// </summary>
/// <remarks>
/// 日志是排障的唯一抓手。用户可以完全不知道它在跑，所以我一旦出错，
/// 必须有地方能事后查到现场。写盘失败一律吞掉 —— 日志不能反过来把截图搞崩。
/// </remarks>
internal static class Log
{
    private const long RotateThresholdBytes = 1024 * 1024;

    private static readonly object Gate = new();
    private static string? _cachedDirectory;

    public static string LogDirectory
    {
        get
        {
            _cachedDirectory ??= AppPaths.LogDirectory;
            return _cachedDirectory;
        }
    }

    public static void Debug(string message) => Write("DBG", message);

    public static void Info(string message) => Write("INF", message);

    public static void Warn(string message) => Write("WRN", message);

    public static void Error(string message) => Write("ERR", message);

    public static void Error(string message, Exception exception) =>
        Write("ERR", $"{message}{Environment.NewLine}{exception}");

    private static void Write(string level, string message)
    {
        try
        {
            string line =
                $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}"
                + $" [{level}] {message}";
            lock (Gate)
            {
                IoDirectory.CreateDirectory(LogDirectory);
                File.AppendAllText(CurrentFile(), line + Environment.NewLine);
            }
        }
        catch
        {
            // 日志本身永远不能成为失败原因。
        }
    }

    private static string CurrentFile()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return Path.Combine(LogDirectory, $"prismshot-{stamp}.log");
    }

    /// <summary>清理过期日志。只在启动后调用一次，避免每次截图都去扫目录。</summary>
    public static void Prune(int keepDays = 14)
    {
        try
        {
            if (!IoDirectory.Exists(LogDirectory))
            {
                return;
            }

            DateTime deadline = DateTime.Now - TimeSpan.FromDays(keepDays);
            foreach (string file in IoDirectory.EnumerateFiles(LogDirectory, "prismshot-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < deadline)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // 单个文件删不掉不影响其它文件。
                }
            }

            RotateIfHuge();
        }
        catch
        {
            // 同上。
        }
    }

    private static void RotateIfHuge()
    {
        string file = CurrentFile();
        FileInfo info = new(file);
        if (info.Exists && info.Length > RotateThresholdBytes)
        {
            File.Move(
                file,
                Path.Combine(
                    LogDirectory,
                    $"prismshot-{DateTime.Now:yyyyMMdd-HHmmss}.old.log"
                ),
                overwrite: true
            );
        }
    }
}
