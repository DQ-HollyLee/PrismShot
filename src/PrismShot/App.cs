using System;
using System.Windows;
using PrismShot.Core;

namespace PrismShot;

/// <summary>
/// WPF 应用对象。常驻模式下唯一的生命周期主人。
/// </summary>
/// <remarks>
/// 刻意不建主窗口：常态下一个窗口都不存在，进程里只剩下「一根钩子 + 一个管道服务端 + 一个 XAML 栈」。
/// 需要出脸的时候（覆盖层、设置页、结果缩略图）才现建，用完立刻 Dispose。
/// </remarks>
internal sealed class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureCreated();
        Log.Prune();
        Log.Info($"PrismShot {BuildStamp.Full} 启动（{AppPaths.Current}）。");

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("PrismShot 退出。");
        base.OnExit(e);
    }
}
