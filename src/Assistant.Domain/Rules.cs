// Les deux règles métier : le contrôle d'accès et les citations obligatoires. Ce sont des
// fonctions pures, testables en microsecondes.

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
///
/// La règle à écrire : un morceau dont les groupes contiennent <see cref="PublicGroup"/>
/// se lit par tout le monde. Pour les autres, l'utilisateur doit avoir au moins un groupe
/// en commun avec le morceau.
/// </summary>
public sealed class AccessPolicy
{
    public const string PublicGroup = "tous";

    public bool CanRead(User user, Chunk chunk)
    {
        throw new NotImplementedException("AccessPolicy.CanRead : à écrire (exercice S2.2)");
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
///
/// La fonction à écrire rend d'un côté les numéros valides, de 1 à <c>passageCount</c>,
/// de l'autre les numéros invalides, chacun une seule fois et dans l'ordre où il apparaît.
/// Une sortie inattendue ne doit jamais faire lever d'exception : un nombre trop grand
/// pour un int est une citation invalide. Seuls les chiffres de 0 à 9 comptent : « [١] », écrit dans
/// une autre écriture, n'est pas une citation.
/// </summary>
public static class Citations
{
    public static CitationCheck Check(string text, int passageCount)
    {
        throw new NotImplementedException("Citations.Check : à écrire (exercice S2.2)");
    }
}
