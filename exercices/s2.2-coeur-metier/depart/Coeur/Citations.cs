// Règle métier n° 2 : toute réponse cite ses sources.                        — À ÉCRIRE

namespace Coeur;

/// <summary>Résultat de la vérification des citations d'une réponse.</summary>
public sealed record CitationCheck(IReadOnlyList<int> Cited, IReadOnlyList<int> Invalid)
{
    public bool IsValid => Cited.Count > 0 && Invalid.Count == 0;
}

/// <summary>
/// Le modèle est probabiliste, la vérification ne l'est pas. On contrôle que la réponse
/// contient au moins une citation et que chaque citation renvoie à un passage réellement
/// fourni (numéros de 1 à <c>passageCount</c>). Formats acceptés : [1], [2, 3], [2,3].
/// Renvoyer les numéros valides sans doublon (dans l'ordre d'apparition) et les numéros
/// invalides. Un nombre trop grand pour un int est une citation invalide, pas un plantage.
/// Tests : Coeur.Tests/DomainTests.cs (CitationsTests).
/// </summary>
public static class Citations
{
    public static CitationCheck Check(string text, int passageCount)
    {
        throw new NotImplementedException("Citations.Check : à écrire (exercice S2.2)");
    }
}
