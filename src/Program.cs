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
    Console.Error.WriteLine($"Error: {e.Message}");
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
        "import" => await ImportCommand(cli, options),
        _ => Usage($"Unknown command: {command}"),
    };
}

static async Task<int> ListCommand(WindhawkCli cli)
{
    var mods = await cli.ListInstalledModsAsync();
    if (mods.Count == 0)
    {
        Console.WriteLine("No mods installed.");
        return 0;
    }
    foreach (var m in mods)
        Console.WriteLine($"{(m.Enabled ? "[on] " : "[off]")} {m.Id,-40} {m.Version,-10} {m.Name}");
    return 0;
}

static async Task<int> SettingsCommand(WindhawkCli cli, Options options)
{
    if (options.Positional.Count != 1) return Usage("Usage: windhawk-share settings <mod-id>");
    var settings = await cli.GetSettingsAsync(options.Positional[0]);
    var roots = SettingsTools.Roots(settings);
    if (roots.Count == 0)
    {
        Console.WriteLine("This mod has no settings.");
        return 0;
    }
    Console.WriteLine("Settings (select them with --mod id:Name1,Name2):");
    foreach (var (root, count) in roots)
        Console.WriteLine(count == 1 ? $"  {root}" : $"  {root}  ({count} values, shared together)");
    return 0;
}

static async Task<int> ExportCommand(WindhawkCli cli, Options options)
{
    if (string.IsNullOrWhiteSpace(options.Output))
        return Usage("Specify the output file with -o <file.json>");

    var installed = await cli.ListInstalledModsAsync();
    if (installed.Count == 0)
    {
        Console.Error.WriteLine("No installed mods to export.");
        return 1;
    }

    var selections = options.Mods.Count > 0
        ? options.Mods
        : await InteractiveSelection(cli, installed);
    if (selections.Count == 0)
    {
        Console.Error.WriteLine("No mods selected.");
        return 1;
    }

    var meta = new PackageMeta
    {
        Name = options.Name ?? Ask("Package name: "),
        Author = options.Author ?? Ask("Author: "),
        Description = options.Description ?? Ask("Description (optional): "),
        CreatedAt = DateTimeOffset.UtcNow,
        WindhawkVersion = await cli.GetVersionAsync(),
        Windows = Exporter.CurrentWindows(),
    };

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("windhawk-share/0.1");
    var check = new OfficialCheck(cli, http, options.ModsSourceDir ?? OfficialCheck.DefaultModsSourceDir);

    Console.WriteLine("Verifying against the official repository...");
    var report = await new Exporter(cli, check).BuildAsync(selections, meta, installed);

    foreach (var (id, reason) in report.Excluded)
        Console.WriteLine($"  EXCLUDED  {id}: {reason}");
    foreach (var w in report.Warnings)
        Console.WriteLine($"  warning   {w}");

    if (report.Package.Mods.Count == 0)
    {
        Console.Error.WriteLine("No exportable mods: package not created.");
        return 1;
    }

    var json = JsonSerializer.Serialize(report.Package, PackageJsonContext.Default.SetupPackage);
    await File.WriteAllTextAsync(options.Output, json, new UTF8Encoding(false));

    Console.WriteLine();
    Console.WriteLine($"Package saved to {Path.GetFullPath(options.Output)}");
    foreach (var m in report.Package.Mods)
        Console.WriteLine($"  {m.Id} {m.Version}: {m.Settings.Count} values");
    return 0;
}

static async Task<int> ImportCommand(WindhawkCli cli, Options options)
{
    if (options.Positional.Count != 1) return Usage("Usage: windhawk-share import <package.json>");

    var package = ImportValidation.Load(options.Positional[0]);
    var meta = package.Meta;
    Console.WriteLine($"Package: {meta.Name}  (by {meta.Author})");
    if (!string.IsNullOrWhiteSpace(meta.Description)) Console.WriteLine($"  {meta.Description}");
    Console.WriteLine($"  Created on {meta.Windows.Product} build {meta.Windows.Build}, Windhawk {meta.WindhawkVersion ?? "?"}");

    var current = Exporter.CurrentWindows();
    if (current.Product != meta.Windows.Product)
        Console.WriteLine($"  WARNING: you are on {current.Product}, some mods may not work.");

    Console.WriteLine();
    Console.WriteLine("Verifying against the official repository...");
    var importer = new Importer(cli);
    var plan = await importer.PlanAsync(package, options.OnlyMods, options.ExactVersion);

    Console.WriteLine();
    Console.WriteLine("What will happen:");
    foreach (var p in plan)
    {
        var what = p.Action switch
        {
            ModAction.Install => $"INSTALL {p.InstallVersion ?? "(latest version)"}, {p.Settings.Count} settings",
            ModAction.UpdateSettingsOnly => $"already installed, update {p.Settings.Count} settings",
            _ => "skip",
        };
        Console.WriteLine($"  {p.Source.Id}: {what}");
        foreach (var note in p.Notes) Console.WriteLine($"      - {note}");
    }

    var todo = plan.Where(p => p.Action != ModAction.Skip).ToList();
    if (todo.Count == 0)
    {
        Console.WriteLine("Nothing to do.");
        return 0;
    }

    // Text settings may contain paths or commands: show them before applying.
    var suspicious = todo
        .SelectMany(p => p.Settings.Select(kv => (p.Source.Id, kv.Key, kv.Value)))
        .Where(x => ImportValidation.LooksLikePathOrCommand(x.Value))
        .ToList();
    if (suspicious.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Settings containing paths or commands, please review them:");
        foreach (var (id, key, value) in suspicious)
            Console.WriteLine($"  {id} / {key} = {(value.Length > 120 ? value[..120] + "..." : value)}");
    }

    if (!Importer.IsElevated())
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine(new AdminRequiredException().Message);
        return 1;
    }

    if (!options.Yes)
    {
        Console.WriteLine();
        if (!Ask("Proceed? (y/N): ").Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Cancelled, nothing was changed.");
            return 0;
        }
    }

    Console.WriteLine();
    var summary = await importer.ExecuteAsync(plan, msg => Console.WriteLine(msg));
    Console.WriteLine();
    Console.WriteLine("Summary:");
    foreach (var line in summary) Console.WriteLine($"  {line}");
    return summary.Any(l => l.StartsWith("ERROR")) ? 1 : 0;
}

static async Task<List<ModSelection>> InteractiveSelection(WindhawkCli cli, List<InstalledMod> installed)
{
    Console.WriteLine("Installed mods:");
    for (var i = 0; i < installed.Count; i++)
        Console.WriteLine($"  {i + 1,2}. {installed[i].Name} ({installed[i].Id})");

    var picked = ParseNumbers(Ask("Numbers of the mods to share, comma-separated: "), installed.Count);
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
        Console.WriteLine($"Settings of {mod.Name}:");
        for (var i = 0; i < roots.Count; i++)
        {
            var (root, count) = roots[i];
            Console.WriteLine(count == 1 ? $"  {i + 1,2}. {root}" : $"  {i + 1,2}. {root} ({count} values)");
        }

        var answer = Ask("Enter = all, or the numbers to include: ");
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
    Console.Error.WriteLine("Run 'windhawk-share help' for the list of commands.");
    return 2;
}

static void PrintHelp()
{
    Console.WriteLine("""
        windhawk-share: share your Windhawk setups (requires Windhawk 2.0+)

        Commands:
          list                         List installed mods
          settings <mod-id>            Show the selectable settings of a mod
          export -o <file.json>        Create a package (without --mod, starts guided selection)
          import <file.json>           Install the package's mods and apply its settings

        Export options:
          --mod <id>                   Include the mod with all its settings (repeatable)
          --mod <id>:Name1,Name2       Include only some settings
          --name, --author, --description <text>
          --cli <path>                 Path to windhawk-cli.exe
          --mods-source <folder>       Folder with the installed mods' sources

        Import options:
          --only <id1,id2>             Import only some of the package's mods
          --exact-version              Install the package's version instead of the latest
          --yes                        Don't ask for confirmation

        Import requires administrator rights.

        Examples:
          windhawk-share export -o my-explorer.json --mod explorer-style --name "Dark Explorer"
          windhawk-share import my-explorer.json
        """);
}

/// <summary>Minimal argument parsing, no external dependencies.</summary>
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
    public bool Yes { get; private set; }
    public bool ExactVersion { get; private set; }
    public IReadOnlySet<string>? OnlyMods { get; private set; }

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
            if (a == "--yes") { o.Yes = true; continue; }
            if (a == "--exact-version") { o.ExactVersion = true; continue; }
            if (i + 1 >= args.Length)
            {
                o.Error = $"Missing value for {a}";
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
                case "--only":
                    o.OnlyMods = value
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToHashSet(StringComparer.Ordinal);
                    break;
                case "--mod":
                    var sel = ParseModSelection(value);
                    if (sel is null)
                    {
                        o.Error = $"Invalid selection: {value}";
                        return o;
                    }
                    o.Mods.Add(sel);
                    break;
                default:
                    o.Error = $"Unknown option: {a}";
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
