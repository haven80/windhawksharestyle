using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindhawkShare;

/// <summary>
/// The shareable package. By design it contains NO code, URLs or sources:
/// only official-repository mod IDs, versions and setting values.
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
    /// Flat settings in Windhawk's format (e.g. "TimeStyle.FontSize", "list[0].name").
    /// Allowed values: string, integer, boolean.
    /// On import, keys present here overwrite; keys not present are left as they are.
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
