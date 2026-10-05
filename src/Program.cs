using System.Text;
using System.Text.Json;
using WindhawkShare;

Console.OutputEncoding = Encoding.UTF8;

try
{
    return await Run(args);
}
catch (WindhawkCliException e)
{
    Console.Error.WriteLine($"Errore: {e.Message}");
    return 1;
}

static async Task<int> Run(string[] args)
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    {
        PrintHelp();
        return 0;
    }

    var command = args[0];
    var options = Options.Parse(args.Skip(1).ToArray());
    if (options.Error is not null)
    {
        Console.Error.WriteLine(options.Error);
        return 2;
    }

    var cli = WindhawkCli.Locate(options.CliPath);

    return command switch
    {
        "list" => await ListCommand(cli),
        "settings" => await SettingsCommand(cli, options),
        "export" => await ExportCommand(cli, options),
        _ => Usage($"Comando sconosciuto: {command}"),
    };
}

static async Task<int> ListCommand(WindhawkCli cli)
{
    var mods = await cli.ListInstalledModsAsync();
    if (mods.Count == 0)
    {
        Console.WriteLine("Nessuna mod installata.");
        return 0;
    }
    foreach (var m in mods)
        Console.WriteLine($"{(m.Enabled ? "[on] " : "[off]")} {m.Id,-40} {m.Version,-10} {m.Name}");
    return 0;
}

static async Task<int> SettingsCommand(WindhawkCli cli, Options options)
{
    if (options.Positional.Count != 1) return Usage("Uso: windhawk-share settings <id-mod>");
    var settings = await cli.GetSettingsAsync(options.Positional[0]);
    var roots = SettingsTools.Roots(settings);
    if (roots.Count == 0)
    {
        Console.WriteLine("Questa mod non ha impostazioni.");
        return 0;
    }
    Console.WriteLine("Impostazioni (selezionabili con --mod id:Nome1,Nome2):");
    foreach (var (root, count) in roots)
        Console.WriteLine(count == 1 ? $"  {root}" : $"  {root}  ({count} valori, condivisi insieme)");
    return 0;
}

static async Task<int> ExportCommand(WindhawkCli cli, Options options)
{
    if (string.IsNullOrWhiteSpace(options.Output))
        return Usage("Specifica il file di destinazione con -o <file.json>");

    var installed = await cli.ListInstalledModsAsync();
    if (installed.Count == 0)
    {
        Console.Error.WriteLine("Nessuna mod installata da esportare.");
        return 1;
    }

    var selections = options.Mods.Count > 0
        ? options.Mods
        : await InteractiveSelection(cli, installed);
    if (selections.Count == 0)
    {
        Console.Error.WriteLine("Nessuna mod selezionata.");
        return 1;
    }

    var meta = new PackageMeta
    {
        Name = options.Name ?? Ask("Nome del pacchetto: "),
        Author = options.Author ?? Ask("Autore: "),
        Description = options.Description ?? Ask("Descrizione (facoltativa): "),
        CreatedAt = DateTimeOffset.UtcNow,
        WindhawkVersion = await cli.GetVersionAsync(),
        Windows = Exporter.CurrentWindows(),
    };

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("windhawk-share/0.1");
    var check = new OfficialCheck(cli, http, options.ModsSourceDir ?? OfficialCheck.DefaultModsSourceDir);

    Console.WriteLine("Verifica con il repository ufficiale in corso...");
    var report = await new Exporter(cli, check).BuildAsync(selections, meta, installed);

    foreach (var (id, reason) in report.Excluded)
        Console.WriteLine($"  ESCLUSA  {id}: {reason}");
    foreach (var w in report.Warnings)
        Console.WriteLine($"  avviso   {w}");

    if (report.Package.Mods.Count == 0)
    {
        Console.Error.WriteLine("Nessuna mod esportabile: pacchetto non creato.");
        return 1;
    }

    var json = JsonSerializer.Serialize(report.Package, PackageJsonContext.Default.SetupPackage);
    await File.WriteAllTextAsync(options.Output, json, new UTF8Encoding(false));

    Console.WriteLine();
    Console.WriteLine($"Pacchetto salvato in {Path.GetFullPath(options.Output)}");
    foreach (var m in report.Package.Mods)
        Console.WriteLine($"  {m.Id} {m.Version}: {m.Settings.Count} valori");
    return 0;
}

static async Task<List<ModSelection>> InteractiveSelection(WindhawkCli cli, List<InstalledMod> installed)
{
    Console.WriteLine("Mod installate:");
    for (var i = 0; i < installed.Count; i++)
        Console.WriteLine($"  {i + 1,2}. {installed[i].Name} ({installed[i].Id})");

    var picked = ParseNumbers(Ask("Numeri delle mod da condividere, separati da virgola: "), installed.Count);
    var result = new List<ModSelection>();

    foreach (var index in picked)
    {
        var mod = installed[index];
        var roots = SettingsTools.Roots(await cli.GetSettingsAsync(mod.Id));
        if (roots.Count == 0)
        {
            result.Add(new ModSelection(mod.Id, null));
            continue;
        }

        Console.WriteLine();
        Console.WriteLine($"Impostazioni di {mod.Name}:");
        for (var i = 0; i < roots.Count; i++)
        {
            var (root, count) = roots[i];
            Console.WriteLine(count == 1 ? $"  {i + 1,2}. {root}" : $"  {i + 1,2}. {root} ({count} valori)");
        }

        var answer = Ask("Invio = tutte, oppure i numeri da includere: ");
        if (string.IsNullOrWhiteSpace(answer))
        {
            result.Add(new ModSelection(mod.Id, null));
        }
        else
        {
            var chosen = ParseNumbers(answer, roots.Count)
                .Select(i => roots[i].Root)
                .ToHashSet(StringComparer.Ordinal);
            result.Add(new ModSelection(mod.Id, chosen));
        }
    }
    return result;
}

static List<int> ParseNumbers(string input, int max) =>
    input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => int.TryParse(s, out var n) ? n - 1 : -1)
        .Where(n => n >= 0 && n < max)
        .Distinct()
        .ToList();

static string Ask(string prompt)
{
    Console.Write(prompt);
    return Console.ReadLine()?.Trim() ?? "";
}

static int Usage(string message)
{
    Console.Error.WriteLine(message);
    Console.Error.WriteLine("Usa 'windhawk-share help' per l'elenco dei comandi.");
    return 2;
}

static void PrintHelp()
{
    Console.WriteLine("""
        windhawk-share: condividi i tuoi setup di Windhawk (richiede Windhawk 2.0+)

        Comandi:
          list                         Elenca le mod installate
          settings <id-mod>            Mostra le impostazioni selezionabili di una mod
          export -o <file.json>        Crea un pacchetto (senza --mod parte la scelta guidata)

        Opzioni di export:
          --mod <id>                   Includi la mod con tutte le impostazioni (ripetibile)
          --mod <id>:Nome1,Nome2       Includi solo alcune impostazioni
          --name, --author, --description <testo>
          --cli <percorso>             Percorso di windhawk-cli.exe
          --mods-source <cartella>     Cartella dei sorgenti delle mod installate

        Esempio:
          windhawk-share export -o mio-explorer.json --mod explorer-style --name "Explorer scuro"
        """);
}

/// <summary>Parsing minimale degli argomenti, senza dipendenze esterne.</summary>
sealed class Options
{
    public List<string> Positional { get; } = new();
    public List<ModSelection> Mods { get; } = new();
    public string? Output { get; private set; }
    public string? Name { get; private set; }
    public string? Author { get; private set; }
    public string? Description { get; private set; }
    public string? CliPath { get; private set; }
    public string? ModsSourceDir { get; private set; }
    public string? Error { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith('-'))
            {
                o.Positional.Add(a);
                continue;
            }
            if (i + 1 >= args.Length)
            {
                o.Error = $"Manca il valore per {a}";
                return o;
            }
            var value = args[++i];
            switch (a)
            {
                case "-o" or "--output": o.Output = value; break;
                case "--name": o.Name = value; break;
                case "--author": o.Author = value; break;
                case "--description": o.Description = value; break;
                case "--cli": o.CliPath = value; break;
                case "--mods-source": o.ModsSourceDir = value; break;
                case "--mod":
                    var sel = ParseModSelection(value);
                    if (sel is null)
                    {
                        o.Error = $"Selezione non valida: {value}";
                        return o;
                    }
                    o.Mods.Add(sel);
                    break;
                default:
                    o.Error = $"Opzione sconosciuta: {a}";
                    return o;
            }
        }
        return o;
    }

    private static ModSelection? ParseModSelection(string value)
    {
        var parts = value.Split(':', 2);
        var id = parts[0].Trim();
        if (id.Length == 0) return null;
        if (parts.Length == 1) return new ModSelection(id, null);

        var roots = parts[1]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        return roots.Count == 0 ? null : new ModSelection(id, roots);
    }
}
