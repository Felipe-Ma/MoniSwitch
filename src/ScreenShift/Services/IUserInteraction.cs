namespace ScreenShift.Services;

/// <summary>
/// Outcome of the hotkey prompt. Cancelled means leave everything as it was; otherwise
/// <see cref="Hotkey"/> is the new gesture text, with null meaning the hotkey was removed.
/// </summary>
public sealed record HotkeyPromptResult(bool Cancelled, string? Hotkey);

/// <summary>
/// The view models' one window onto the user: confirmations, prompts and errors. Behind an
/// interface so they can drive these flows without owning a window, and so the flows can be
/// exercised without a UI at all.
/// </summary>
public interface IUserInteraction
{
    /// <summary>
    /// Asks whether to keep a display change that has already been applied, with a deadline.
    /// </summary>
    /// <remarks>
    /// The deadline is the point. If the change left every monitor dark the user cannot click
    /// anything, so silence has to mean "put it back" rather than "keep it".
    /// </remarks>
    /// <returns>True to keep the new settings; false to revert, including on timeout.</returns>
    bool ConfirmDisplayChange(string summary, TimeSpan timeout);

    /// <summary>Plain yes/no question, used before destructive but non-display actions.</summary>
    bool ConfirmAction(string title, string message);

    /// <summary>Asks for a line of text. Null when cancelled or left empty.</summary>
    string? PromptForText(string title, string prompt, string initialValue = "");

    /// <summary>Captures a global hotkey for a profile by listening to real key presses.</summary>
    HotkeyPromptResult PromptForHotkey(string profileName, string? currentHotkey);

    /// <summary>Reports a failure the user should see.</summary>
    void ShowError(string title, string message);
}
