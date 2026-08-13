using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenShift.Models;

namespace ScreenShift.Services;

/// <summary>What applying a profile would do: the changes to make, and what could not be honoured.</summary>
public sealed class ProfilePlan
{
    public required string ProfileName { get; init; }

    public required IReadOnlyList<MonitorChangeRequest> Requests { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>
    /// False means the current state already matches the profile (or nothing matched at all —
    /// the warnings tell those cases apart).
    /// </summary>
    public bool HasWork => Requests.Count > 0;

    /// <summary>Multi-line description shown in the keep/revert prompt.</summary>
    public string Summary()
    {
        var lines = new List<string> { $"Profile \"{ProfileName}\":" };
        lines.AddRange(Requests.Select(r => "•  " + r));
        lines.AddRange(Warnings.Select(w => "!  " + w));
        return string.Join("\n", lines);
    }
}

/// <summary>
/// Owns the saved profiles: loading and saving profiles.json, the CRUD operations, and turning a
/// profile back into change requests for <see cref="IDisplayService.Apply"/>.
/// </summary>
public sealed class DisplayProfileService
{
    private const int CurrentVersion = 1;
    private const int MaxNameLength = 60;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IDisplayService _displays;
    private readonly IAppLogger _logger;
    private readonly List<DisplayProfile> _profiles = [];

    public DisplayProfileService(IDisplayService displays, IAppLogger logger)
    {
        _displays = displays;
        _logger = logger;
        Load();
    }

    public IReadOnlyList<DisplayProfile> Profiles => _profiles;

    // ------------------------------------------------------------------
    // CRUD
    // ------------------------------------------------------------------

    public DisplayProfile SaveCurrentAs(string name)
    {
        var now = DateTime.Now;
        var profile = new DisplayProfile
        {
            Name = UniqueName(name),
            CreatedAt = now,
            UpdatedAt = now,
            Monitors = CaptureCurrent(),
        };

        _profiles.Add(profile);
        Save();

        _logger.Info($"Saved profile \"{profile.Name}\" with {profile.Monitors.Count} display(s).");
        return profile;
    }

    /// <summary>Overwrites a profile's saved layout with whatever is on screen right now.</summary>
    public void UpdateFromCurrent(Guid id)
    {
        var profile = Find(id);
        profile.Monitors = CaptureCurrent();
        profile.UpdatedAt = DateTime.Now;
        Save();

        _logger.Info($"Updated profile \"{profile.Name}\" from the current configuration.");
    }

    public void Rename(Guid id, string newName)
    {
        var profile = Find(id);
        profile.Name = UniqueName(newName, excludeId: profile.Id);
        Save();
    }

    public DisplayProfile Duplicate(Guid id)
    {
        var source = Find(id);
        var now = DateTime.Now;

        var copy = new DisplayProfile
        {
            Name = UniqueName(source.Name),
            CreatedAt = now,
            UpdatedAt = now,
            Monitors = source.Monitors.Select(m => m.Clone()).ToList(),
        };

        _profiles.Insert(_profiles.IndexOf(source) + 1, copy);
        Save();
        return copy;
    }

    public void Delete(Guid id)
    {
        var removed = _profiles.RemoveAll(p => p.Id == id);
        if (removed > 0)
        {
            Save();
            _logger.Info("Deleted a profile.");
        }
    }

    // ------------------------------------------------------------------
    // Planning
    // ------------------------------------------------------------------

    /// <summary>
    /// Works out what has to change to reach <paramref name="profile"/> from the current state.
    /// Requests are diffs: a monitor already matching its saved state contributes nothing, so
    /// applying a profile twice is a no-op and an empty plan doubles as "this profile is active".
    /// </summary>
    public ProfilePlan BuildPlan(DisplayProfile profile)
    {
        var warnings = new List<string>();
        var monitors = _displays.GetMonitors();
        var pairs = MatchMonitors(profile.Monitors, monitors, warnings);

        if (pairs.Count == 0)
        {
            warnings.Add("None of the displays saved in this profile are connected right now.");
            return new ProfilePlan { ProfileName = profile.Name, Requests = [], Warnings = warnings };
        }

        if (profile.Monitors.Count(e => e is { Enabled: true, Primary: true }) > 1)
        {
            warnings.Add("The profile marks more than one display as primary; only the first is honoured.");
        }

        var requests = new List<MonitorChangeRequest>();
        var primaryAssigned = false;
        var profileNamesAPrimary = pairs.Any(p => p.Entry is { Enabled: true, Primary: true });

        foreach (var (entry, monitor) in pairs)
        {
            if (!entry.Enabled)
            {
                if (monitor.IsEnabled)
                {
                    if (monitor.IsPrimary && !profileNamesAPrimary)
                    {
                        warnings.Add("The profile switches the primary display off without naming a new one; Windows will choose.");
                    }

                    requests.Add(new MonitorChangeRequest { Monitor = monitor, Enabled = false });
                }

                continue;
            }

            var makePrimary = entry.Primary && !primaryAssigned;
            primaryAssigned |= makePrimary;

            if (!monitor.IsEnabled)
            {
                // Switching on: nothing to diff against, so everything the profile recorded is
                // requested outright. Apply() runs the topology stage first and then re-resolves,
                // which is what lets the mode settings land on the freshly enabled monitor.
                requests.Add(new MonitorChangeRequest
                {
                    Monitor = monitor,
                    Enabled = true,
                    Resolution = entry is { Width: { } w, Height: { } h } ? new DisplayResolution(w, h) : null,
                    RefreshHz = entry.RefreshHz,
                    Orientation = entry.Orientation,
                    Position = entry is { PosX: { } px, PosY: { } py } ? new DisplayPosition(px, py) : null,
                    MakePrimary = makePrimary,
                });
                continue;
            }

            // Both on: request only what differs. Refresh rate is compared against GDI's value
            // rather than the CCD one, because the profile stored GDI's number and the two round
            // fractional rates differently (59.94 Hz reads as 59 in GDI, rounds to 60 from CCD).
            var mode = _displays.GetCurrentMode(monitor);
            var currentResolution = mode?.Resolution ?? monitor.Resolution;

            DisplayResolution? resolution = null;
            if (entry is { Width: { } width, Height: { } height })
            {
                var desired = new DisplayResolution(width, height);
                if (desired != currentResolution)
                {
                    resolution = desired;
                }
            }

            uint? refresh = entry.RefreshHz is { } hz && hz != mode?.RefreshHz ? hz : null;

            DisplayOrientation? orientation = entry.Orientation != monitor.Orientation ? entry.Orientation : null;

            DisplayPosition? position = null;
            if (entry is { PosX: { } x, PosY: { } y })
            {
                var desiredPosition = new DisplayPosition(x, y);
                if (desiredPosition != monitor.Position)
                {
                    position = desiredPosition;
                }
            }

            var request = new MonitorChangeRequest
            {
                Monitor = monitor,
                Resolution = resolution,
                RefreshHz = refresh,
                Orientation = orientation,
                Position = position,
                MakePrimary = makePrimary && !monitor.IsPrimary,
            };

            if (request.ChangesAnything)
            {
                requests.Add(request);
            }
        }

        return new ProfilePlan { ProfileName = profile.Name, Requests = requests, Warnings = warnings };
    }

    /// <summary>
    /// Lines saved monitors up with connected hardware. Three passes, strictest first; a monitor
    /// claimed by one pass is out of the running for the next.
    /// </summary>
    private static List<(ProfileMonitorConfig Entry, MonitorInfo Monitor)> MatchMonitors(
        IReadOnlyList<ProfileMonitorConfig> entries,
        IReadOnlyList<MonitorInfo> monitors,
        List<string> warnings)
    {
        var pairs = new List<(ProfileMonitorConfig, MonitorInfo)>();
        var unmatched = entries.ToList();
        var free = monitors.ToList();

        // Pass 1: device path — monitor plus port. Survives reboots and driver updates, and keeps
        // two identical monitors apart because they can never share a port.
        for (var i = unmatched.Count - 1; i >= 0; i--)
        {
            var entry = unmatched[i];
            if (string.IsNullOrEmpty(entry.DevicePath))
            {
                continue;
            }

            var monitor = free.FirstOrDefault(m => string.Equals(m.DevicePath, entry.DevicePath, StringComparison.OrdinalIgnoreCase));
            if (monitor is null)
            {
                continue;
            }

            pairs.Add((entry, monitor));
            unmatched.RemoveAt(i);
            free.Remove(monitor);
        }

        // Pass 2: adapter path + connector id — covers a monitor whose device path changed shape
        // without the hardware moving.
        for (var i = unmatched.Count - 1; i >= 0; i--)
        {
            var entry = unmatched[i];
            if (string.IsNullOrEmpty(entry.AdapterPath))
            {
                continue;
            }

            var monitor = free.FirstOrDefault(m =>
                m.TargetId == entry.TargetId
                && string.Equals(m.AdapterDevicePath, entry.AdapterPath, StringComparison.OrdinalIgnoreCase));

            if (monitor is null)
            {
                continue;
            }

            pairs.Add((entry, monitor));
            unmatched.RemoveAt(i);
            free.Remove(monitor);
        }

        // Pass 3: EDID model — the monitor moved to a different port. Only taken when there is
        // exactly one candidate and exactly one saved entry wanting it: with two identical monitors
        // both moved, any pairing would be a guess, and guessing hands the portrait settings to the
        // landscape monitor.
        var ambiguityWarned = new HashSet<(string, ushort)>();

        for (var i = unmatched.Count - 1; i >= 0; i--)
        {
            var entry = unmatched[i];
            if (entry.EdidVendor is not { } vendor || entry.EdidProduct is not { } product)
            {
                continue;
            }

            var wanters = unmatched.Count(e => e.EdidVendor == vendor && e.EdidProduct == product);
            var candidates = free.Where(m => m.EdidVendorCode == vendor && m.EdidProductCode == product).ToList();

            if (candidates.Count == 1 && wanters == 1)
            {
                pairs.Add((entry, candidates[0]));
                unmatched.RemoveAt(i);
                free.Remove(candidates[0]);
            }
            else if (candidates.Count > 0 && ambiguityWarned.Add((vendor, product)))
            {
                warnings.Add($"More than one \"{entry.FriendlyName}\" could be the saved one; those displays were skipped rather than guessed at.");
            }
        }

        foreach (var entry in unmatched.Where(e => e.EdidVendor is null || !ambiguityWarned.Contains((e.EdidVendor, e.EdidProduct ?? 0))))
        {
            warnings.Add($"Saved display \"{entry.FriendlyName}\" is not connected; its settings were skipped.");
        }

        foreach (var monitor in free)
        {
            if (pairs.Count > 0)
            {
                warnings.Add($"{monitor.FriendlyName} is not part of this profile and was left unchanged.");
            }
        }

        return pairs;
    }

    // ------------------------------------------------------------------
    // Capture and persistence
    // ------------------------------------------------------------------

    private List<ProfileMonitorConfig> CaptureCurrent()
    {
        var monitors = _displays.GetMonitors();
        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("No displays detected; there is nothing to save.");
        }

        var result = new List<ProfileMonitorConfig>();

        foreach (var monitor in monitors)
        {
            var config = new ProfileMonitorConfig
            {
                DevicePath = monitor.DevicePath,
                AdapterPath = monitor.AdapterDevicePath,
                TargetId = monitor.TargetId,
                FriendlyName = monitor.FriendlyName,
                EdidVendor = monitor.EdidVendorCode,
                EdidProduct = monitor.EdidProductCode,
                Enabled = monitor.IsEnabled,
                Primary = monitor.IsPrimary,
                Orientation = monitor.Orientation,
            };

            if (monitor.IsEnabled)
            {
                // GDI is the source of truth for the mode: these are the exact values the apply
                // path will write back, so saving anything else would make a saved profile
                // immediately "different" from the state it was saved from.
                var mode = _displays.GetCurrentMode(monitor);

                config.Width = mode?.Width ?? monitor.Resolution?.Width;
                config.Height = mode?.Height ?? monitor.Resolution?.Height;
                config.RefreshHz = mode?.RefreshHz
                    ?? (monitor.RefreshRate.IsKnown ? (uint?)Math.Round(monitor.RefreshRate.Hz) : null);
                config.PosX = monitor.Position?.X;
                config.PosY = monitor.Position?.Y;
            }

            result.Add(config);
        }

        return result;
    }

    private DisplayProfile Find(Guid id) =>
        _profiles.FirstOrDefault(p => p.Id == id)
        ?? throw new InvalidOperationException("That profile no longer exists.");

    private string UniqueName(string requested, Guid? excludeId = null)
    {
        var baseName = string.IsNullOrWhiteSpace(requested) ? "Profile" : requested.Trim();
        if (baseName.Length > MaxNameLength)
        {
            baseName = baseName[..MaxNameLength].TrimEnd();
        }

        var name = baseName;
        var suffix = 2;

        while (_profiles.Any(p => p.Id != excludeId && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{baseName} ({suffix++})";
        }

        return name;
    }

    private void Load()
    {
        if (!File.Exists(AppPaths.ProfilesFile))
        {
            return;
        }

        try
        {
            var file = JsonSerializer.Deserialize<ProfileFile>(File.ReadAllText(AppPaths.ProfilesFile), JsonOptions);

            if (file?.Profiles is { } loaded)
            {
                _profiles.AddRange(loaded.Where(p => !string.IsNullOrWhiteSpace(p.Name) && p.Monitors.Count > 0));
            }

            _logger.Info($"Loaded {_profiles.Count} profile(s) from {AppPaths.ProfilesFile}.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt file must not stop the app from starting, but silently discarding the
            // user's profiles would be worse — so the evidence is kept under a new name.
            var backup = AppPaths.ProfilesFile + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";

            try
            {
                File.Move(AppPaths.ProfilesFile, backup);
            }
            catch (IOException)
            {
            }

            _logger.Error($"profiles.json could not be read and was moved to {backup}.", ex);
        }
    }

    private void Save()
    {
        AppPaths.EnsureCreated();

        var payload = JsonSerializer.Serialize(
            new ProfileFile { Version = CurrentVersion, Profiles = _profiles },
            JsonOptions);

        // Write-then-move so a crash mid-save can never leave a half-written profiles.json.
        var temp = AppPaths.ProfilesFile + ".tmp";
        File.WriteAllText(temp, payload);
        File.Move(temp, AppPaths.ProfilesFile, overwrite: true);
    }

    /// <summary>Top-level shape of profiles.json. The version field is there for future migrations.</summary>
    private sealed class ProfileFile
    {
        public int Version { get; set; } = CurrentVersion;

        public List<DisplayProfile> Profiles { get; set; } = [];
    }
}
