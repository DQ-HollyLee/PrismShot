using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PrismShot.Core.Platform;

/// <summary>
/// 合成按键。唯一用途是那条「一键启动显卡录像」的宏。
/// </summary>
/// <remarks>
/// 为什么这里做宏而不是去调显卡厂商的 API：NVIDIA 和 AMD 的录像能力都没有公开的
/// 稳定本机接口，ShadowPlay 的接口是私有的，AMD 那边同理。它们唯一一致、且厂商保证会一直有的
/// 入口就是「用户按同一个组合键」。所以这个宏按钮的正确定位是「替用户记住那台机器上的组合键」，
/// 而不是「调用录像功能」。
/// </remarks>
internal static class InputSimulator
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventExtendedKey = 0x0001;

    /// <summary>连发两次。部分录像软件注册的是 RegisterHotKey，对合成按键的处理比真实按键迟钝。</summary>
    public static bool SendChord(HotkeyBinding binding, int repeat = 1)
    {
        if (binding.IsEmpty)
        {
            return false;
        }

        for (int index = 0; index < Math.Max(1, repeat); index++)
        {
            if (!SendOnce(binding))
            {
                return false;
            }

            if (index < repeat - 1)
            {
                Thread.Sleep(80);
            }
        }

        return true;
    }

    private static bool SendOnce(HotkeyBinding binding)
    {
        List<Input> inputs = new();

        // 按下顺序固定：修饰键先按，主键后按，松开时反过来。
        // 反了的话某些游戏 / 录像软件会把它识别成「单独按了修饰键」，进而弹出别的面板。
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            inputs.Add(Key(0x5B, down: true));
        }

        if (binding.Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            inputs.Add(Key(0x11, down: true));
        }

        if (binding.Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            inputs.Add(Key(0x12, down: true));
        }

        if (binding.Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            inputs.Add(Key(0x10, down: true));
        }

        inputs.Add(Key(binding.VirtualKey, down: true));
        inputs.Add(Key(binding.VirtualKey, down: false));

        if (binding.Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            inputs.Add(Key(0x10, down: false));
        }

        if (binding.Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            inputs.Add(Key(0x12, down: false));
        }

        if (binding.Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            inputs.Add(Key(0x11, down: false));
        }

        if (binding.Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            inputs.Add(Key(0x5B, down: false));
        }

        Input[] array = inputs.ToArray();
        uint sent = Win32SendInput((uint)array.Length, array, Marshal.SizeOf<Input>());
        if (sent != array.Length)
        {
            Log.Warn($"合成按键只送出了 {sent}/{array.Length} 个事件。");
            return false;
        }

        return true;
    }

    private static Input Key(uint virtualKey, bool down) =>
        new()
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = (ushort)virtualKey,
                    ScanCode = 0,
                    Flags = down ? 0u : KeyEventKeyUp,
                    ExtraInfo = IntPtr.Zero,
                },
            },
        };

    [DllImport("user32.dll", EntryPoint = "SendInput")]
    private static extern uint Win32SendInput(uint count, Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    // 联合体在这里只是为了让 sizeof(INPUT) 对得上，所以显式排布。
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }
}

/// <summary>录像宏的预设。</summary>
internal static class RecorderPresets
{
    public const string XboxGameBar = "xbox";
    public const string Nvidia = "nvidia";
    public const string Amd = "amd";
    public const string Custom = "custom";

    public static readonly (string Key, string Display, string Keys)[] All =
    {
        (XboxGameBar, "Xbox Game Bar（Win+Alt+R，N 卡 A 卡通吃）", "Win+Alt+R"),
        (Nvidia, "NVIDIA ShadowPlay（Alt+F9）", "Alt+F9"),
        (Nvidia + "-replay", "NVIDIA 即时重放保存（Alt+F10）", "Alt+F10"),
        (Amd, "AMD Adrenalin（Ctrl+Shift+R）", "Ctrl+Shift+R"),
        (Custom, "自定义", "Win+Alt+R"),
    };

    /// <summary>把预设解成真正要发的组合键。</summary>
    public static HotkeyBinding Resolve(RecorderSettings settings)
    {
        if (string.Equals(settings.Preset, Custom, StringComparison.OrdinalIgnoreCase))
        {
            return HotkeyBinding.Parse(settings.Keys);
        }

        foreach ((string key, string _, string keys) in All)
        {
            if (string.Equals(key, settings.Preset, StringComparison.OrdinalIgnoreCase))
            {
                return HotkeyBinding.Parse(keys);
            }
        }

        return HotkeyBinding.Parse("Win+Alt+R");
    }
}
