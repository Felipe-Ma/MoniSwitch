using System.Text;
using System.Windows.Input;

namespace ScreenShift.Models;

/// <summary>
/// A global hotkey combination, e.g. Ctrl+Alt+1. Stored in profiles.json in its
/// <see cref="ToString"/> form, so the JSON stays hand-editable.
/// </summary>
/// <remarks>
/// <see cref="ModifierKeys"/> is used directly because its numeric values coincide with the Win32
/// MOD_* flags, so the conversion for RegisterHotKey is a cast rather than a mapping table.
/// </remarks>
public readonly record struct HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    /// <summary>fsModifiers for RegisterHotKey (without MOD_NOREPEAT; the caller adds that).</summary>
    public uint Win32Modifiers => (uint)Modifiers;

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    /// <summary>
    /// A gesture must involve Ctrl, Alt or Win. Shift-only combinations are refused because they
    /// would swallow ordinary typing ("Shift+1" is how you type an exclamation mark).
    /// </summary>
    public bool IsValid =>
        Key != Key.None
        && !IsModifierKey(Key)
        && (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;

    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin
        or Key.System;

    public override string ToString()
    {
        var text = new StringBuilder();

        if (Modifiers.HasFlag(ModifierKeys.Control))
        {
            text.Append("Ctrl+");
        }

        if (Modifiers.HasFlag(ModifierKeys.Alt))
        {
            text.Append("Alt+");
        }

        if (Modifiers.HasFlag(ModifierKeys.Shift))
        {
            text.Append("Shift+");
        }

        if (Modifiers.HasFlag(ModifierKeys.Windows))
        {
            text.Append("Win+");
        }

        text.Append(KeyName(Key));
        return text.ToString();
    }

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = ModifierKeys.None;
        var key = Key.None;

        foreach (var raw in text.Split('+'))
        {
            var token = raw.Trim();

            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModifierKeys.Control;
                    continue;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    continue;
                case "shift":
                    modifiers |= ModifierKeys.Shift;
                    continue;
                case "win" or "windows":
                    modifiers |= ModifierKeys.Windows;
                    continue;
            }

            // "1" is stored for what the Key enum calls D1.
            if (token.Length == 1 && char.IsAsciiDigit(token[0]))
            {
                token = "D" + token;
            }

            if (!Enum.TryParse<Key>(token, ignoreCase: true, out key))
            {
                return false;
            }
        }

        gesture = new HotkeyGesture(modifiers, key);
        return gesture.IsValid;
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "NumPad" + (key - Key.NumPad0),
        _ => key.ToString(),
    };
}
