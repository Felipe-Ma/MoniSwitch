using System.Windows;
using System.Windows.Input;
using ScreenShift.Models;
using ScreenShift.Services;

namespace ScreenShift.Views;

/// <summary>
/// Captures a hotkey combination by listening to real key presses, rather than making the user
/// type one out. Escape cancels, which is also why Escape can never be captured as the key.
/// </summary>
public partial class HotkeyPromptDialog : Window
{
    private HotkeyGesture? _captured;
    private bool _cleared;

    private HotkeyPromptDialog(string profileName, string? currentHotkey)
    {
        InitializeComponent();

        TitleText.Text = $"Hotkey for \"{profileName}\"";
        ClearButton.Visibility = string.IsNullOrEmpty(currentHotkey) ? Visibility.Collapsed : Visibility.Visible;

        if (HotkeyGesture.TryParse(currentHotkey, out var current))
        {
            GestureText.Text = current.ToString();
        }

        PreviewKeyDown += OnPreviewKeyDown;
    }

    public static HotkeyPromptResult ShowFor(Window? owner, string profileName, string? currentHotkey)
    {
        var dialog = new HotkeyPromptDialog(profileName, currentHotkey);

        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (dialog.ShowDialog() != true)
        {
            return new HotkeyPromptResult(Cancelled: true, Hotkey: null);
        }

        return dialog._cleared
            ? new HotkeyPromptResult(Cancelled: false, Hotkey: null)
            : new HotkeyPromptResult(Cancelled: false, Hotkey: dialog._captured?.ToString());
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Alt combinations arrive as Key.System with the real key in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Escape falls through so the Cancel button's IsCancel handling sees it.
        if (key == Key.Escape)
        {
            return;
        }

        e.Handled = true;

        // A modifier alone is the user mid-press; show progress but capture nothing.
        if (HotkeyGesture.IsModifierKey(key))
        {
            GestureText.Text = new HotkeyGesture(Keyboard.Modifiers, Key.None).ToString().TrimEnd('+') is { Length: > 0 } partial
                ? partial + "+…"
                : "…";
            return;
        }

        var candidate = new HotkeyGesture(Keyboard.Modifiers, key);

        if (!candidate.IsValid)
        {
            GestureText.Text = candidate.ToString();
            HintText.Text = "That combination has no Ctrl, Alt or Win — it would swallow ordinary typing.";
            SaveButton.IsEnabled = false;
            _captured = null;
            return;
        }

        _captured = candidate;
        GestureText.Text = candidate.ToString();
        HintText.Text = string.Empty;
        SaveButton.IsEnabled = true;
    }

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _cleared = true;
        DialogResult = true;
    }
}
