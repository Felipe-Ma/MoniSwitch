namespace ScreenShift.Services;

/// <summary>
/// Asks the user to confirm a display change that has already been applied, with a deadline.
/// </summary>
/// <remarks>
/// The deadline is the whole point. If a change leaves a monitor dark the user cannot click
/// anything, so silence has to mean "put it back" rather than "keep it".
/// <para>
/// This lives behind an interface so the view models can drive the rollback flow without owning a
/// window, and so the flow can be exercised without a UI at all.
/// </para>
/// </remarks>
public interface IUserConfirmation
{
    /// <summary>
    /// Shows the prompt and blocks until the user answers or the timeout expires.
    /// </summary>
    /// <returns>True to keep the new settings; false to revert, including on timeout.</returns>
    bool ConfirmDisplayChange(string summary, TimeSpan timeout);

    /// <summary>Reports a failure that the user should see.</summary>
    void ShowError(string title, string message);
}
