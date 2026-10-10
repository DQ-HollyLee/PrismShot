using System;

namespace StarPie.Plugin.PrismShot;

/// <summary>
/// 照搬过来的 OCR 源码里到处是 <c>AppLogger.LogXxx</c>，那是宿主的内部日志。
/// 这里是它的替身：把那些日志接到插件自己的日志上。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要一个可变静态槽，而不是把 <c>IPluginLogger</c> 一路传进
/// <see cref="Upstream.OcrManager"/>：传进去就要改它的每一个方法签名，
/// 而"照搬"的全部意义就在于<b>不改</b>。留一个槽，绑定一次，源码零改动。
/// </para>
/// <para>
/// 没绑定时所有日志静默丢弃，绝不写控制台也绝不抛 ——
/// 宿主加载插件的线程池里冒出一行野日志，比没有日志更难查。
/// </para>
/// </remarks>
internal static class OcrLog
{
    private static Action<string> _debug = _ => { };
    private static Action<string> _info = _ => { };
    private static Action<string> _warn = _ => { };
    private static Action<string, Exception?> _error = (_, _) => { };

    /// <summary>在插件初始化时调用一次，之后照搬源码里的日志就会落进宿主给的插件日志。</summary>
    public static void Bind(IPluginLogger? logger)
    {
        if (logger is null)
        {
            return;
        }

        _debug = logger.Debug;
        _info = logger.Info;
        _warn = logger.Warn;
        _error = (message, exception) =>
        {
            // 宿主把异常拼进 message，这里补上，免得日志里只剩一句消息没有堆栈。
            logger.Error(exception is null ? message : $"{message} | {exception}");
        };
    }

    public static void Debug(string message)
    {
        try
        {
            _debug(message);
        }
        catch
        {
            // 日志写失败不该让识别失败。
        }
    }

    public static void Info(string message) => Safe(() => _info(message));

    public static void Warn(string message) => Safe(() => _warn(message));

    public static void Error(string message, Exception? exception = null) =>
        Safe(() => _error(message, exception));

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // 同上。
        }
    }
}
