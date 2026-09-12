// Règle métier n° 2 : toute réponse cite ses sources.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Coeur;

/// <summary>Résultat de la vérification des citations d'une réponse.</summary>
public sealed record CitationCheck(IReadOnlyList<int> Cited, IReadOnlyList<int> Invalid)
{
    public bool IsValid => Cited.Count > 0 && Invalid.Count == 0;
}

/// <summary>
/// Règle métier n° 2 : le modèle est probabiliste, la vérification ne l'est pas.
/// On contrôle que la réponse contient au moins une citation et que chaque
/// citation renvoie à un passage réellement fourni. Formats acceptés : [1], [2, 3], [2,3].
/// </summary>
public static class Citations
{
    private static readonly Regex Citation = new(@"\[([0-9]+(?:\s*,\s*[0-9]+)*)\]", RegexOptions.Compiled);

    public static CitationCheck Check(string text, int passageCount)
    {
        var numbers = new List<int>();
        foreach (Match match in Citation.Matches(text))
        {
            foreach (var part in match.Groups[1].Value.Split(','))
            {
                // Un nombre trop grand pour un int ([33612345678]) est une citation invalide, pas un plantage.
                var number = int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : int.MaxValue;
                if (!numbers.Contains(number))
                {
                    numbers.Add(number);
                }
            }
        }
        var valid = numbers.Where(n => n >= 1 && n <= passageCount).ToList();
        var invalid = numbers.Where(n => n < 1 || n > passageCount).ToList();
        return new CitationCheck(valid, invalid);
    }
}
