using System.Text.Json;

namespace WindhawkShare;

/// <summary>
/// Controlli sul pacchetto ricevuto. Il file arriva da sconosciuti: tutto ciò che non torna viene scartato.
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
        if (!info.Exists) throw new WindhawkCliException($"File non trovato: {path}");
        if (info.Length > MaxFileBytes) throw new WindhawkCliException("Il pacchetto è troppo grande.");

        SetupPackage? package;
        try
        {
            package = JsonSerializer.Deserialize(File.ReadAllText(path), PackageJsonContext.Default.SetupPackage);
        }
        catch (JsonException e)
        {
            throw new WindhawkCliException($"Il file non è un pacchetto valido: {e.Message}");
        }

        if (package is null) throw new WindhawkCliException("Il file è vuoto.");
        if (package.FormatVersion != SetupPackage.CurrentFormatVersion)
            throw new WindhawkCliException(
                $"Versione del formato non supportata ({package.FormatVersion}). Aggiorna windhawk-share.");
        if (package.Mods.Count > MaxMods) throw new WindhawkCliException("Il pacchetto contiene troppe mod.");
        return package;
    }

    /// <summary>Versioni tipo "1.2", "1.2.0", "2.0.0-beta": niente che possa sembrare un'opzione.</summary>
    public static bool IsValidVersion(string v) =>
        v.Length is > 0 and <= 32 &&
        char.IsAsciiLetterOrDigit(v[0]) &&
        v.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+');

    /// <summary>
    /// Chiavi tipo "Nome", "Gruppo.Nome", "lista[0].nome". Niente "=" (romperebbe chiave=valore),
    /// niente spazi o caratteri di controllo, e non può iniziare con "-".
    /// </summary>
    public static bool IsValidSettingKey(string key) =>
        key.Length is > 0 and <= MaxKeyLength &&
        (char.IsAsciiLetterOrDigit(key[0]) || key[0] == '_') &&
        key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '[' or ']' or '-' or '$');

    /// <summary>Converte un valore nel testo atteso da "mod settings set". Null se non accettabile.</summary>
    public static string? ToCliValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String when value.GetString()!.Length <= MaxStringValueLength
            && !value.GetString()!.Any(c => char.IsControl(c) && c is not '\t' and not '\n' and not '\r')
            => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        // DA VERIFICARE: si assume che la CLI accetti true/false per i booleani.
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };
}

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
    /// Prepara il piano senza toccare nulla. exactVersion: installa la versione del pacchetto
    /// invece dell'ultima disponibile.
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
                p.Notes.Add("ID non valido o mod locale: ignorata");
                continue;
            }
            if (!seen.Add(mod.Id))
            {
                p.Notes.Add("presente due volte nel pacchetto: ignorata la copia");
                continue;
            }
            if (onlyMods is not null && !onlyMods.Contains(mod.Id))
            {
                p.Notes.Add("non selezionata");
                continue;
            }
            if (mod.Settings.Count > ImportValidation.MaxSettingsPerMod)
            {
                p.Notes.Add("troppe impostazioni: ignorata");
                continue;
            }

            // Regola fondamentale: deve esistere nel repository ufficiale.
            var repo = await cli.RepoShowAsync(mod.Id);
            if (repo is null)
            {
                p.Notes.Add("non presente nel repository ufficiale: ignorata");
                continue;
            }

            foreach (var (key, value) in mod.Settings)
            {
                var text = ImportValidation.IsValidSettingKey(key) ? ImportValidation.ToCliValue(value) : null;
                if (text is null) p.Notes.Add($"impostazione '{Truncate(key)}' non valida: ignorata");
                else p.Settings.Add(new(key, text));
            }

            if (installed.TryGetValue(mod.Id, out var current))
            {
                p.Action = ModAction.UpdateSettingsOnly;
                p.InstalledVersion = current.Version;
                await NoteLeftoverListItems(p);
                if (current.Version != mod.Version)
                    p.Notes.Add($"hai la versione {current.Version}, il pacchetto è stato creato con la {mod.Version}");
            }
            else
            {
                p.Action = ModAction.Install;
                if (exactVersion && ImportValidation.IsValidVersion(mod.Version))
                    p.InstallVersion = mod.Version;
                else if (repo.Version != mod.Version)
                    p.Notes.Add($"verrà installata l'ultima versione ({repo.Version}), il pacchetto usa la {mod.Version}");
            }
        }
        return plan;
    }

    /// <summary>
    /// Le liste vengono sovrascritte elemento per elemento: se la tua lista è più lunga di quella del
    /// pacchetto, gli elementi in più restano. Lo si segnala nel piano.
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
                p.Notes.Add($"la tua lista '{root}' ha {extra} valori in più che resteranno invariati");
        }
    }

    /// <summary>Esegue il piano. Restituisce le righe di riepilogo.</summary>
    public async Task<List<string>> ExecuteAsync(List<PlannedMod> plan, Action<string> progress)
    {
        var summary = new List<string>();

        foreach (var p in plan.Where(p => p.Action != ModAction.Skip))
        {
            var id = p.Source.Id;

            if (p.Action == ModAction.Install)
            {
                progress($"Installazione di {id}...");
                var r = await cli.InstallFromRepoAsync(id, p.InstallVersion, disabled: !p.Source.Enabled);
                if (r.ExitCode != CliExitCode.Success && r.ExitCode != CliExitCode.AlreadyInstalled)
                {
                    summary.Add($"ERRORE  {id}: installazione fallita ({WindhawkCli.ErrorText(r)})");
                    continue;
                }
            }

            if (p.Settings.Count == 0)
            {
                summary.Add($"ok      {id}");
                continue;
            }

            progress($"Applicazione delle impostazioni di {id}...");
            var (applied, rejected) = await ApplySettingsAsync(id, p.Settings);
            summary.Add(rejected.Count == 0
                ? $"ok      {id}: {applied} valori applicati"
                : $"parz.   {id}: {applied} valori applicati, rifiutati da Windhawk: {string.Join(", ", rejected)}");
        }
        return summary;
    }

    /// <summary>
    /// Prova ad applicare tutto insieme. Se Windhawk rifiuta (es. una chiave che non esiste più in questa
    /// versione della mod), riprova gruppo per gruppo, così una sola chiave sbagliata non blocca il resto.
    /// Un gruppo = un'impostazione di primo livello, quindi una lista resta sempre intera.
    /// </summary>
    private async Task<(int Applied, List<string> RejectedRoots)> ApplySettingsAsync(
        string modId, List<KeyValuePair<string, string>> settings)
    {
        var all = await cli.SetSettingsAsync(modId, settings);
        if (all.ExitCode == CliExitCode.Success) return (settings.Count, new());
        if (all.ExitCode != CliExitCode.UsageError)
            throw new WindhawkCliException($"Impostazioni di {modId} non applicate ({WindhawkCli.ErrorText(all)})");

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

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..60] + "...";
}
