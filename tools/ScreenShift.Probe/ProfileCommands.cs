using ScreenShift.Services;

namespace ScreenShift.Probe;

/// <summary>
/// Profile CRUD and apply from the command line, against the same profiles.json the app uses.
/// </summary>
/// <remarks>
/// --profile-apply applies without the confirmation prompt and leaves the result in place, which
/// makes it the harness for proving a profile round-trips: save one, wreck the configuration,
/// apply the profile, and check that a freshly built plan comes back empty.
/// </remarks>
internal static class ProfileCommands
{
    public static int Save(string name)
    {
        var (_, profiles) = Create();
        var profile = profiles.SaveCurrentAs(name);

        Console.WriteLine($"Saved \"{profile.Name}\":");
        foreach (var monitor in profile.Monitors)
        {
            Console.WriteLine($"  {monitor}");
        }

        Console.WriteLine($"-> {AppPaths.ProfilesFile}");
        return 0;
    }

    public static int List()
    {
        var (_, profiles) = Create();

        if (profiles.Profiles.Count == 0)
        {
            Console.WriteLine("No profiles saved.");
            return 0;
        }

        foreach (var profile in profiles.Profiles)
        {
            Console.WriteLine($"{profile.Name}  (updated {profile.UpdatedAt:yyyy-MM-dd HH:mm})");
            foreach (var monitor in profile.Monitors)
            {
                Console.WriteLine($"  {monitor}");
            }
        }

        return 0;
    }

    public static int Delete(string name)
    {
        var (_, profiles) = Create();

        var found = profiles.Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (found is null)
        {
            Console.Error.WriteLine($"No profile named \"{name}\".");
            return 2;
        }

        profiles.Delete(found.Id);
        Console.WriteLine($"Deleted \"{found.Name}\".");
        return 0;
    }

    public static int Apply(string name, bool dryRun)
    {
        var (displays, profiles) = Create();

        var profile = profiles.Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            var available = profiles.Profiles.Count == 0
                ? "none saved"
                : string.Join(", ", profiles.Profiles.Select(p => $"\"{p.Name}\""));
            Console.Error.WriteLine($"No profile named \"{name}\". Available: {available}.");
            return 2;
        }

        var plan = profiles.BuildPlan(profile);

        foreach (var warning in plan.Warnings)
        {
            Console.WriteLine($"warning: {warning}");
        }

        if (!plan.HasWork)
        {
            Console.WriteLine("Nothing to do — the profile is already active.");
            return 0;
        }

        Console.WriteLine("Plan:");
        foreach (var request in plan.Requests)
        {
            Console.WriteLine($"  {request}");
        }

        if (dryRun)
        {
            Console.WriteLine("(dry run — nothing applied)");
            return 0;
        }

        var result = displays.Apply(plan.Requests);
        if (!result.Succeeded)
        {
            Console.Error.WriteLine($"Apply failed: {result.Message}");
            if (result.RolledBack)
            {
                Console.Error.WriteLine("The previous settings were restored.");
            }

            return 1;
        }

        Thread.Sleep(TimeSpan.FromSeconds(2));

        Console.WriteLine("--- now ---");
        foreach (var monitor in displays.GetMonitors())
        {
            Console.WriteLine(monitor.IsEnabled
                ? $"  {monitor.GdiDeviceName,-14} {monitor.FriendlyName,-14} {monitor.Resolution} @ {monitor.RefreshRate} at ({monitor.Position?.X},{monitor.Position?.Y}){(monitor.IsPrimary ? "  PRIMARY" : string.Empty)}"
                : $"  {"(off)",-14} {monitor.FriendlyName,-14} target {monitor.TargetId}");
        }

        // The acid test: planning the same profile against the new state should find nothing to do.
        var residual = profiles.BuildPlan(profile);
        if (!residual.HasWork)
        {
            Console.WriteLine("STATE MATCHES PROFILE.");
            return 0;
        }

        Console.Error.WriteLine("State does not fully match the profile; still pending:");
        foreach (var request in residual.Requests)
        {
            Console.Error.WriteLine($"  {request}");
        }

        return 1;
    }

    private static (DisplayService Displays, DisplayProfileService Profiles) Create()
    {
        var logger = new ConsoleLogger();
        var displays = new DisplayService(logger);
        return (displays, new DisplayProfileService(displays, logger));
    }
}
