using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WindhawkShare;

/// <summary>Documented exit codes of windhawk-cli.</summary>
public static class CliExitCode
{
    public const int Success = 0;
    public const int GenericError = 1;
    public const int UsageError = 2;
    public const int NetworkError = 3;
    public const int ModNotInstalled = 4;
    public const int ModNotFoundInRepository = 5;
    public const int CompilationFailed = 6;
    public const int AlreadyInstalled = 7;
}

public sealed record CliResult(int ExitCode, string Stdout, string Stderr);

public sealed record InstalledMod(string Id, string Version, string Name, bool Enabled);

public sealed record RepoMod(string Id, string Version, string Name);

public class WindhawkCliException(string message) : Exception(message);

/// <summary>
/// Talks to Windhawk ONLY through the official CLI (windhawk-cli.exe, Windhawk 2.0+).
/// No direct access to the registry or to Windhawk's internal files.
/// </summary>
public sealed class WindhawkCli
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public string ExePath { get; }

    private WindhawkCli(string exePath) => ExePath = exePath;

    /// <summary>
    /// Finds windhawk-cli.exe: explicit path, WINDHAWK_CLI variable, PATH, install folder.
    /// </summary>
    public static WindhawkCli Locate(string? explicitPath = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(explicitPath);

        var fromEnv = Environment.GetEnvironmentVariable("WINDHAWK_CLI");
        if (!string.IsNullOrWhiteSpace(fromEnv)) candidates.Add(fromEnv);

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim(), "windhawk-cli.exe"));

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
            candidates.Add(Path.Combine(programFiles, "Windhawk", "windhawk-cli.exe"));

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate)) return new WindhawkCli(Path.GetFullPath(candidate));
            }
            catch (Exception)
            {
                // Invalid entry in PATH: ignore it.
            }
        }

        throw new WindhawkCliException(
            "windhawk-cli.exe not found. Windhawk 2.0 or later is required. " +
            "Specify its location with --cli <path> or the WINDHAWK_CLI environment variable.");
    }

    /// <summary>
    /// Runs the CLI. Arguments go through ArgumentList, with no string concatenation,
    /// so values containing spaces or quotes cannot inject extra arguments.
    /// </summary>
    public async Task<CliResult> RunAsync(IEnumerable<string> args, bool json = true)
    {
        var psi = new ProcessStartInfo(ExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // --json is a global option placed before the command (verified).
        if (json) psi.ArgumentList.Add("--json");
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new WindhawkCliException($"Could not start {ExePath}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw new WindhawkCliException("windhawk-cli did not respond in time.");
        }

        return new CliResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    public async Task<string?> GetVersionAsync()
    {
        try
        {
            var r = await RunAsync(["--version"], json: false);
            if (r.ExitCode != CliExitCode.Success) return null;
            // Typically "windhawk-cli 2.0.0-alpha.5": keep the last word.
            var text = r.Stdout.Trim();
            var lastSpace = text.LastIndexOf(' ');
            return lastSpace >= 0 ? text[(lastSpace + 1)..] : (text.Length > 0 ? text : null);
        }
        catch (WindhawkCliException)
        {
            return null;
        }
    }

    public async Task<List<InstalledMod>> ListInstalledModsAsync()
    {
        var r = await RunAsync(["mod", "list"]);
        EnsureSuccess(r, "mod list");

        using var doc = ParseJson(r.Stdout, "mod list");
        var entries = FindModEntries(Unwrap(doc.RootElement))
            ?? throw new WindhawkCliException(
                "Unexpected output format from 'mod list'. Start of the response:\n" + Snippet(r.Stdout));

        var result = new List<InstalledMod>();
        foreach (var (keyId, item) in entries)
        {
            var id = FindString(item, "id") ?? keyId;
            if (string.IsNullOrEmpty(id)) continue;
            result.Add(new InstalledMod(
                id,
                FindString(item, "version") ?? "",
                FindString(item, "name") ?? id,
                IsEnabled(item)));
        }
        return result;
    }

    /// <summary>
    /// Finds the mod list in several possible shapes:
    /// an array, an object containing an array, or an object with mods keyed by ID.
    /// </summary>
    private static List<(string? KeyId, JsonElement Item)>? FindModEntries(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().Select(e => ((string?)null, e)).ToList();

        if (root.ValueKind != JsonValueKind.Object) return null;

        // Object containing an array of mods (e.g. "mods", "items", "installedMods"...).
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array &&
                prop.Value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Object))
                return prop.Value.EnumerateArray().Select(e => ((string?)null, e)).ToList();
        }

        // Object with a nested object holding the mods (e.g. { "mods": { "id": {...} } }).
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                var nested = FindModEntries(prop.Value);
                if (nested is not null && nested.Count > 0 && LooksLikeMods(nested)) return nested;
            }
        }

        // Object keyed by ID: { "explorer-style": { ... }, ... }
        var byKey = root.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.Object)
            .Select(p => ((string?)p.Name, p.Value))
            .ToList();
        return byKey.Count > 0 && LooksLikeMods(byKey) ? byKey : null;
    }

    private static bool LooksLikeMods(List<(string? KeyId, JsonElement Item)> entries) =>
        entries.All(e => FindString(e.Item, "version") is not null || FindString(e.Item, "id") is not null);

    /// <summary>Enabled state: "enabled", or "disabled" (also inside "config").</summary>
    private static bool IsEnabled(JsonElement item)
    {
        foreach (var obj in Candidates(item))
        {
            if (obj.TryGetProperty("enabled", out var en) && en.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return en.ValueKind == JsonValueKind.True;
            if (obj.TryGetProperty("disabled", out var dis))
            {
                if (dis.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return dis.ValueKind == JsonValueKind.False;
                if (dis.ValueKind == JsonValueKind.Number && dis.TryGetInt32(out var n))
                    return n == 0;
            }
        }
        return true;
    }

    /// <summary>The object itself plus the sub-objects where mod data usually lives.</summary>
    private static IEnumerable<JsonElement> Candidates(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) yield break;
        yield return item;
        foreach (var name in new[] { "metadata", "config", "mod" })
            if (item.TryGetProperty(name, out var sub) && sub.ValueKind == JsonValueKind.Object)
                yield return sub;
    }

    private static string? FindString(JsonElement item, string name)
    {
        foreach (var obj in Candidates(item))
        {
            var v = GetString(obj, name);
            if (v is not null) return v;
        }
        return null;
    }

    /// <summary>
    /// The CLI wraps every response in an envelope { "schemaVersion", "success", "data" }:
    /// the actual payload is in "data".
    /// </summary>
    private static JsonElement Unwrap(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out var data) &&
            (root.TryGetProperty("success", out _) || root.TryGetProperty("schemaVersion", out _)))
        {
            if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
                throw new WindhawkCliException("windhawk-cli reported an error:\n" + Snippet(root.GetRawText()));
            return data;
        }
        return root;
    }

    private static string Snippet(string text)
    {
        var t = text.Trim();
        return t.Length <= 400 ? t : t[..400] + " ...";
    }

    /// <summary>Current settings of a mod, always returned in flat form.</summary>
    public async Task<Dictionary<string, JsonElement>> GetSettingsAsync(string modId)
    {
        var r = await RunAsync(["mod", "settings", "get", modId]);
        if (r.ExitCode == CliExitCode.ModNotInstalled)
            throw new WindhawkCliException($"The mod '{modId}' is not installed.");
        EnsureSuccess(r, "mod settings get");

        using var doc = ParseJson(r.Stdout, "mod settings get");
        var root = Unwrap(doc.RootElement);
        var settings = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("settings", out var s)
            ? s
            : root;

        var flat = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (settings.ValueKind == JsonValueKind.Object)
            SettingsTools.Flatten(settings, "", flat);
        return flat;
    }

    /// <summary>
    /// Asks the CLI whether the mod exists in the official repository.
    /// Returns null if it doesn't. Throws if the network is down:
    /// without verification, nothing gets exported or installed.
    /// </summary>
    public async Task<RepoMod?> RepoShowAsync(string modId)
    {
        var r = await RunAsync(["repo", "show", modId]);
        if (r.ExitCode == CliExitCode.ModNotFoundInRepository) return null;
        if (r.ExitCode == CliExitCode.NetworkError)
            throw new WindhawkCliException(
                "Could not reach the official Windhawk repository. Check your internet connection.");
        EnsureSuccess(r, "repo show");

        using var doc = ParseJson(r.Stdout, "repo show");
        var root = Unwrap(doc.RootElement);
        return new RepoMod(
            FindString(root, "id") ?? modId,
            FindString(root, "version") ?? "",
            FindString(root, "name") ?? modId);
    }

    /// <summary>
    /// Installs a mod FROM THE OFFICIAL REPOSITORY, by ID only. The --file option is never used:
    /// this is what guarantees that installed code always comes from the official repository.
    /// </summary>
    public async Task<CliResult> InstallFromRepoAsync(string modId, string? version, bool disabled)
    {
        if (!OfficialCheck.IsValidModId(modId))
            throw new WindhawkCliException($"Invalid mod ID: {modId}");

        var args = new List<string> { "mod", "install", modId };
        if (version is not null)
        {
            if (!ImportValidation.IsValidVersion(version))
                throw new WindhawkCliException($"Invalid version: {version}");
            args.Add("--version");
            args.Add(version);
        }
        if (disabled) args.Add("--disabled");
        return await RunAsync(args);
    }

    /// <summary>Sets several values at once. Windhawk validates them against the mod's settings schema.</summary>
    public async Task<CliResult> SetSettingsAsync(string modId, IEnumerable<KeyValuePair<string, string>> values)
    {
        var args = new List<string> { "mod", "settings", "set", modId };
        args.AddRange(values.Select(kv => $"{kv.Key}={kv.Value}"));
        return await RunAsync(args);
    }

    public static string ErrorText(CliResult r)
    {
        var detail = string.IsNullOrWhiteSpace(r.Stderr) ? r.Stdout : r.Stderr;
        return $"code {r.ExitCode}: {Snippet(detail)}";
    }

    // ---------- helpers ----------

    private static void EnsureSuccess(CliResult r, string command)
    {
        if (r.ExitCode == CliExitCode.Success) return;
        var detail = string.IsNullOrWhiteSpace(r.Stderr) ? r.Stdout : r.Stderr;
        throw new WindhawkCliException(
            $"windhawk-cli {command} exited with code {r.ExitCode}: {detail.Trim()}");
    }

    private static JsonDocument ParseJson(string text, string command)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException e)
        {
            throw new WindhawkCliException($"Invalid JSON output from '{command}': {e.Message}");
        }
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
