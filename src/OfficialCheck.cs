using System.Security.Cryptography;
using System.Text;

namespace WindhawkShare;

public enum OfficialStatus
{
    /// <summary>Nel repository ufficiale, sorgente locale identico a quello ufficiale.</summary>
    Verified,
    /// <summary>Nel repository ufficiale, ma il sorgente locale non è confrontabile (es. versione non più recente).</summary>
    OfficialIdOnly,
    /// <summary>Mod creata in locale: mai esportabile.</summary>
    LocalMod,
    /// <summary>L'ID non esiste nel repository ufficiale: mai esportabile.</summary>
    NotInRepository,
    /// <summary>Il sorgente installato è diverso da quello ufficiale: mai esportabile.</summary>
    Modified,
}

public sealed record OfficialCheckResult(OfficialStatus Status, string Message, string? LatestVersion)
{
    public bool Exportable => Status is OfficialStatus.Verified or OfficialStatus.OfficialIdOnly;
}

/// <summary>
/// Applica la regola fondamentale: si condividono solo mod del repository ufficiale, non modificate.
/// L'indirizzo del repository è fisso qui nel codice e non viene mai letto da file o pacchetti.
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
        // Le mod create in locale hanno un prefisso tipo "local@".
        if (mod.Id.StartsWith("local@", StringComparison.OrdinalIgnoreCase) || !IsValidModId(mod.Id))
            return new(OfficialStatus.LocalMod, "mod locale, non presente nel repository ufficiale", null);

        var repo = await cli.RepoShowAsync(mod.Id);
        if (repo is null)
            return new(OfficialStatus.NotInRepository, "non presente nel repository ufficiale", null);

        if (!string.Equals(repo.Version, mod.Version, StringComparison.Ordinal))
            return new(OfficialStatus.OfficialIdOnly,
                $"mod ufficiale; installata la {mod.Version}, l'ultima è la {repo.Version}, " +
                "quindi il sorgente non è confrontabile", repo.Version);

        // Stessa versione dell'ultima ufficiale: confronto il sorgente installato con quello ufficiale.
        var localPath = Path.Combine(modsSourceDir, mod.Id + ".wh.cpp");
        if (!File.Exists(localPath))
            return new(OfficialStatus.OfficialIdOnly,
                "mod ufficiale; sorgente locale non trovato, confronto non possibile", repo.Version);

        string officialSource;
        try
        {
            officialSource = await http.GetStringAsync(OfficialRawBase + mod.Id + ".wh.cpp");
        }
        catch (HttpRequestException e)
        {
            return new(OfficialStatus.OfficialIdOnly,
                $"mod ufficiale; sorgente ufficiale non scaricabile ({e.Message})", repo.Version);
        }

        var localSource = await File.ReadAllTextAsync(localPath);
        if (Hash(Normalize(localSource)) != Hash(Normalize(officialSource)))
            return new(OfficialStatus.Modified,
                "il sorgente installato è diverso da quello ufficiale (mod modificata in locale)",
                repo.Version);

        return new(OfficialStatus.Verified, "verificata con il repository ufficiale", repo.Version);
    }

    /// <summary>
    /// ID delle mod ufficiali: lettere minuscole, numeri e trattini, e deve iniziare con lettera o numero
    /// (un ID che inizia con "-" verrebbe scambiato per un'opzione della CLI).
    /// </summary>
    public static bool IsValidModId(string id) =>
        id.Length is > 0 and <= 128 &&
        char.IsAsciiLetterOrDigit(id[0]) &&
        id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    /// <summary>Stessa normalizzazione della CLI: niente BOM, fine riga Unix.</summary>
    private static string Normalize(string source) =>
        source.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
