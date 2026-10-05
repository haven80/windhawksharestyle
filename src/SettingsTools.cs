using System.Text.Json;

namespace WindhawkShare;

public static class SettingsTools
{
    /// <summary>
    /// Appiattisce impostazioni annidate nel formato di Windhawk:
    /// oggetti con il punto ("TimeStyle.FontSize"), liste con l'indice ("lista[0].nome").
    /// Se la CLI le restituisce già piatte, le lascia invariate.
    /// </summary>
    public static void Flatten(JsonElement element, string prefix, Dictionary<string, JsonElement> output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var key = prefix.Length == 0 ? prop.Name : $"{prefix}.{prop.Name}";
                    Flatten(prop.Value, key, output);
                }
                break;

            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                    Flatten(item, $"{prefix}[{i++}]", output);
                break;

            default:
                if (prefix.Length > 0) output[prefix] = element.Clone();
                break;
        }
    }

    /// <summary>
    /// L'impostazione di primo livello a cui appartiene una chiave.
    /// "TimeStyle.FontSize" -> "TimeStyle", "controlStyles[1].target" -> "controlStyles".
    /// Selezionando per radice, una lista viene sempre condivisa intera: mai pezzi incoerenti.
    /// </summary>
    public static string RootOf(string key)
    {
        var cut = key.IndexOfAny(['.', '[']);
        return cut < 0 ? key : key[..cut];
    }

    /// <summary>Radici in ordine di prima apparizione, con il numero di chiavi di ciascuna.</summary>
    public static List<(string Root, int KeyCount)> Roots(Dictionary<string, JsonElement> settings)
    {
        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in settings.Keys)
        {
            var root = RootOf(key);
            if (counts.TryGetValue(root, out var n)) counts[root] = n + 1;
            else { counts[root] = 1; order.Add(root); }
        }
        return order.Select(r => (r, counts[r])).ToList();
    }

    public static Dictionary<string, JsonElement> FilterByRoots(
        Dictionary<string, JsonElement> settings, IReadOnlySet<string> roots) =>
        settings
            .Where(kv => roots.Contains(RootOf(kv.Key)))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    /// <summary>Windhawk gestisce solo stringhe e interi (i booleani sono interi 0/1).</summary>
    public static bool IsSupportedValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => true,
        JsonValueKind.True or JsonValueKind.False => true,
        JsonValueKind.Number => value.TryGetInt64(out _),
        _ => false,
    };
}
