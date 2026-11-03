// Les trois règles métier : le contrôle d'accès, les citations obligatoires et la forme d'une
// réponse acceptable. Ce sont des fonctions pures, testables en microsecondes.

using System.Globalization;
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
/// document : un passage interdit n'entre tout simplement pas dans le prompt (ADR 0006).
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

public sealed record OutputCheck(IReadOnlyList<string> Problems)
{
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// Règle métier n° 3 : à quoi doit ressembler une réponse avant d'être montrée.
/// Les citations ne jugent que la forme. Cette règle juge le fond : une réponse vide, trop
/// longue, dans la mauvaise langue ou qui déverse un raisonnement n'est pas une réponse,
/// même si elle contient « [1] ». Aucun modèle n'est nommé ici, mais les marqueurs de
/// raisonnement ont été relevés sur les modèles qu'on a rencontrés, qwen3 en tête. Cette
/// connaissance du modèle est assumée et testée (ADR 0008), mais elle vieillira : il faut la
/// revoir quand on change de modèle. Ce qui peut se régler ou se retirer mécaniquement est
/// traité dans le service IA, qui retire les balises &lt;think&gt; quel que soit le moteur et
/// règle le budget de réflexion d'Ollama.
/// </summary>
public static class OutputRules
{
    private static readonly HashSet<string> FrenchMarkers =
        "le la les des une un du de et est pas pour vous votre dans par sur au aux".Split(' ').ToHashSet();

    private static readonly HashSet<string> EnglishMarkers =
        "the is are and user let's okay this that with for answer should".Split(' ').ToHashSet();

    // Tournures génériques d'un raisonnement déversé. Les \b évitent de les trouver au milieu d'un autre mot.
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
        // On compte en points de code Unicode et non en unités UTF-16 comme string.Length : un emoji compte pour
        // un caractère, pas pour deux. Une demi-paire de substitution isolée compte aussi pour un.
        var length = stripped.EnumerateRunes().Count();
        if (length > maxChars)
        {
            problems.Add($"réponse trop longue ({length} caractères, {maxChars} au plus)");
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
