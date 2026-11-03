// Les valeurs de configuration, comme celles d'un instantané ou du découpage, se comparent et s'affichent sous une
// même forme canonique.

using System.Globalization;

namespace Assistant.Application;

public static class ConfigurationValues
{
    /// <summary>
    /// Forme sous laquelle on compare et on affiche une valeur de configuration. Elle compare les valeurs et non les
    /// types : 4 et 4.0 sont égaux, car un réel entier s'écrit sans « .0 » et se relit comme un entier. La contrepartie
    /// est qu'un texte se confond avec le nombre ou le booléen qui s'écrit pareil, comme « 4 » et 4 ou « true » et true.
    /// </summary>
    public static string Canonical(object? value) => value switch
    {
        null => "null",
        string s => s,
        IReadOnlyDictionary<string, object> d => "{" + string.Join(", ", d.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={Canonical(kv.Value)}")) + "}",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture)!,
        System.Collections.IEnumerable items => "[" + string.Join(", ", items.Cast<object?>().Select(Canonical)) + "]",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };
}
