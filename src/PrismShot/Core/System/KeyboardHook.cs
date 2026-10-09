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
    private volatile bool _overlayKeys;

    public KeyboardHook()
    {
        _callback = OnHook;
    }

    /// <summary>
    /// 命中快捷键。回调在普通线程池线程上，不在钩子线程里，可以放心做重活。
    /// </summary>
    public event Action<HotkeyBinding>? Triggered;

    /// <summary>
    /// 覆盖层打开期间观察到的物理按键。<paramref name="virtualKey"/>
    /// 配合 <see cref="HotkeyBinding.ReadCurrentModifiers"/> 之外的即时修饰键一起上报。
    /// </summary>
    /// <remarks>
    /// 覆盖层刻意不去抢键盘焦点 —— 一抢，用户原本正在输入的那句话就断在后面的窗口里了。
    /// 代价是 WPF 的 <c>KeyDown</c> 根本不会来，ESC 也就跟着失效。
    /// 这里补齐缺口：由钩子在最先处把按键拦下来并上报，覆盖层照样能取消，
    /// 而且不看哪块屏上有焦点，跨多显示器也一致。
    /// </remarks>
    public event Action<uint, HotkeyModifiers>? KeyObserved;

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

    /// <summary>
    /// 覆盖层独占键盘。处于这个状态时，所有物理按键都不再往下传，只上报到
    /// <see cref="KeyObserved"/>。录屏宏等自己合成的那些按键不会被吞（注入标记已先行跳过）。
    /// </summary>
    public void SetOverlayKeys(bool enabled) => _overlayKeys = enabled;

    private void HookThread()
    {
        try
        {
            _threadId = Win32.GetCurrentThreadId();
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
                Win32.PostThreadMessage(_threadId, Win32.WindowsMessageQuit, IntPtr.Zero, IntPtr.Zero);
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
        bool injected = (data.Flags & InjectedFlag) != 0;
        if (injected)
        {
            // 但覆盖层开着的时候还是要让它看见一次：合成的 Esc 也该能取消。
            // 关键区别是这里**不吞**（照常往下传）—— 录屏宏发的组合键必须能走到游戏里去，
            // 吞掉就等于「按了录制没反应」。
            if (_overlayKeys && isKeyDown)
            {
                ReportToOverlay(data.VirtualKeyCode);
            }

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

        if (_overlayKeys)
        {
            ReportToOverlay(data.VirtualKeyCode);
            return 1;
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
        Action<HotkeyBinding>? subscribers = Triggered;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                subscribers?.Invoke(captured);
            }
            catch (Exception ex)
            {
                Log.Error($"处理快捷键 {captured} 时异常。", ex);
            }
        });

        return 1;
    }

    /// <summary>把一次按下原样告诉覆盖层。投到线程池上，绝不在钩子回调里做事。</summary>
    private void ReportToOverlay(uint virtualKey)
    {
        // 先把委托抓成局部变量再投递。直接写 KeyObserved?.Invoke(...) 会踩到
        // 「Collection was modified」—— 覆盖层关闭时会 -= 订阅者，
        // 而这时可能已经有一次调用在别的线程上排队，两者撞上就抛。
        Action<uint, HotkeyModifiers>? observer = KeyObserved;
        if (observer is null)
        {
            return;
        }

        HotkeyModifiers modifiers = HotkeyBinding.ReadCurrentModifiers();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                observer(virtualKey, modifiers);
            }
            catch (Exception ex)
            {
                Log.Error("上报覆盖层按键失败。", ex);
            }
        });
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
            Win32.PostThreadMessage(_threadId, Win32.WindowsMessageQuit, IntPtr.Zero, IntPtr.Zero);
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _ready.Dispose();
        Log.Info("键盘钩子已卸载。");
    }
}
