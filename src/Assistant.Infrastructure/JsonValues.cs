// Les valeurs de configuration écrites en JSON, puis relues avec leur type : booléen, entier, réel, texte, liste ou
// dictionnaire.

using System.Text.Json.Nodes;

namespace Assistant.Infrastructure;

/// <summary>
/// Convertit vers et depuis JSON les petits dictionnaires de configuration, comme celui du découpage.
/// </summary>
public static class JsonValues
{
    public static object ToObject(JsonNode node) => node switch
    {
        JsonObject o => o.ToDictionary(kv => kv.Key, kv => kv.Value is null ? (object)"" : ToObject(kv.Value), StringComparer.Ordinal),
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<int>(out var i) => i,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray a => a.Select(item => item is null ? (object)"" : ToObject(item)).ToList(),
        _ => node.ToJsonString(),
    };

    public static object? ToObjectOrNull(JsonNode? node) => node is null ? null : ToObject(node);

    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        IReadOnlyDictionary<string, object?> d => FromDictionary(d),
        System.Collections.IEnumerable items => new JsonArray(items.Cast<object?>().Select(ToNode).ToArray()),
        _ => throw new ArgumentException($"valeur non sérialisable : {value.GetType().Name}"),
    };

    public static JsonObject FromDictionary<TValue>(IReadOnlyDictionary<string, TValue> values)
    {
        var result = new JsonObject();
        foreach (var (key, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))   // clés triées
        {
            result[key] = ToNode(value);
        }
        return result;
    }
}
