using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using PrismShot.Core.Interop;

namespace PrismShot.Core.Platform;

/// <summary>
/// 全局低级键盘钩子。整个软件常驻开销的主体就是它。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不用更省事的 <c>RegisterHotKey</c>：Win+Shift+S 已经被 Explorer 抢走了，
/// 注册会直接返回「热键已被占用」，而 PrintScreen 即使注册成功，系统自己那份
/// 「复制全屏到剪贴板」也照跑不误，结果是复制两份、互相打架。
/// 低级钩子的位置在系统分发之前，回调里返回 1 就能把这次按键从整条链上摘掉 ——
/// 这才是「真正取代」原生截图工具的唯一做法。
/// </para>
/// <para>
/// 代价是每次按键都要过一次我们的回调。所以回调里只做一件事：比对，命中就投递到别的线程，
/// 绝不在这里做任何可能阻塞的事（超过 LowLevelHooksTimeout 系统会直接摘掉钩子，
/// 那时候快捷键会静默失效，用户只会觉得「这软件坏了」）。
/// </para>
/// </remarks>
internal sealed class KeyboardHook : IDisposable
{
    private const uint InjectedFlag = 0x10;

    private readonly ManualResetEventSlim _ready = new(false);
    private readonly object _gate = new();
    private readonly List<HotkeyBinding> _bindings = new();

    // 委托必须被字段引用住：一旦被 GC 回收，系统的回调就是野指针。
    private readonly HookProc _callback;

    private Thread? _thread;
    private IntPtr _hook;
    private uint _threadId;
    private volatile bool _disposed;
    private volatile bool _suspended;
    private volatile bool _swallowWindowsKeyUp;

    public KeyboardHook()
    {
        _callback = OnHook;
    }

    /// <summary>
    /// 命中快捷键。回调在普通线程池线程上，不在钩子线程里，可以放心做重活。
    /// </summary>
    public event Action<HotkeyBinding>? Triggered;

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(HookThread)
        {
            IsBackground = true,
            Name = "PrismShot.KeyboardHook",
            // 钩子线程的优先级略高一点：按键回调迟到会让用户觉得「按了没反应」。
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public void UpdateBindings(IEnumerable<HotkeyBinding> bindings)
    {
        lock (_gate)
        {
            _bindings.Clear();
            foreach (HotkeyBinding binding in bindings)
            {
                if (!binding.IsEmpty)
                {
                    _bindings.Add(binding);
                }
            }
        }
    }

    /// <summary>
    /// 临时停用。覆盖层已经弹出来的时候再按一次不该再弹一层，
    /// 而且那时候我们自己的合成按键不能被自己的钩子吞掉。
    /// </summary>
    public void SetSuspended(bool suspended) => _suspended = suspended;

    private void HookThread()
    {
        try
        {
            _threadId = Kernel32.GetCurrentThreadId();
            IntPtr module = Win32.GetModuleHandle(null);
            _hook = Win32.SetWindowsHookEx(
                Win32.WindowsHookKeyboardLowLevel,
                _callback,
                module,
                0
            );

            if (_hook == IntPtr.Zero)
            {
                Log.Error($"安装键盘钩子失败，最后错误 {Marshal.GetLastWin32Error()}。");
                _ready.Set();
                return;
            }

            _ready.Set();
            Log.Info("键盘钩子已挂上。");

            // 低级钩子要有消息循环才会被调用。
            while (Win32.GetMessage(out MSG message, IntPtr.Zero, 0, 0) > 0)
            {
                Win32.TranslateMessage(ref message);
                Win32.DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            Log.Error("键盘钩子线程异常退出。", ex);
            _ready.Set();
        }
        finally
        {
            if (_hook != IntPtr.Zero)
            {
                Win32.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }

            if (_threadId != 0)
            {
                Kernel32.PostThreadMessage(_threadId, Kernel32.WmQuit, IntPtr.Zero, IntPtr.Zero);
            }
        }
    }

    private IntPtr OnHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || _disposed)
        {
            return Win32.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        int message = wParam.ToInt32();
        bool isKeyDown =
            message == Win32.WindowsMessageKeyDown || message == Win32.WindowsMessageSystemKeyDown;
        bool isKeyUp =
            message == Win32.WindowsMessageKeyUp || message == Win32.WindowsMessageSystemKeyUp;

        if (!isKeyDown && !isKeyUp)
        {
            return Win32.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        KeyboardHookStruct data = Marshal.PtrToStructure<KeyboardHookStruct>(lParam);

        // 我们自己合成的按键不能再走一遍匹配，否则录像宏一发就自己把自己再触发一次。
        if ((data.Flags & InjectedFlag) != 0)
        {
            return Win32.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        if (_suspended)
        {
            return Win32.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        if (isKeyUp)
        {
            // Win 键抬起要额外吞掉一次：我们把 Win+Shift+S 的 S 吞了，
            // 于是系统眼里就是「Win 按下又松开，中间什么都没发生」，会顺手弹出开始菜单。
            if (_swallowWindowsKeyUp && IsWindowsKey(data.VirtualKeyCode))
            {
                _swallowWindowsKeyUp = false;
                return 1;
            }

            return Win32.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        HotkeyBinding? hit = Match(data.VirtualKeyCode);
        if (hit is null)
        {
            return Win32.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        if ((hit.Modifiers & HotkeyModifiers.Win) != 0)
        {
            _swallowWindowsKeyUp = true;
        }

        HotkeyBinding captured = hit;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Triggered?.Invoke(captured);
            }
            catch (Exception ex)
            {
                Log.Error($"处理快捷键 {captured} 时异常。", ex);
            }
        });

        return 1;
    }

    private HotkeyBinding? Match(uint virtualKey)
    {
        HotkeyModifiers current = HotkeyBinding.ReadCurrentModifiers();
        lock (_gate)
        {
            foreach (HotkeyBinding binding in _bindings)
            {
                if (binding.Matches(virtualKey, current))
                {
                    return binding;
                }
            }
        }

        return null;
    }

    private static bool IsWindowsKey(uint virtualKey) =>
        virtualKey == Win32.VirtualKeyLeftWindows || virtualKey == Win32.VirtualKeyRightWindows;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_threadId != 0)
        {
            Kernel32.PostThreadMessage(_threadId, Kernel32.WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _ready.Dispose();
        Log.Info("键盘钩子已卸载。");
    }
}

internal static class Kernel32
{
    public const uint WmQuit = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetMessage(out MSG message, IntPtr window, uint min, uint max);

    [DllImport("kernel32.dll")]
    public static extern bool TranslateMessage(ref MSG message);

    [DllImport("kernel32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG message);

    [DllImport("kernel32.dll")]
    public static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
}
