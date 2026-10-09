using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using PrismShot.Core.Interop;

namespace PrismShot.Core.Platform;

[Flags]
internal enum HotkeyModifiers
{
    None = 0,
    Win = 1,
    Control = 2,
    Alt = 4,
    Shift = 8,
}

/// <summary>
/// 一条快捷键：主按键 + 修饰键组合，外加它的可读文本形式。
/// </summary>
/// <remarks>
/// 解析放在这里而不是各处随手 <c>Split('+')</c>：名称表和大小写规则只应该有一份。
/// 用户手改配置文件时写 <c>win+shift+s</c> 还是 <c>Win+Shift+S</c> 都要认。
/// </remarks>
internal sealed class HotkeyBinding
{
    private static readonly Dictionary<string, uint> VirtualKeyNames = BuildVirtualKeyNames();

    private HotkeyBinding(HotkeyModifiers modifiers, uint virtualKey, string text)
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
        Text = text;
    }

    public HotkeyModifiers Modifiers { get; }

    public uint VirtualKey { get; }

    public string Text { get; }

    public bool IsEmpty => VirtualKey == 0;

    public static HotkeyBinding Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new HotkeyBinding(HotkeyModifiers.None, 0, string.Empty);
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        uint virtualKey = 0;
        List<string> parts = new();

        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            switch (token.ToLowerInvariant())
            {
                case "win":
                case "windows":
                case "meta":
                case "super":
                    modifiers |= HotkeyModifiers.Win;
                    parts.Add("Win");
                    continue;
                case "ctrl":
                case "control":
                    modifiers |= HotkeyModifiers.Control;
                    parts.Add("Ctrl");
                    continue;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    parts.Add("Alt");
                    continue;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    parts.Add("Shift");
                    continue;
            }

            if (TryResolveKey(token, out uint key))
            {
                // 后写的覆盖先写的：用户写「A+B」这种畸形组合时，最后一个是真正的触发键。
                virtualKey = key;
                parts.Add(NormalizeKeyName(token));
            }
        }

        if (virtualKey == 0)
        {
            return new HotkeyBinding(HotkeyModifiers.None, 0, string.Empty);
        }

        return new HotkeyBinding(modifiers, virtualKey, string.Join("+", parts));
    }

    /// <summary>
    /// 判断一次按键事件是否命中本绑定。<paramref name="current"/> 是事发那一刻实际按住的修饰键。
    /// </summary>
    /// <remarks>
    /// 修饰键要求「完全一致」而不是「至少包含」：否则 Win+Shift+S 会在用户按
    /// Win+Shift+Ctrl+S 时也炸一次，那种误触发很难解释。
    /// </remarks>
    public bool Matches(uint virtualKey, HotkeyModifiers current) =>
        !IsEmpty && VirtualKey == virtualKey && Modifiers == current;

    private static bool TryResolveKey(string token, out uint virtualKey)
    {
        virtualKey = 0;
        if (VirtualKeyNames.TryGetValue(token, out uint mapped))
        {
            virtualKey = mapped;
            return true;
        }

        if (token.Length == 1)
        {
            char character = char.ToUpperInvariant(token[0]);
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = character;
                return true;
            }
        }

        return false;
    }

    private static string NormalizeKeyName(string token)
    {
        if (token.Length == 1)
        {
            return token.ToUpperInvariant();
        }

        if (token.StartsWith('F') && int.TryParse(token[1..], out int functionKey))
        {
            return $"F{functionKey}";
        }

        return char.ToUpperInvariant(token[0]) + token[1..].ToLowerInvariant();
    }

    private static Dictionary<string, uint> BuildVirtualKeyNames()
    {
        Dictionary<string, uint> map = new(StringComparer.OrdinalIgnoreCase)
        {
            ["printscreen"] = 0x2C,
            ["prtsc"] = 0x2C,
            ["snapshot"] = 0x2C,
            ["esc"] = 0x1B,
            ["escape"] = 0x1B,
            ["space"] = 0x20,
            ["tab"] = 0x09,
            ["enter"] = 0x0D,
            ["backspace"] = 0x08,
            ["insert"] = 0x2D,
            ["delete"] = 0x2E,
            ["del"] = 0x2E,
            ["home"] = 0x24,
            ["end"] = 0x23,
            ["pageup"] = 0x21,
            ["pagedown"] = 0x22,
            ["left"] = 0x25,
            ["up"] = 0x26,
            ["right"] = 0x27,
            ["down"] = 0x28,
            ["pause"] = 0x13,
            ["break"] = 0x13,
            ["capslock"] = 0x14,
            ["scrolllock"] = 0x91,
            ["numlock"] = 0x90,
            ["grave"] = 0xC0,
            ["minus"] = 0xBD,
            ["equal"] = 0xBB,
            ["comma"] = 0xBC,
            ["period"] = 0xBE,
            ["slash"] = 0xBF,
            ["semicolon"] = 0xBA,
            ["quote"] = 0xDE,
            ["bracketleft"] = 0xDB,
            ["bracketright"] = 0xDD,
            ["backslash"] = 0xDC,
        };

        for (int index = 1; index <= 24; index++)
        {
            map[$"f{index}"] = (uint)(0x6F + index);
        }

        for (int index = 0; index <= 9; index++)
        {
            map[$"num{index}"] = (uint)(0x60 + index);
        }

        return map;
    }

    public override string ToString() => string.IsNullOrEmpty(Text) ? "(未绑定)" : Text;

    /// <summary>取当前按住的修饰键。用于低级钩子里判断组合。</summary>
    public static HotkeyModifiers ReadCurrentModifiers()
    {
        HotkeyModifiers modifiers = HotkeyModifiers.None;

        if (
            IsDown(Win32.VirtualKeyLeftWindows) || IsDown(Win32.VirtualKeyRightWindows)
        )
        {
            modifiers |= HotkeyModifiers.Win;
        }

        if (IsDown(Win32.VirtualKeyControl))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (IsDown(Win32.VirtualKeyMenu))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (IsDown(Win32.VirtualKeyShift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        return modifiers;
    }

    private static bool IsDown(uint virtualKey) =>
        (Win32.GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;
}
