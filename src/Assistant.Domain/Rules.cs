// Règles métier : qui peut lire quoi, toute réponse cite ses sources, à quoi
// ressemble une réponse acceptable. Trois fonctions pures, testables en microsecondes.

using System.Text.RegularExpressions;

namespace Assistant.Domain;

public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
}

public sealed class EmptyQuestionException : DomainException
{
    public EmptyQuestionException() : base("La question est vide.") { }
}

/// <summary>
/// Règle métier n° 1 : le contrôle d'accès est appliqué de façon déterministe AVANT
/// l'appel au modèle. On ne demande jamais au modèle de « ne pas révéler » un
/// document : un passage interdit n'entre tout simplement pas dans le prompt.
/// </summary>
public sealed class AccessPolicy
{
    public const string PublicGroup = "tous";

    public bool CanRead(User user, Chunk chunk)
    {
        if (chunk.AllowedGroups.Contains(PublicGroup))
        {
            return true;
        }
        return user.Groups.Overlaps(chunk.AllowedGroups);
    }
}

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
    private static readonly Regex Citation = new(@"\[(\d+(?:\s*,\s*\d+)*)\]", RegexOptions.Compiled);

    public static CitationCheck Check(string text, int passageCount)
    {
        var numbers = new List<int>();
        foreach (Match match in Citation.Matches(text))
        {
            foreach (var part in match.Groups[1].Value.Split(','))
            {
                var number = int.Parse(part.Trim());
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

public sealed record OutputCheck(IReadOnlyList<string> Problems)
{
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// Règle métier n° 3 : à quoi doit ressembler une réponse avant d'être montrée.
/// Complète les citations (la forme) par le fond : une réponse vide, trop longue,
/// dans la mauvaise langue ou qui déverse un raisonnement n'est pas une réponse,
/// même si elle contient « [1] ». Ces règles ne connaissent aucun modèle en
/// particulier ; ce qui est propre à un modèle est neutralisé côté service IA.
/// </summary>
public static class OutputRules
{
    private static readonly HashSet<string> FrenchMarkers =
        "le la les des une un du de et est pas pour vous votre dans par sur au aux".Split(' ').ToHashSet();

    private static readonly HashSet<string> EnglishMarkers =
        "the is are and user let's okay this that with for answer should".Split(' ').ToHashSet();

    // Marqueurs génériques d'un raisonnement déversé, bornés par des mots.
    private static readonly Regex Reasoning = new(
        @"</?think>|\b(?:okay|ok),\s+(?:let|so)\b|\blet me\b|\bthe user is asking\b|\bfirst,\s+i need\b|\bwait,\s",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Word = new(@"[a-zàâçéèêëîïôûùüÿœ']+", RegexOptions.Compiled);

    public static OutputCheck Check(string text, int maxChars = 1500)
    {
        var problems = new List<string>();
        var stripped = text.Trim();
        if (stripped.Length == 0)
        {
            return new OutputCheck(new[] { "réponse vide" });
        }
        if (stripped.Length > maxChars)
        {
            problems.Add($"réponse trop longue ({stripped.Length} caractères, {maxChars} au plus)");
        }
        var found = Reasoning.Match(stripped);
        if (found.Success)
        {
            problems.Add($"raisonnement du modèle déversé dans la réponse (« {found.Value.Trim()} »)");
        }
        var words = Word.Matches(stripped.ToLowerInvariant()).Select(m => m.Value).ToList();
        if (words.Count >= 5)
        {
            var french = words.Count(FrenchMarkers.Contains);
            var english = words.Count(EnglishMarkers.Contains);
            if (english > french && english >= 3)
            {
                problems.Add("réponse dans une autre langue que le français");
            }
        }
        return new OutputCheck(problems);
    }
}
