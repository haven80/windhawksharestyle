using System.Text.Json;

namespace WindhawkShare;

/// <summary>A mod chosen for export. Roots null = all settings.</summary>
public sealed record ModSelection(string Id, IReadOnlySet<string>? Roots);

public sealed class ExportReport
{
    public SetupPackage Package { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<(string Id, string Reason)> Excluded { get; } = new();
}

public sealed class Exporter(WindhawkCli cli, OfficialCheck officialCheck)
{
    public async Task<ExportReport> BuildAsync(
        IReadOnlyList<ModSelection> selections, PackageMeta meta, IReadOnlyList<InstalledMod> installed)
    {
        var report = new ExportReport();
        report.Package.Meta = meta;

        var byId = installed.ToDictionary(m => m.Id, StringComparer.Ordinal);

        foreach (var sel in selections)
        {
            if (!byId.TryGetValue(sel.Id, out var mod))
            {
                report.Excluded.Add((sel.Id, "not installed"));
                continue;
            }

            var check = await officialCheck.CheckAsync(mod);
            if (!check.Exportable)
            {
                report.Excluded.Add((mod.Id, check.Message));
                continue;
            }
            if (check.Status == OfficialStatus.OfficialIdOnly)
                report.Warnings.Add($"{mod.Id}: {check.Message}");

            var all = await cli.GetSettingsAsync(mod.Id);
            var chosen = sel.Roots is null ? all : SettingsTools.FilterByRoots(all, sel.Roots);

            if (sel.Roots is not null)
            {
                var existing = SettingsTools.Roots(all).Select(r => r.Root).ToHashSet(StringComparer.Ordinal);
                foreach (var missing in sel.Roots.Where(r => !existing.Contains(r)))
                    report.Warnings.Add($"{mod.Id}: setting '{missing}' doesn't exist, skipped");
            }

            var settings = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var (key, value) in chosen)
            {
                if (SettingsTools.IsSupportedValue(value)) settings[key] = value;
                else report.Warnings.Add($"{mod.Id}: unsupported value type for '{key}', skipped");
            }

            report.Package.Mods.Add(new PackageMod
            {
                Id = mod.Id,
                Version = mod.Version,
                Enabled = mod.Enabled,
                Settings = settings,
            });
        }

        return report;
    }

    public static WindowsInfo CurrentWindows()
    {
        var build = Environment.OSVersion.Version.Build;
        return new WindowsInfo
        {
            Product = build >= 22000 ? "Windows 11" : "Windows 10",
            Build = build,
        };
    }
}
