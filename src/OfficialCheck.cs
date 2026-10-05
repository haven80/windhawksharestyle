using System.Security.Cryptography;
using System.Text;

namespace WindhawkShare;

public enum OfficialStatus
{
    /// <summary>In the official repository, local source identical to the official one.</summary>
    Verified,
    /// <summary>In the official repository, but the local source can't be compared (e.g. not the latest version).</summary>
    OfficialIdOnly,
    /// <summary>Locally created mod: never exportable.</summary>
    LocalMod,
    /// <summary>The ID doesn't exist in the official repository: never exportable.</summary>
    NotInRepository,
    /// <summary>The installed source differs from the official one: never exportable.</summary>
    Modified,
}

public sealed record OfficialCheckResult(OfficialStatus Status, string Message, string? LatestVersion)
{
    public bool Exportable => Status is OfficialStatus.Verified or OfficialStatus.OfficialIdOnly;
}

/// <summary>
/// Enforces the core rule: only unmodified mods from the official repository are shared.
/// The repository address is hard-coded here and never read from files or packages.
/// </summary>
public sealed class OfficialCheck(WindhawkCli cli, HttpClient http, string modsSourceDir)
{
    private const string OfficialRawBase =
        "https://raw.githubusercontent.com/ramensoftware/windhawk-mods/main/mods/";

    public static string DefaultModsSourceDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Windhawk", "ModsSource");

    public async Task<OfficialCheckResult> CheckAsync(InstalledMod mod)
    {
        // Locally created mods have a "local@" prefix.
        if (mod.Id.StartsWith("local@", StringComparison.OrdinalIgnoreCase) || !IsValidModId(mod.Id))
            return new(OfficialStatus.LocalMod, "local mod, not in the official repository", null);

        var repo = await cli.RepoShowAsync(mod.Id);
        if (repo is null)
            return new(OfficialStatus.NotInRepository, "not in the official repository", null);

        if (!string.Equals(repo.Version, mod.Version, StringComparison.Ordinal))
            return new(OfficialStatus.OfficialIdOnly,
                $"official mod; version {mod.Version} is installed but the latest is {repo.Version}, " +
                "so the source can't be compared", repo.Version);

        // Same version as the latest official one: compare the installed source with the official source.
        var localPath = Path.Combine(modsSourceDir, mod.Id + ".wh.cpp");
        if (!File.Exists(localPath))
            return new(OfficialStatus.OfficialIdOnly,
                "official mod; local source not found, comparison not possible", repo.Version);

        string officialSource;
        try
        {
            officialSource = await http.GetStringAsync(OfficialRawBase + mod.Id + ".wh.cpp");
        }
        catch (HttpRequestException e)
        {
            return new(OfficialStatus.OfficialIdOnly,
                $"official mod; could not download the official source ({e.Message})", repo.Version);
        }

        var localSource = await File.ReadAllTextAsync(localPath);
        if (Hash(Normalize(localSource)) != Hash(Normalize(officialSource)))
            return new(OfficialStatus.Modified,
                "the installed source differs from the official one (locally modified mod)",
                repo.Version);

        return new(OfficialStatus.Verified, "verified against the official repository", repo.Version);
    }

    /// <summary>
    /// Official mod IDs: lowercase letters, digits and hyphens, starting with a letter or digit
    /// (an ID starting with "-" would be mistaken for a CLI option).
    /// </summary>
    public static bool IsValidModId(string id) =>
        id.Length is > 0 and <= 128 &&
        char.IsAsciiLetterOrDigit(id[0]) &&
        id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    /// <summary>Same normalization as the CLI: no BOM, Unix line endings.</summary>
    private static string Normalize(string source) =>
        source.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
