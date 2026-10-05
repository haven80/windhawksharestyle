using System.Text.Json;

namespace WindhawkShare;

/// <summary>
/// Checks on an incoming package. Files come from strangers: anything that doesn't fit is discarded.
/// </summary>
public static class ImportValidation
{
    public const long MaxFileBytes = 5 * 1024 * 1024;
    public const int MaxMods = 200;
    public const int MaxSettingsPerMod = 5000;
    public const int MaxKeyLength = 256;
    public const int MaxStringValueLength = 64 * 1024;

    public static SetupPackage Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new WindhawkCliException($"File not found: {path}");
        if (info.Length > MaxFileBytes) throw new WindhawkCliException("The package is too large.");

        SetupPackage? package;
        try
        {
            package = JsonSerializer.Deserialize(File.ReadAllText(path), PackageJsonContext.Default.SetupPackage);
        }
        catch (JsonException e)
        {
            throw new WindhawkCliException($"The file is not a valid package: {e.Message}");
        }

        if (package is null) throw new WindhawkCliException("The file is empty.");
        if (package.FormatVersion != SetupPackage.CurrentFormatVersion)
            throw new WindhawkCliException(
                $"Unsupported package format version ({package.FormatVersion}). Please update Windhawk Share.");
        if (package.Mods.Count > MaxMods) throw new WindhawkCliException("The package contains too many mods.");
        return package;
    }

    /// <summary>Versions like "1.2", "1.2.0", "2.0.0-beta": nothing that could look like a CLI option.</summary>
    public static bool IsValidVersion(string v) =>
        v.Length is > 0 and <= 32 &&
        char.IsAsciiLetterOrDigit(v[0]) &&
        v.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+');

    /// <summary>
    /// Keys like "Name", "Group.Name", "list[0].name". No "=" (it would break key=value),
    /// no spaces or control characters, and no leading "-".
    /// </summary>
    public static bool IsValidSettingKey(string key) =>
        key.Length is > 0 and <= MaxKeyLength &&
        (char.IsAsciiLetterOrDigit(key[0]) || key[0] == '_') &&
        key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '[' or ']' or '-' or '$');

    /// <summary>Values to show the user before applying them: paths, commands, web addresses.</summary>
    public static bool LooksLikePathOrCommand(string value) =>
        value.Contains(":\\") || value.Contains("\\\\") || value.Contains('%') ||
        value.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
        value.Contains(".exe", StringComparison.OrdinalIgnoreCase) ||
        value.Contains(".ps1", StringComparison.OrdinalIgnoreCase) ||
        value.Contains(".bat", StringComparison.OrdinalIgnoreCase) ||
        value.Contains(".cmd", StringComparison.OrdinalIgnoreCase);

    /// <summary>Converts a value to the text expected by "mod settings set". Null if not acceptable.</summary>
    public static string? ToCliValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String when value.GetString()!.Length <= MaxStringValueLength
            && !value.GetString()!.Any(c => char.IsControl(c) && c is not '\t' and not '\n' and not '\r')
            => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };
}

public sealed class AdminRequiredException() : WindhawkCliException(
    "Installing mods or changing settings requires administrator rights, " +
    "because Windhawk stores them in the system registry. " +
    "Run Windhawk Share as administrator (right-click → Run as administrator).");

public enum ModAction { Install, UpdateSettingsOnly, Skip }

public sealed class PlannedMod
{
    public required PackageMod Source { get; init; }
    public ModAction Action { get; set; }
    public string? InstallVersion { get; set; }
    public string? InstalledVersion { get; set; }
    public List<KeyValuePair<string, string>> Settings { get; } = new();
    public List<string> Notes { get; } = new();
}

public sealed class Importer(WindhawkCli cli)
{
    /// <summary>
    /// Builds the plan without changing anything. exactVersion: install the package's version
    /// instead of the latest one.
    /// </summary>
    public async Task<List<PlannedMod>> PlanAsync(
        SetupPackage package, IReadOnlySet<string>? onlyMods, bool exactVersion)
    {
        var installed = (await cli.ListInstalledModsAsync()).ToDictionary(m => m.Id, StringComparer.Ordinal);
        var plan = new List<PlannedMod>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mod in package.Mods)
        {
            var p = new PlannedMod { Source = mod, Action = ModAction.Skip };
            plan.Add(p);

            if (!OfficialCheck.IsValidModId(mod.Id))
            {
                p.Notes.Add("invalid ID or local mod: skipped");
                continue;
            }
            if (!seen.Add(mod.Id))
            {
                p.Notes.Add("listed twice in the package: duplicate skipped");
                continue;
            }
            if (onlyMods is not null && !onlyMods.Contains(mod.Id))
            {
                p.Notes.Add("not selected");
                continue;
            }
            if (mod.Settings.Count > ImportValidation.MaxSettingsPerMod)
            {
                p.Notes.Add("too many settings: skipped");
                continue;
            }

            // Core rule: the mod must exist in the official repository.
            var repo = await cli.RepoShowAsync(mod.Id);
            if (repo is null)
            {
                p.Notes.Add("not in the official repository: skipped");
                continue;
            }

            foreach (var (key, value) in mod.Settings)
            {
                var text = ImportValidation.IsValidSettingKey(key) ? ImportValidation.ToCliValue(value) : null;
                if (text is null) p.Notes.Add($"invalid setting '{Truncate(key)}': skipped");
                else p.Settings.Add(new(key, text));
            }

            if (installed.TryGetValue(mod.Id, out var current))
            {
                p.Action = ModAction.UpdateSettingsOnly;
                p.InstalledVersion = current.Version;
                await NoteLeftoverListItems(p);
                if (current.Version != mod.Version)
                    p.Notes.Add($"you have version {current.Version}, the package was created with {mod.Version}");
            }
            else
            {
                p.Action = ModAction.Install;
                if (exactVersion && ImportValidation.IsValidVersion(mod.Version))
                    p.InstallVersion = mod.Version;
                else if (repo.Version != mod.Version)
                    p.Notes.Add($"the latest version ({repo.Version}) will be installed, the package uses {mod.Version}");
            }
        }
        return plan;
    }

    /// <summary>
    /// Lists are overwritten item by item: if your list is longer than the package's,
    /// the extra items stay. This is reported in the plan.
    /// </summary>
    private async Task NoteLeftoverListItems(PlannedMod p)
    {
        var incomingKeys = p.Settings.Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var listRoots = p.Settings
            .Select(kv => kv.Key)
            .Where(k => k.Length > SettingsTools.RootOf(k).Length && k[SettingsTools.RootOf(k).Length] == '[')
            .Select(SettingsTools.RootOf)
            .ToHashSet(StringComparer.Ordinal);
        if (listRoots.Count == 0) return;

        var current = await cli.GetSettingsAsync(p.Source.Id);
        foreach (var root in listRoots)
        {
            var extra = current.Keys.Count(k => SettingsTools.RootOf(k) == root && !incomingKeys.Contains(k));
            if (extra > 0)
                p.Notes.Add($"your list '{root}' has {extra} extra values that will be left unchanged");
        }
    }

    /// <summary>Runs the plan. Returns the summary lines.</summary>
    public async Task<List<string>> ExecuteAsync(List<PlannedMod> plan, Action<string> progress)
    {
        var summary = new List<string>();

        foreach (var p in plan.Where(p => p.Action != ModAction.Skip))
        {
            var id = p.Source.Id;

            if (p.Action == ModAction.Install)
            {
                progress($"Installing {id}...");
                var r = await cli.InstallFromRepoAsync(id, p.InstallVersion, disabled: !p.Source.Enabled);
                if (IsAccessDenied(r)) throw new AdminRequiredException();
                if (r.ExitCode != CliExitCode.Success && r.ExitCode != CliExitCode.AlreadyInstalled)
                {
                    summary.Add($"ERROR    {id}: installation failed ({WindhawkCli.ErrorText(r)})");
                    continue;
                }
            }

            if (p.Settings.Count == 0)
            {
                summary.Add($"ok       {id}");
                continue;
            }

            progress($"Applying settings for {id}...");
            var (applied, rejected) = await ApplySettingsAsync(id, p.Settings);
            summary.Add(rejected.Count == 0
                ? $"ok       {id}: {applied} values applied"
                : $"partial  {id}: {applied} values applied, rejected by Windhawk: {string.Join(", ", rejected)}");
        }
        return summary;
    }

    /// <summary>
    /// Tries to apply everything at once. If Windhawk rejects it (e.g. a key that no longer exists in
    /// this version of the mod), it retries group by group, so one bad key doesn't block the rest.
    /// A group = one top-level setting, so a list always stays whole.
    /// </summary>
    private async Task<(int Applied, List<string> RejectedRoots)> ApplySettingsAsync(
        string modId, List<KeyValuePair<string, string>> settings)
    {
        var all = await cli.SetSettingsAsync(modId, settings);
        if (all.ExitCode == CliExitCode.Success) return (settings.Count, new());
        if (IsAccessDenied(all)) throw new AdminRequiredException();
        if (all.ExitCode != CliExitCode.UsageError)
            throw new WindhawkCliException($"Settings for {modId} were not applied ({WindhawkCli.ErrorText(all)})");

        var applied = 0;
        var rejected = new List<string>();
        foreach (var group in settings.GroupBy(kv => SettingsTools.RootOf(kv.Key)))
        {
            var r = await cli.SetSettingsAsync(modId, group);
            if (r.ExitCode == CliExitCode.Success) applied += group.Count();
            else rejected.Add(group.Key);
        }
        return (applied, rejected);
    }

    /// <summary>
    /// Windhawk writes to HKEY_LOCAL_MACHINE: without admin rights it fails with "access denied".
    /// The OS message is localized, so a few languages are checked besides the error code.
    /// </summary>
    private static bool IsAccessDenied(CliResult r)
    {
        var text = r.Stdout + r.Stderr;
        return text.Contains("\"osError\":5", StringComparison.Ordinal) ||
               text.Contains("ERROR_ACCESS_DENIED", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Accesso negato", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return true;
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..60] + "...";
}
