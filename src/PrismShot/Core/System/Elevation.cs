using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using PrismShot.Core;

namespace PrismShot.Core.Platform;

/// <summary>
/// 「以管理员身份常驻」这件事的全部实现。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：Windows 的 UIPI 规定，普通权限进程装的低级键盘钩子，在**前台窗口属于
/// 更高完整性进程时不会被调用**。任务管理器、注册表编辑器、任何「以管理员身份运行」的程序
/// 在前台时，我们的钩子就收不到键 —— 于是 Win+Shift+S 落到系统原生截图工具手里。
/// 用户看到的正是这个：只有任务管理器成为焦点时，按 Win+Shift+S 会弹出 Windows 自带的截图。
/// </para>
/// <para>
/// 让进程自己提权有两条路。写进清单（<c>requireAdministrator</c>）最直接，但每次双击都要过 UAC，
/// 开机自启也会弹 —— 一个截图工具天天要人点确认，比它要解决的问题还烦。
/// 所以这里走第二条：注册一个「登录时触发、以最高权限运行」的计划任务。
/// 注册那一下需要一次 UAC，之后无论开机自启还是手动重启，都由任务计划程序以管理员身份
/// 静默拉起，不再有任何弹窗。
/// </para>
/// <para>
/// 用 XML 而不是 <c>schtasks /TR</c> 注册，是因为 <c>/TR</c> 的参数里要嵌一层引号，
/// 而程序路径一旦带空格（<c>C:\Program Files\...</c>，这是常态）就会散架；
/// XML 里路径是独立元素，不存在转义问题，也能顺手把「执行时间无上限」这类默认值写死 ——
/// 常驻程序被任务计划的默认 72 小时上限杀掉，是很难查的一类怪事。
/// </para>
/// </remarks>
internal static class Elevation
{
    /// <summary>计划任务名。任务计划程序里看到的就是它。</summary>
    public const string TaskName = "PrismShot";

    /// <summary>由计划任务拉起时会带上的参数（用于等待上一个实例让出互斥体）。</summary>
    public const string FromTaskFlag = "--from-task";

    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(
        IntPtr process,
        uint desiredAccess,
        out IntPtr token
    );

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr token,
        int informationClass,
        out int information,
        int informationLength,
        out int returnLength
    );

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>本进程现在是不是管理员。</summary>
    public static bool IsElevated
    {
        get
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
                {
                    return false;
                }

                return GetTokenInformation(token, TokenElevationClass, out int elevated, sizeof(int), out _)
                    && elevated != 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (token != IntPtr.Zero)
                {
                    CloseHandle(token);
                }
            }
        }
    }

    /// <summary>那条最高权限的计划任务在不在。</summary>
    public static bool IsTaskRegistered()
    {
        if (!AppPaths.IsInstalled)
        {
            return false;
        }

        return RunSchtasks($"/Query /TN \"{TaskName}\"", elevate: false, out _) == 0;
    }

    /// <summary>
    /// 注册（或更新）最高权限计划任务。会弹一次 UAC —— 这是整个流程里唯一一次。
    /// </summary>
    public static bool Register(out string message)
    {
        if (!AppPaths.IsInstalled)
        {
            message = "便携模式下不注册计划任务。";
            return false;
        }

        string executable = AppPaths.ExecutablePath;
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
        {
            message = "找不到自身可执行文件路径，无法注册计划任务。";
            return false;
        }

        string xmlPath;
        try
        {
            xmlPath = Path.Combine(Path.GetTempPath(), "prismshot-task.xml");
            File.WriteAllText(xmlPath, BuildTaskXml(executable), new System.Text.UnicodeEncoding());
        }
        catch (Exception ex)
        {
            message = $"写任务定义失败：{ex.Message}";
            return false;
        }

        int exit = RunSchtasks(
            $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F",
            elevate: true,
            out string output
        );

        TryDelete(xmlPath);

        if (exit == 0)
        {
            message = $"已注册计划任务「{TaskName}」（最高权限），开机自启与手动启动都不再弹 UAC。";
            return true;
        }

        message = IsElevated
            ? $"注册计划任务失败（exit {exit}）：{Trim(output)}"
            : $"未获管理员授权，计划任务没有注册。{Trim(output)}";
        return false;
    }

    /// <summary>删掉计划任务，回到注册表 Run 键那条普通权限的自启。</summary>
    public static bool Unregister(out string message)
    {
        int exit = RunSchtasks($"/Delete /TN \"{TaskName}\" /F", elevate: true, out string output);
        if (exit == 0)
        {
            message = $"已删除计划任务「{TaskName}」。";
            return true;
        }

        message = $"删除计划任务失败（exit {exit}）：{Trim(output)}";
        return false;
    }

    /// <summary>
    /// 让计划任务现在就把常驻实例拉起来（管理员身份，不需要 UAC）。
    /// </summary>
    /// <remarks>
    /// 这是整个方案能「一次授权、长期免打扰」的关键：任务由 Task Scheduler 服务（SYSTEM）
    /// 拉起，用的是任务里写死的最高权限令牌，不在用户会话里走一遍提权，所以不会弹窗。
    /// </remarks>
    public static bool StartViaTask(out string message)
    {
        int exit = RunSchtasks($"/Run /TN \"{TaskName}\"", elevate: false, out string output);
        message = exit == 0 ? "已交给计划任务以管理员身份启动。" : $"启动计划任务失败：{Trim(output)}";
        return exit == 0;
    }

    /// <summary>
    /// 任务定义。几个关键项：
    /// <list type="bullet">
    /// <item><c>RunLevel = HighestAvailable</c> —— 管理员令牌的来源。</item>
    /// <item><c>LogonType = InteractiveToken</c> —— 用当前用户的交互式令牌，不需要存密码。</item>
    /// <item><c>ExecutionTimeLimit = PT0S</c> —— 不限执行时间。常驻程序被默认的 72 小时上限杀掉
    /// 会表现为「用几天之后热键突然全都不灵了」，极难排查。</item>
    /// <item><c>MultipleInstancesPolicy = IgnoreNew</c> —— 不重复拉起，和单实例互斥体双保险。</item>
    /// </list>
    /// </summary>
    private static string BuildTaskXml(string executable)
    {
        string user = string.Format(
            CultureInfo.InvariantCulture,
            @"{0}\{1}",
            Environment.UserDomainName,
            Environment.UserName
        );

        // 路径和用户名都可能带 & < > 之类字符（用户名里出现过 & 的机器是真实存在的），
        // 直接拼进 XML 会让整份任务定义解析失败，而且报错发生在 UAC 之后 —— 白弹一次。
        string command = SecurityElement.Escape(executable) ?? executable;
        string account = SecurityElement.Escape(user) ?? user;

        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>PrismShot 常驻实例（管理员权限）。前台是管理员窗口时，低级键盘钩子才会被调用，Win+Shift+S 才能被接管。</Description>
    <Author>{account}</Author>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{account}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{account}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{command}</Command>
      <Arguments>{FromTaskFlag}</Arguments>
    </Exec>
  </Actions>
</Task>
""";
    }

    /// <summary>
    /// 跑一次 schtasks。<paramref name="elevate"/> 为 true 时通过 ShellExecute 的 runas 动词启动，
    /// 会弹一次 UAC —— 只有注册/删除任务需要。
    /// </summary>
    private static int RunSchtasks(string arguments, bool elevate, out string output)
    {
        ProcessStartInfo start = new("schtasks.exe", arguments)
        {
            UseShellExecute = elevate,
            CreateNoWindow = !elevate,
            RedirectStandardOutput = !elevate,
            RedirectStandardError = !elevate,
        };

        if (elevate)
        {
            start.Verb = "runas";
        }

        output = string.Empty;
        try
        {
            using Process? process = Process.Start(start);
            if (process is null)
            {
                return -1;
            }

            if (!elevate)
            {
                output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            }

            process.WaitForExit(elevate ? 60_000 : 15_000);
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (Exception ex)
        {
            // 用户在 UAC 上点了「否」也走这里（Win32Exception 1223 = 操作已被用户取消）。
            output = ex.Message;
            return -1;
        }
    }

    private static string Trim(string text)
    {
        string single = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return single.Length <= 240 ? single : single[..240];
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 临时文件删不掉不是错误。
        }
    }
}
