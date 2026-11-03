namespace Assistant.Application;

/// <summary>Erreur levée par un cas d'usage.</summary>
public class AssistantApplicationException : Exception
{
    public AssistantApplicationException(string message) : base(message) { }
}

public sealed class IndexNotBuiltException : AssistantApplicationException
{
    public IndexNotBuiltException()
        : base("Aucun index n'a été construit. Lancez d'abord l'indexation du corpus.") { }
}

public sealed class EmptyCorpusException : AssistantApplicationException
{
    public EmptyCorpusException() : base("Le corpus ne contient aucun texte à indexer.") { }
}

/// <summary>
/// Le modèle d'embeddings a changé depuis la construction de l'index. C'est le cœur
/// de la problématique : l'index est une donnée persistée qui dépend du modèle.
/// On ne peut pas « juste changer une ligne de config ».
/// </summary>
public sealed class IndexModelMismatchException : AssistantApplicationException
{
    public IndexModelMismatchException(string indexModel, int indexDimension, string currentModel, int currentDimension)
        : base($"L'index a été construit avec « {indexModel} » ({indexDimension} dim.) mais le modèle d'embeddings "
               + $"actuel est « {currentModel} » ({currentDimension} dim.). Il faut réindexer le corpus.") { }
}

public sealed class InconsistentEmbeddingsException : AssistantApplicationException
{
    public InconsistentEmbeddingsException(string detail)
        : base($"Embeddings incohérents pendant l'indexation : {detail}") { }
}

/// <summary>
/// Le service IA est injoignable ou a renvoyé une erreur. <c>Transient</c> dit si réessayer a un sens :
/// oui quand le service est injoignable, trop lent ou en erreur 5xx, non quand il refuse la requête (4xx).
/// </summary>
public sealed class AiServiceException : AssistantApplicationException
{
    public bool Transient { get; }

    public AiServiceException(string message, bool transient = true) : base(message)
    {
        Transient = transient;
    }
}

public sealed class InvalidSnapshotNameException : AssistantApplicationException
{
    public InvalidSnapshotNameException(string name)
        : base($"nom d'instantané invalide : « {name} » (lettres, chiffres, . _ - ; 64 caractères au plus)") { }
}

/// <summary>
/// Aucun instantané ne porte ce nom. Le port <see cref="ISnapshotStore"/> prévoit cette exception. Le message
/// liste les noms connus, séparés par des virgules, ou dit « aucun » s'il n'y en a pas.
/// </summary>
public sealed class SnapshotNotFoundException : AssistantApplicationException
{
    public SnapshotNotFoundException(string name, IReadOnlyList<string> known)
        : base($"instantané introuvable : {name} (connus : {(known.Count > 0 ? string.Join(", ", known) : "aucun")})") { }
}

/// <summary>
/// Aucun prompt ne porte ce nom. Le port <see cref="IPromptRepository"/> prévoit cette exception. Le message donne le
/// nom cherché, l'endroit où on l'a cherché et les prompts connus, pour qu'une faute de frappe se corrige d'un coup
/// d'œil.
/// </summary>
public sealed class PromptNotFoundException : AssistantApplicationException
{
    public PromptNotFoundException(string name, string location, IReadOnlyList<string> known)
        : base($"prompt introuvable : {name} dans {location} (connus : {(known.Count > 0 ? string.Join(", ", known) : "aucun")})") { }
}
