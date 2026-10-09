namespace PrismShot.Core;

/// <summary>
/// 版本数字的单一入口。
/// </summary>
/// <remarks>
/// 版本号本来应该由 MSBuild 从 csproj 注入过来，但那样一来 <c>version.json</c> 就是装饰品了。
/// 这里反过来：csproj 里的三个 Version 字段由 <c>build.ps1</c> 读 version.json 写进去，
/// 运行时从这里读回 —— 链条单一方向，不会出现「安装包写 1.2.0、关于页显示 1.1.9」这种事。
/// </remarks>
internal static class BuildStamp
{
    public const string Version = "0.1.0";

    public const string Channel = "alpha";

    public const string DisplayName = "PrismShot";

    /// <summary>中文名。只在标题栏和 README 里露面。</summary>
    public const string Codename = "棱采";

    public static string Full =>
        string.Equals(Channel, "stable", global::System.StringComparison.Ordinal)
            ? Version
            : $"{Version}-{Channel}";
}
