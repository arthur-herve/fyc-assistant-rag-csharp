// Une mesure grossière du contenu d'une réponse : la part des mots-clés attendus qu'elle contient, sans tenir compte
// de la casse ni des accents.

using System.Text;

namespace Assistant.Cli;

public static class Keywords
{
    /// <summary>La part des mots-clés que contient le texte, ou null s'il n'y a aucun mot-clé à chercher.</summary>
    public static double? Coverage(string text, IReadOnlyList<string> keywords)
    {
        if (keywords.Count == 0)
        {
            return null;
        }
        var plain = Plain(text);
        return (double)keywords.Count(k => plain.Contains(Plain(k), StringComparison.Ordinal)) / keywords.Count;
    }

    /// <summary>
    /// Met le texte en minuscules sans accents, pour une comparaison volontairement grossière. On passe par une
    /// table plutôt que par <c>Normalize</c> : l'application tourne en globalisation invariante
    /// (Directory.Build.props), où la décomposition Unicode n'est pas garantie.
    /// </summary>
    public static string Plain(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            var folded = Accents.IndexOf(c);
            if (folded >= 0)
            {
                builder.Append(Folded[folded]);
            }
            else if (c < 128)
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }

    private const string Accents = "àáâãäåçèéêëìíîïñòóôõöùúûüýÿ";
    private const string Folded = "aaaaaaceeeeiiiinooooouuuuyy";
}
