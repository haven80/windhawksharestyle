using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindhawkShare;

/// <summary>
/// Il pacchetto condivisibile. Per costruzione NON contiene codice, URL o sorgenti:
/// solo ID di mod del repository ufficiale, versioni e valori di impostazioni.
/// </summary>
public sealed class SetupPackage
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public PackageMeta Meta { get; set; } = new();
    public List<PackageMod> Mods { get; set; } = new();
}

public sealed class PackageMeta
{
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string? WindhawkVersion { get; set; }
    public WindowsInfo Windows { get; set; } = new();
}

public sealed class WindowsInfo
{
    public string Product { get; set; } = "";
    public int Build { get; set; }
}

public sealed class PackageMod
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Impostazioni piatte nel formato di Windhawk (es. "TimeStyle.FontSize", "lista[0].nome").
    /// Valori ammessi: stringa, numero intero, booleano.
    /// In importazione le chiavi presenti sovrascrivono, quelle assenti restano come sono.
    /// </summary>
    public Dictionary<string, JsonElement> Settings { get; set; } = new();
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SetupPackage))]
internal partial class PackageJsonContext : JsonSerializerContext
{
}
