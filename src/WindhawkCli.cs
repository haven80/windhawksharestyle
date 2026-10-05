using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WindhawkShare;

/// <summary>Codici di uscita documentati di windhawk-cli.</summary>
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

public sealed class WindhawkCliException(string message) : Exception(message);

/// <summary>
/// Parla con Windhawk SOLO attraverso la CLI ufficiale (windhawk-cli.exe, Windhawk 2.0+).
/// Nessun accesso diretto al registro o ai file interni di Windhawk.
/// </summary>
public sealed class WindhawkCli
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public string ExePath { get; }

    private WindhawkCli(string exePath) => ExePath = exePath;

    /// <summary>
    /// Cerca windhawk-cli.exe: percorso esplicito, variabile WINDHAWK_CLI, PATH, cartella di installazione.
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
                // Percorso non valido nel PATH: si ignora.
            }
        }

        throw new WindhawkCliException(
            "windhawk-cli.exe non trovato. Serve Windhawk 2.0 o successivo. " +
            "Indica il percorso con --cli <percorso> o con la variabile WINDHAWK_CLI.");
    }

    /// <summary>
    /// Esegue la CLI. Gli argomenti passano da ArgumentList: nessuna concatenazione di stringhe,
    /// quindi valori con spazi o virgolette non possono iniettare altri argomenti.
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

        // DA VERIFICARE: si assume che --json sia un'opzione globale accettata prima del comando.
        if (json) psi.ArgumentList.Add("--json");
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new WindhawkCliException($"Impossibile avviare {ExePath}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* già terminato */ }
            throw new WindhawkCliException("windhawk-cli non ha risposto entro il tempo limite.");
        }

        return new CliResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    public async Task<string?> GetVersionAsync()
    {
        try
        {
            var r = await RunAsync(["--version"], json: false);
            if (r.ExitCode != CliExitCode.Success) return null;
            // Tipicamente "windhawk-cli 2.0.0-alpha.5": si tiene l'ultima parola.
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
        var array = FindArray(doc.RootElement, "mods");

        var result = new List<InstalledMod>();
        foreach (var item in array.EnumerateArray())
        {
            var id = GetString(item, "id");
            if (string.IsNullOrEmpty(id)) continue;
            result.Add(new InstalledMod(
                id,
                GetString(item, "version") ?? "",
                GetString(item, "name") ?? id,
                item.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True));
        }
        return result;
    }

    /// <summary>Impostazioni correnti della mod, sempre restituite in forma piatta.</summary>
    public async Task<Dictionary<string, JsonElement>> GetSettingsAsync(string modId)
    {
        var r = await RunAsync(["mod", "settings", "get", modId]);
        if (r.ExitCode == CliExitCode.ModNotInstalled)
            throw new WindhawkCliException($"La mod '{modId}' non è installata.");
        EnsureSuccess(r, "mod settings get");

        using var doc = ParseJson(r.Stdout, "mod settings get");
        var root = doc.RootElement;
        var settings = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("settings", out var s)
            ? s
            : root;

        var flat = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (settings.ValueKind == JsonValueKind.Object)
            SettingsTools.Flatten(settings, "", flat);
        return flat;
    }

    /// <summary>
    /// Chiede alla CLI se la mod esiste nel repository ufficiale.
    /// Restituisce null se non esiste. Lancia eccezione se la rete non funziona:
    /// in quel caso non si può verificare, e senza verifica non si esporta.
    /// </summary>
    public async Task<RepoMod?> RepoShowAsync(string modId)
    {
        var r = await RunAsync(["repo", "show", modId]);
        if (r.ExitCode == CliExitCode.ModNotFoundInRepository) return null;
        if (r.ExitCode == CliExitCode.NetworkError)
            throw new WindhawkCliException(
                "Impossibile contattare il repository ufficiale di Windhawk. Controlla la connessione.");
        EnsureSuccess(r, "repo show");

        using var doc = ParseJson(r.Stdout, "repo show");
        var root = doc.RootElement;
        var meta = root.TryGetProperty("metadata", out var m) ? m : root;
        return new RepoMod(
            GetString(root, "id") ?? modId,
            GetString(meta, "version") ?? "",
            GetString(meta, "name") ?? modId);
    }

    // ---------- utilità ----------

    private static void EnsureSuccess(CliResult r, string command)
    {
        if (r.ExitCode == CliExitCode.Success) return;
        var detail = string.IsNullOrWhiteSpace(r.Stderr) ? r.Stdout : r.Stderr;
        throw new WindhawkCliException(
            $"windhawk-cli {command} è terminato con codice {r.ExitCode}: {detail.Trim()}");
    }

    private static JsonDocument ParseJson(string text, string command)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException e)
        {
            throw new WindhawkCliException($"Output JSON non valido da '{command}': {e.Message}");
        }
    }

    private static JsonElement FindArray(JsonElement root, string propertyName)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(propertyName, out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
            return arr;
        throw new WindhawkCliException($"Formato inatteso: manca l'elenco '{propertyName}'.");
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
