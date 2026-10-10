namespace PrismShot.Setup;

/// <summary>安装器的命令行开关。</summary>
/// <remarks>
/// 开关刻意只有五个。安装器不是配置面板 —— 每多一个开关，就多一条「用户装的时候
/// 不知道该不该勾」的分叉，而真正需要调的东西（保存目录、格式、热键）都在
/// <c>settings.json</c> 里，那儿才是它该待的地方。
/// </remarks>
internal sealed class Options
{
    /// <summary>安装到哪。null 表示用默认位置。</summary>
    public string? Directory { get; private init; }

    public bool Uninstall { get; private init; }

    /// <summary>连配置与日志一起删。截图目录永远不动。</summary>
    public bool Purge { get; private init; }

    /// <summary>不问、不停。给「设置 → 应用」里点卸载那种场合用。</summary>
    public bool Silent { get; private init; }

    /// <summary>装完不以管理员常驻。</summary>
    public bool NoElevate { get; private init; }

    public bool ShowHelp { get; private init; }

    public static string Usage =>
        """
        PrismShot 安装程序

          PrismShot.Setup.exe                     安装到 %LOCALAPPDATA%\Programs\PrismShot
          PrismShot.Setup.exe --dir "<路径>"       安装到指定目录
          PrismShot.Setup.exe --no-elevate        装完不以管理员常驻（热键可能被管理员窗口抢走）

          uninstall.exe --uninstall [--purge]     卸载（安装目录里那个 uninstall.exe 就是本程序）

          --silent    不暂停、不询问
          --help      显示这段说明
        """;

    public static bool TryParse(string[] args, out Options options, out string? error)
    {
        error = null;
        string? directory = null;
        bool uninstall = false;
        bool purge = false;
        bool silent = false;
        bool noElevate = false;
        bool help = false;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];

            if (TryValue(argument, "--dir", ref index, args, ref directory, out error))
            {
                continue;
            }

            if (error is not null)
            {
                break;
            }

            switch (argument)
            {
                case "--uninstall":
                    uninstall = true;
                    break;

                case "--purge":
                    purge = true;
                    break;

                case "--silent":
                    silent = true;
                    break;

                case "--no-elevate":
                    noElevate = true;
                    break;

                case "--help" or "-h" or "/?":
                    help = true;
                    break;

                default:
                    error = $"不认识的开关：{argument}";
                    break;
            }

            if (error is not null)
            {
                break;
            }
        }

        options = new Options
        {
            Directory = directory,
            Uninstall = uninstall,
            Purge = purge,
            Silent = silent,
            NoElevate = noElevate,
            ShowHelp = help,
        };

        return error is null;
    }

    /// <summary>同时认 <c>--dir X</c> 与 <c>--dir=X</c> 两种写法。</summary>
    private static bool TryValue(
        string argument,
        string name,
        ref int index,
        string[] args,
        ref string? value,
        out string? error
    )
    {
        error = null;

        if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= args.Length)
            {
                error = $"{name} 后面要跟一个路径。";
                return true;
            }

            value = args[++index];
            return true;
        }

        if (
            argument.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)
            && argument.Length > name.Length + 1
        )
        {
            value = argument[(name.Length + 1)..];
            return true;
        }

        return false;
    }
}
