using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PrismShot.Core;
using PrismShot.Core.Interop;
using PrismShot.Core.Ipc;
using PrismShot.Core.Platform;
using PrismShot.Ui;

namespace PrismShot;

/// <summary>
/// 热键背后的意图。和配置文件里的几个热键槽一一对应。
/// </summary>
internal enum Trigger
{
    None = 0,
    Region,
    FullScreen,
    Window,
    RepeatLast,
}

/// <summary>
/// WPF 应用对象。常驻模式下的生命周期主人。
/// </summary>
/// <remarks>
/// <para>
/// 常态下一个窗口都不存在，进程里只剩「一根钩子 + 一个管道服务端 + 一段已预热的抓帧路径」。
/// 没有托盘图标（默认）、没有任务栏按钮、也没有常驻窗口句柄 ——
/// 这是需求里「存在感要和 Windows 自带截图工具一样低」的具体落地。
/// </para>
/// <para>
/// 也因此，退出要靠命令行 <c>PrismShot.exe --quit</c> 或任务管理器。
/// 常驻软件给不出合适的关闭入口是设计缺陷，但对「本就不该被察觉」的这一类，
/// 加一个需要用户每天看见的图标是更大的缺陷。
/// </para>
/// </remarks>
internal sealed class App : Application
{
    private AppSettings _settings = new();
    private CaptureSession? _session;
    private KeyboardHook? _hook;
    private IpcServer? _server;
    private readonly Dictionary<string, Trigger> _intents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>同一时刻只允许一次截图在跑。连按两次不该叠出两层覆盖层。</summary>
    private int _busy;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureCreated();
        Log.Prune();
        Log.Info($"PrismShot {BuildStamp.Full} 启动（{AppPaths.Current}）。");

        // 没有主窗口，不能让 WPF 自动判断什么时候该收摊。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _settings = SettingsStore.Load();
        SettingsStore.Save(_settings);

        _hook = new KeyboardHook();
        _hook.Triggered += OnHotkey;
        _hook.Start();
        ApplyBindings();

        _session = new CaptureSession(_settings, _hook, Dispatcher);
        _session.Prewarm();

        _server = new IpcServer(HandleCommandAsync);
        _server.Start();

        ApplyAutoStart();

        Log.Info("已进入常驻状态，等待热键。");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _server?.Dispose();
        _hook?.Dispose();
        _session?.Dispose();

        Log.Info("PrismShot 退出。");
        base.OnExit(e);
    }

    // ---------------- 热键 ----------------

    /// <summary>把配置里的四个热键翻译给钩子，同时记住每个组合背后是哪个意图。</summary>
    private void ApplyBindings()
    {
        _intents.Clear();

        List<HotkeyBinding> list = new();
        Register(list, _settings.Hotkeys.Region, Trigger.Region);
        Register(list, _settings.Hotkeys.FullScreen, Trigger.FullScreen);
        Register(list, _settings.Hotkeys.Window, Trigger.Window);
        Register(list, _settings.Hotkeys.RepeatLastRegion, Trigger.RepeatLast);

        _hook?.UpdateBindings(list);
    }

    private void Register(List<HotkeyBinding> list, string text, Trigger intent)
    {
        HotkeyBinding binding = HotkeyBinding.Parse(text);
        if (binding.IsEmpty)
        {
            return;
        }

        list.Add(binding);
        _intents[binding.Text] = intent;
    }

    private void OnHotkey(HotkeyBinding binding)
    {
        _intents.TryGetValue(binding.Text, out Trigger intent);
        if (intent == Trigger.None)
        {
            return;
        }

        Log.Debug($"热键命中：{binding} → {intent}");

        IpcRequest request = new() { Mode = ModeTextOf(intent) };
        _ = RunAsync(request, intent == Trigger.RepeatLast);
    }

    private static string ModeTextOf(Trigger intent) =>
        intent switch
        {
            Trigger.FullScreen => "screen",
            Trigger.Window => "window",
            _ => "region",
        };

    // ----------------  IPC ----------------

    private async Task<IpcResponse> HandleCommandAsync(IpcRequest request)
    {
        switch (request.Action)
        {
            case IpcActions.Ping:
                return new IpcResponse { Ok = true };

            case IpcActions.Reload:
            {
                await Dispatcher
                    .InvokeAsync(() =>
                    {
                        _settings = SettingsStore.Load();
                        ApplyBindings();
                        ApplyAutoStart();
                    })
                    .Task;
                return new IpcResponse { Ok = true };
            }

            case IpcActions.Quit:
                _ = Dispatcher.BeginInvoke((Action)(() => Shutdown()));
                return new IpcResponse { Ok = true };

            case IpcActions.Capture:
            {
                bool repeat = string.Equals(
                    request.Mode,
                    "repeat",
                    StringComparison.OrdinalIgnoreCase
                );
                return await RunAsync(request, repeat).ConfigureAwait(false);
            }

            default:
                return IpcResponse.Failed($"未知动作：{request.Action}");
        }
    }

    private async Task<IpcResponse> RunAsync(IpcRequest request, bool repeatLast)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            Log.Debug("已有一次截图在进行，本次请求被忽略。");
            return IpcResponse.Failed("上一次截图尚未结束。");
        }

        try
        {
            CaptureSession? session = _session;
            if (session is null)
            {
                return IpcResponse.Failed("截图会话尚未就绪。");
            }

            if (repeatLast)
            {
                return session.TryGetLastRegion(out Rect32 last)
                    ? await session.ExecuteAtAsync(last, request).ConfigureAwait(false)
                    : IpcResponse.Failed("还没有上一次的选区。");
            }

            return await session
                .ExecuteAsync(request, MapMode(request.Mode))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("执行截图失败。", ex);
            return IpcResponse.Failed(ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            ReleaseFrameMemory();
        }
    }

    /// <summary>
    /// 一次截图结束后立刻把大块像素缓冲还回去。
    /// </summary>
    /// <remarks>
    /// 每帧回读出来的像素、色调映射的中间结果、裁剪出的选区，都是十几 MB 起步的
    /// 大对象堆分配。常驻进程一旦空闲下来，GC 就再也不主动碰它们了 ——
    /// 于是工作集稳稳停在几百 MB 甚至一个 GB 上下不来，而实际上那些图早就用不上了。
    /// <para>
    /// 放在「一次截图彻底结束」这个明确的时点上收一次，比让 GC 自己挑时机靠谱得多：
    /// 代价是一次几十毫秒的停顿，而它发生在用户已经拿到图之后，感觉不到。
    /// </para>
    /// </remarks>
    private static void ReleaseFrameMemory()
    {
        // 只有工作集真的一路涨上去了才动手。
        // 强制 Gen2 是 stop-the-world 的，每张图都来一次等于给每次截图
        // 都添一次看得见的停顿 —— 平时那几块缓冲让 GC 自己找时机收就够了。
        long threshold = 256L * 1024 * 1024;
        if (Environment.WorkingSet < threshold)
        {
            return;
        }

        try
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            Log.Info($"已回收截图缓冲，工作集 {Environment.WorkingSet / (1024 * 1024)} MB。");
        }
        catch (Exception ex)
        {
            Log.Debug($"回收截图缓冲时忽略异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 把请求里的 mode 文本翻成内部枚举。
    /// 认不出来时一律按「矩形区域」处理 —— 这是最常用也最不容易误会的一种。
    /// </summary>
    private SelectionMode MapMode(string? mode)
    {
        return string.IsNullOrWhiteSpace(mode)
            ? DefaultMode()
            : mode.ToLowerInvariant() switch
            {
                "screen" or "fullscreen" or "full" => SelectionMode.FullScreen,
                "window" => SelectionMode.Window,
                "freeform" or "polygon" => SelectionMode.Freeform,
                "rect" or "region" or "area" => SelectionMode.Rect,
                _ => DefaultMode(),
            };
    }

    private SelectionMode DefaultMode() =>
        _settings.Behavior.DefaultSelectionMode.ToLowerInvariant() switch
        {
            "freeform" => SelectionMode.Freeform,
            "window" => SelectionMode.Window,
            "fullscreen" or "full" => SelectionMode.FullScreen,
            _ => SelectionMode.Rect,
        };

    // ---------------- 自启 ----------------

    private void ApplyAutoStart()
    {
        if (!AutoStart.IsSupported)
        {
            return;
        }

        try
        {
            bool desired = _settings.Behavior.LaunchOnBoot;
            if (AutoStart.IsEnabled() != desired)
            {
                _ = AutoStart.Apply(desired);
                Log.Info($"开机自启已设为 {desired}。");
            }
        }
        catch (Exception ex)
        {
            Log.Error("设置开机自启失败。", ex);
        }
    }
}
