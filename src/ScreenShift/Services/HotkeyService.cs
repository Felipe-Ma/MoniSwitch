using System.Runtime.Versioning;
using ScreenShift.Models;
using ScreenShift.Native;

namespace ScreenShift.Services;

/// <summary>
/// Registers and dispatches global hotkeys through RegisterHotKey — the sanctioned mechanism,
/// per the spec, rather than a keyboard hook. One registration per profile with a gesture.
/// </summary>
/// <remarks>
/// WM_HOTKEY arrives on the window whose handle registered the key, so the owning window forwards
/// its messages here via <see cref="HandleMessage"/>. Registration failures (some other program
/// owns the combination) are reported, not thrown: a taken hotkey should cost that one gesture,
/// never the app.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class HotkeyService : IDisposable
{
    /// <summary>Arbitrary but stable base so our ids are recognisable in tooling.</summary>
    private const int FirstHotkeyId = 0xB0;

    private readonly IAppLogger _logger;
    private readonly Dictionary<int, Guid> _registrations = [];

    private IntPtr _hwnd;
    private int _nextId = FirstHotkeyId;

    public HotkeyService(IAppLogger logger)
    {
        _logger = logger;
    }

    /// <summary>Raised on the UI thread when a registered gesture is pressed.</summary>
    public event Action<Guid>? ProfileHotkeyPressed;

    public bool IsInitialized => _hwnd != IntPtr.Zero;

    public void Initialize(IntPtr windowHandle)
    {
        _hwnd = windowHandle;
    }

    /// <summary>
    /// Replaces all registrations with the given set. Called whenever profiles change; wholesale
    /// replacement keeps this idempotent and immune to drift.
    /// </summary>
    /// <returns>Human-readable descriptions of the gestures that could not be registered.</returns>
    public IReadOnlyList<string> Sync(IEnumerable<(Guid Id, string Name, string? Hotkey)> profiles)
    {
        var failures = new List<string>();

        if (!IsInitialized)
        {
            return failures;
        }

        UnregisterAll();

        foreach (var (id, name, hotkeyText) in profiles)
        {
            if (string.IsNullOrWhiteSpace(hotkeyText))
            {
                continue;
            }

            if (!HotkeyGesture.TryParse(hotkeyText, out var gesture))
            {
                failures.Add($"\"{name}\": '{hotkeyText}' is not a usable hotkey.");
                continue;
            }

            var hotkeyId = _nextId++;

            if (ShellInterop.RegisterHotKey(_hwnd, hotkeyId, gesture.Win32Modifiers | ShellInterop.MOD_NOREPEAT, gesture.VirtualKey))
            {
                _registrations[hotkeyId] = id;
                _logger.Debug($"Registered {gesture} -> \"{name}\".");
            }
            else
            {
                failures.Add($"{gesture} is already taken by another program; \"{name}\" has no hotkey.");
                _logger.Warn($"RegisterHotKey failed for {gesture} (\"{name}\") — most likely owned by another application.");
            }
        }

        _logger.Info($"Hotkeys synced: {_registrations.Count} registered, {failures.Count} failed.");
        return failures;
    }

    /// <summary>Routes WM_HOTKEY. Returns true when the message was one of ours.</summary>
    public bool HandleMessage(int msg, IntPtr wParam)
    {
        if (msg != ShellInterop.WM_HOTKEY || !_registrations.TryGetValue((int)wParam, out var profileId))
        {
            return false;
        }

        ProfileHotkeyPressed?.Invoke(profileId);
        return true;
    }

    public void Dispose() => UnregisterAll();

    private void UnregisterAll()
    {
        foreach (var id in _registrations.Keys)
        {
            ShellInterop.UnregisterHotKey(_hwnd, id);
        }

        _registrations.Clear();
    }
}
