using System;
using System.IO;
using System.Reflection;

namespace PrismShot.Core;

/// <summary>
/// 路径集中地。所有路径从这里出，别的地方不许自己拼。
/// </summary>
/// <remarks>
/// 便携模式：程序目录里放一个 <c>portable.txt</c>，配置、日志、默认截图就全部落在程序目录下的
/// <c>Data\</c>。这样拷整个文件夹到 U 盘就能带走，不用先「卸载」掉写进 AppData 的东西。
/// 便携与否在进程启动时定死，中途查看 InstallKind 不会改变 —— 路径一旦在飞行中变化，
/// 排查问题时连日志在哪都搞不清。
/// </remarks>
internal static class AppPaths
{
    private static readonly Lazy<InstallKind> Kind = new(DetectKind);

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location ?? string.Empty;

    public static string AppDirectory => AppContext.BaseDirectory;

    public static InstallKind Current => Kind.Value;

    /// <summary>常规安装：数据进 AppData，可以写注册表自启、可以注册计划任务。</summary>
    public static bool IsInstalled => Current == InstallKind.Installed;

    /// <summary>用户数据目录（配置、日志、插件数据）。</summary>
    public static string DataDirectory =>
        Current == InstallKind.Portable
            ? Path.Combine(AppDirectory, "Data")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PrismShot"
            );

    public static string ConfigPath => Path.Combine(DataDirectory, "settings.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string ThumbnailCacheDirectory => Path.Combine(DataDirectory, "recent");

    /// <summary>默认保存目录。用户没在配置里指定时使用。</summary>
    public static string DefaultScreenshotDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PrismShot");

    public static string PortableFlagPath => Path.Combine(AppDirectory, "portable.txt");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    private static InstallKind DetectKind()
    {
        try
        {
            return File.Exists(PortableFlagPath) ? InstallKind.Portable : InstallKind.Installed;
        }
        catch
        {
            return InstallKind.Installed;
        }
    }
}

internal enum InstallKind
{
    /// <summary>写注册表自启、数据进 AppData 的常规安装。</summary>
    Installed,

    /// <summary>文件夹整体可搬运的模式，不写注册表。</summary>
    Portable,
}
