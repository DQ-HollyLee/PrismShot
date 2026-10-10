using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace PrismShot.Setup;

/// <summary>
/// PrismShot 的安装 / 卸载程序。同一个 exe 扮演两个角色：装的时候叫
/// <c>PrismShot.Setup.exe</c>，装完它被复制一份到安装目录里叫 <c>uninstall.exe</c>。
/// </summary>
/// <remarks>
/// <para>
/// 为什么自己写而不是引 Inno Setup / NSIS：那两家都是好东西，但它们是<b>构建机上的外部依赖</b>，
/// 而这个项目要求「拿到仓库就能构建」。多一个必须事先装好的工具链，就多一条
/// 「新人克隆下来发现装不了」的裂缝。安装器本身只做四件很确定的事 —— 解包、写卸载项、
/// 建开始菜单快捷方式、叫起程序 —— 自己写比引第三方更可控。
/// </para>
/// <para>
/// 为什么是<b>每用户</b>安装（<c>%LOCALAPPDATA%\Programs\PrismShot</c>）而不是 Program Files：
/// 这个程序的数据、配置、日志本来就都在用户目录下，装到 Program Files 只会换来一次
/// 毫无必要的 UAC，而它自己要提权的地方（热键要在管理员窗口上生效）另有
/// <c>--elevate</c> 那条路，且只需要弹一次。
/// </para>
/// </remarks>
internal static class Program
{
    private const string ProductName = "PrismShot";
    private const string UninstallerName = "uninstall.exe";
    private const string PayloadResourceName = "payload.zip";
    private const string UninstallRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PrismShot";

    private static int Main(string[] args)
    {
        TryUseUtf8();

        if (!Options.TryParse(args, out Options options, out string? error))
        {
            Console.WriteLine(error);
            Console.WriteLine();
            Console.WriteLine(Options.Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(Options.Usage);
            return 0;
        }

        return options.Uninstall ? Uninstall(options) : Install(options);
    }

    // ---------------------------------------------------------------- 安装

    private static int Install(Options options)
    {
        string directory = options.Directory ?? DefaultInstallDirectory();

        Console.WriteLine($"PrismShot 安装程序 —— 版本 {OwnVersion()}");
        Console.WriteLine($"安装到：{directory}");
        Console.WriteLine();

        if (!AskRunningInstanceToQuit(directory, "覆盖安装"))
        {
            return 1;
        }

        string staging = Path.Combine(
            Path.GetTempPath(),
            $"PrismShot-Setup-{DateTime.Now:yyyyMMddHHmmss}"
        );

        try
        {
            Directory.CreateDirectory(staging);
            ExtractPayload(staging);
            CopyTree(staging, directory);
            Console.WriteLine("  程序文件已就位。");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"!! 解包失败：{ex.Message}");
            return 1;
        }
        finally
        {
            TryDeleteDirectory(staging);
        }

        // 卸载程序必须是安装目录里一个真实存在的 exe ——
        // 「设置 → 应用」和开始菜单都靠它，而它不能只活在下载的那个安装包里。
        CopySelfAsUninstaller(directory);
        Console.WriteLine($"  已在安装目录留下卸载程序：{UninstallerName}");

        WriteUninstallRegistration(directory);
        Console.WriteLine("  已在「设置 → 应用」登记卸载项。");

        CreateStartMenuShortcuts(directory);

        string executable = Path.Combine(directory, "PrismShot.exe");
        if (!options.NoElevate)
        {
            Console.WriteLine();
            Console.WriteLine("接着会弹一次 UAC —— 只有这一次。批准之后 PrismShot 以管理员常驻，");
            Console.WriteLine("任务管理器这类管理员窗口在最前面时，Win+Shift+S 也不会被系统抢走。");
            Run(executable, "--elevate");
        }
        else
        {
            Run(executable, null);
        }

        Console.WriteLine();
        Console.WriteLine("安装完成。");
        if (options.NoElevate)
        {
            Console.WriteLine("注意：这次跳过了提权，管理员窗口在前台时热键仍可能被系统抢走。");
            Console.WriteLine($"      想补上就跑一次：\"{executable}\" --elevate");
        }

        Pause(options);
        return 0;
    }

    // ---------------------------------------------------------------- 卸载

    private static int Uninstall(Options options)
    {
        string? directory = ResolveInstallDirectory();

        if (directory is null)
        {
            Console.Error.WriteLine("!! 找不到 PrismShot 的安装位置。");
            Console.Error.WriteLine("   若它是便携版，直接删整个文件夹即可 —— 便携版不往系统里写任何东西。");
            return 1;
        }

        Console.WriteLine($"卸载 PrismShot —— {directory}");
        Console.WriteLine();

        // 让 PrismShot 自己先撤掉它的两条自启（注册表 Run 与计划任务）并退出。
        // 顺序不能反：它还活着的话，下一次启动自检会把自启原样写回去。
        string executable = Path.Combine(directory, "PrismShot.exe");
        if (File.Exists(executable))
        {
            Run(executable, "--uninstall");
            WaitForExit("PrismShot", TimeSpan.FromSeconds(8));
            Console.WriteLine("  常驻实例已退出，开机自启已撤销。");
        }

        RemoveStartMenuShortcuts();
        RemoveUninstallRegistration();
        Console.WriteLine("  已取消「设置 → 应用」里的登记。");

        // 先删内容，最后再处理自己所在的目录 —— 正在运行的 exe 删不掉自己。
        string self = Environment.ProcessPath ?? "";
        int removed = DeleteInstalledFiles(directory, skip: self);
        Console.WriteLine($"  已删除 {removed} 个文件。");

        if (options.Purge)
        {
            string data = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductName
            );
            if (Directory.Exists(data))
            {
                TryDeleteDirectory(data);
                Console.WriteLine($"  已删除配置与日志：{data}");
            }
        }
        else
        {
            string data = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductName
            );
            Console.WriteLine($"  配置与日志保留在：{data}");
            Console.WriteLine("  （要一起删就加 --purge。截图目录永远不动。）");
        }

        ScheduleSelfRemoval(directory, self, options);

        Console.WriteLine();
        Console.WriteLine("卸载完成。");
        Pause(options);
        return 0;
    }

    // ---------------------------------------------------------------- 步骤

    private static string DefaultInstallDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            ProductName
        );

    /// <summary>从嵌入资源里取出打包好的程序内容。</summary>
    private static void ExtractPayload(string destination)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        using Stream? stream = assembly.GetManifestResourceStream(PayloadResourceName);

        // 少了它不是「装了个空壳」那么简单 —— 用户会以为装好了，
        // 于是必须在这里就喊停，而不是让后面去撞一个不存在的文件。
        if (stream is null)
        {
            throw new InvalidOperationException(
                "这个安装程序里没有打包内容（payload.zip）。它是构建时打进去的，"
                + "请确认是用 build.ps1 构建而不是直接 dotnet build 这个工程。"
            );
        }

        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        archive.ExtractToDirectory(destination, overwriteFiles: true);
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.GetFiles(source))
        {
            string target = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
        }

        foreach (string folder in Directory.GetDirectories(source))
        {
            CopyTree(folder, Path.Combine(destination, Path.GetFileName(folder)));
        }
    }

    private static void CopySelfAsUninstaller(string directory)
    {
        string? self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self))
        {
            return;
        }

        string target = Path.Combine(directory, UninstallerName);
        if (string.Equals(self, target, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        File.Copy(self, target, overwrite: true);
    }

    /// <summary>
    /// 写「设置 → 应用」能看见的那条卸载登记。
    /// </summary>
    /// <remarks>
    /// 写在 HKCU 而不是 HKLM：这是一次每用户安装，写 HKLM 要管理员权限，
    /// 而这里的安装恰恰<b>不该</b>要管理员权限 —— 两件事分开，各自只要它该要的那一点。
    /// </remarks>
    private static void WriteUninstallRegistration(string directory)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallRegistryPath);

        string uninstaller = Path.Combine(directory, UninstallerName);

        key.SetValue("DisplayName", "PrismShot（棱采）");
        key.SetValue("DisplayVersion", OwnVersion());
        key.SetValue("Publisher", "PrismShot");
        key.SetValue("InstallLocation", directory);
        key.SetValue("DisplayIcon", Path.Combine(directory, "PrismShot.exe") + ",0");
        key.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" --uninstall --silent");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(DirectorySize(directory) / 1024), RegistryValueKind.DWord);
        key.SetValue("HelpLink", "https://github.com/prismshot/PrismShot");
    }

    private static void RemoveUninstallRegistration()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  （清理卸载登记时出错，不影响卸载：{ex.Message}）");
        }
    }

    /// <summary>卸载时该删哪个目录：优先认自己所在的目录，其次认注册表里记的。</summary>
    private static string? ResolveInstallDirectory()
    {
        string here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (File.Exists(Path.Combine(here, "PrismShot.exe")))
        {
            return here;
        }

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UninstallRegistryPath);
        string? recorded = key?.GetValue("InstallLocation") as string;
        return string.IsNullOrWhiteSpace(recorded) || !Directory.Exists(recorded) ? null : recorded;
    }

    private static void CreateStartMenuShortcuts(string directory)
    {
        string? folder = StartMenuFolder();
        if (folder is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);

            Shortcut.Create(
                Path.Combine(folder, "PrismShot.lnk"),
                Path.Combine(directory, "PrismShot.exe"),
                workingDirectory: directory,
                description: "棱采 —— 按 Win+Shift+S 截图"
            );

            Shortcut.Create(
                Path.Combine(folder, "卸载 PrismShot.lnk"),
                Path.Combine(directory, UninstallerName),
                arguments: "--uninstall",
                workingDirectory: directory,
                description: "卸载棱采"
            );

            Console.WriteLine($"  开始菜单：{folder}");
        }
        catch (Exception ex)
        {
            // 快捷方式只是个入口，做不出来也不该让安装失败 ——
            // 程序本身靠热键活着，开始菜单是给「想找它」的人用的。
            Console.WriteLine($"  （开始菜单快捷方式没建成，不影响使用：{ex.Message}）");
        }
    }

    private static void RemoveStartMenuShortcuts()
    {
        string? folder = StartMenuFolder();
        if (folder is not null && Directory.Exists(folder))
        {
            TryDeleteDirectory(folder);
            Console.WriteLine("  已移除开始菜单项。");
        }
    }

    private static string? StartMenuFolder()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return root.Length == 0
            ? null
            : Path.Combine(root, "Microsoft", "Windows", "Start Menu", "Programs", ProductName);
    }

    /// <summary>删掉安装目录里的文件，跳过正在运行的这一个。返回删掉的个数。</summary>
    private static int DeleteInstalledFiles(string directory, string skip)
    {
        int removed = 0;

        foreach (string file in Directory.GetFiles(directory))
        {
            if (string.Equals(file, skip, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  （删不掉 {Path.GetFileName(file)}：{ex.Message}）");
            }
        }

        foreach (string folder in Directory.GetDirectories(directory))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  （删不掉 {Path.GetFileName(folder)}：{ex.Message}）");
            }
        }

        return removed;
    }

    /// <summary>
    /// 让安装目录连同卸载程序本身一起消失。
    /// </summary>
    /// <remarks>
    /// 正在运行的 exe 删不掉自己，这是 Windows 的规矩。做法是往临时目录放一个
    /// 自删的小脚本，等这个进程结束再动手。只有确认「自己就是装在那个目录里的
    /// uninstall.exe」时才做 —— 万一有人把这个 exe 拷到别处去双击，绝不能顺手删掉那个目录。
    /// </remarks>
    private static void ScheduleSelfRemoval(string directory, string self, Options options)
    {
        bool isTheInstalledUninstaller =
            self.Length > 0
            && string.Equals(Path.GetFileName(self), UninstallerName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Path.GetDirectoryName(self)?.TrimEnd(Path.DirectorySeparatorChar),
                directory.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase
            );

        if (!isTheInstalledUninstaller)
        {
            Console.WriteLine($"  目录 {directory} 里的 {UninstallerName} 已保留，可手动删除。");
            return;
        }

        string script = Path.Combine(Path.GetTempPath(), $"PrismShot-Cleanup-{Guid.NewGuid():N}.cmd");

        try
        {
            // ping -n 是老派但可靠的「等几秒」：cmd 里没有 sleep，
            // 而等的是这个进程退出、文件句柄放开，两秒足够。
            File.WriteAllText(
                script,
                "@echo off\r\n"
                    + "ping -n 3 127.0.0.1 >nul\r\n"
                    + $"rd /s /q \"{directory}\"\r\n"
                    + "del \"%~f0\"\r\n",
                Encoding.ASCII
            );

            Process.Start(
                new ProcessStartInfo(script)
                {
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                }
            );

            Console.WriteLine("  安装目录会在几秒后自行删除（含卸载程序本身）。");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  （没能安排自清理，请手动删除 {directory}：{ex.Message}）");
        }

        if (options.Silent)
        {
            return;
        }
    }

    // ---------------------------------------------------------------- 杂项

    /// <summary>
    /// 安装 / 覆盖安装前先请已经在跑的那个实例退出，否则文件被占着，一个都换不掉。
    /// </summary>
    private static bool AskRunningInstanceToQuit(string directory, string why)
    {
        if (!IsRunning("PrismShot"))
        {
            return true;
        }

        string executable = Path.Combine(directory, "PrismShot.exe");
        if (File.Exists(executable))
        {
            Console.WriteLine("  检测到 PrismShot 正在运行，请它先退出……");
            Run(executable, "--quit");
        }

        if (WaitForExit("PrismShot", TimeSpan.FromSeconds(8)))
        {
            return true;
        }

        Console.Error.WriteLine("!! PrismShot 仍在运行，文件被占用，无法" + why + "。");
        Console.Error.WriteLine("   请在任务管理器里结束 PrismShot.exe 后重试。");
        return false;
    }

    private static bool IsRunning(string name) => Process.GetProcessesByName(name).Length > 0;

    private static bool WaitForExit(string name, TimeSpan timeout)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (!IsRunning(name))
            {
                return true;
            }

            Thread.Sleep(200);
        }

        return !IsRunning(name);
    }

    private static void Run(string executable, string? arguments)
    {
        try
        {
            ProcessStartInfo info = new(executable) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                info.Arguments = arguments;
            }

            Process.Start(info);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  （没能启动 {Path.GetFileName(executable)}：{ex.Message}）");
        }
    }

    private static long DirectorySize(string directory)
    {
        long total = 0;
        try
        {
            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                total += new FileInfo(file).Length;
            }
        }
        catch
        {
            // 尺寸只是给「设置 → 应用」看的一个估算值，算不准无所谓。
        }

        return total;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // 临时目录 / 开始菜单，删不掉不值得打断流程。
        }
    }

    private static string OwnVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0";

    private static void Pause(Options options)
    {
        if (options.Silent)
        {
            return;
        }

        Console.WriteLine();
        Console.Write("按回车关闭……");
        Console.ReadLine();
    }

    private static void TryUseUtf8()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 输出被重定向时改不了编码，用默认的一样能看。
        }
    }
}
