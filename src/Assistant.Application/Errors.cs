namespace Assistant.Application;

/// <summary>Erreur levée par un cas d'usage.</summary>
public class AssistantApplicationException : Exception
{
    public AssistantApplicationException(string message) : base(message) { }

    public AssistantApplicationException(string message, Exception? inner) : base(message, inner) { }
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
/// L'index a été reconstruit (par un autre processus) entre le contrôle du modèle et la recherche.
/// Fait partie du contrat du port <see cref="IVectorIndex"/> : la recherche la lève au lieu de chercher
/// dans un autre index que celui contrôlé. <see cref="SearchPassages"/> recommence une fois, puis la laisse passer.
/// </summary>
public sealed class IndexReplacedException : AssistantApplicationException
{
    public IndexReplacedException()
        : base("L'index a été reconstruit pendant la recherche. Reposez la question.") { }
}

/// <summary>
/// L'index n'a pas pu être écrit : fait partie du contrat du port <see cref="IVectorIndex"/>.
/// L'index en service reste le précédent, en mémoire comme sur le disque.
/// </summary>
public sealed class IndexWriteException : AssistantApplicationException
{
    public IndexWriteException(string location, string detail, Exception? inner = null)
        : base($"écriture impossible de l'index ({location}) : {detail}", inner) { }
}

/// <summary>
/// Le modèle d'embeddings a changé depuis la construction de l'index. C'est le cœur
/// de la problématique : l'index est une donnée persistée qui dépend du modèle.
/// On ne peut pas « juste changer une ligne de config ».
/// </summary>
public sealed class IndexModelMismatchException : AssistantApplicationException
{
    public string IndexModel { get; }
    public string CurrentModel { get; }

    public IndexModelMismatchException(string indexModel, int indexDimension, string currentModel, int currentDimension)
        : base($"L'index a été construit avec « {indexModel} » ({indexDimension} dim.) mais le modèle d'embeddings "
               + $"actuel est « {currentModel} » ({currentDimension} dim.). Il faut réindexer le corpus.")
    {
        IndexModel = indexModel;
        CurrentModel = currentModel;
    }
}

public sealed class InconsistentEmbeddingsException : AssistantApplicationException
{
    public InconsistentEmbeddingsException(string detail)
        : base($"Embeddings incohérents pendant l'indexation : {detail}") { }
}

/// <summary>
/// Le service IA est injoignable ou a renvoyé une erreur. <c>Transient</c> : vrai quand
/// réessayer a un sens (injoignable, délai dépassé, 5xx) ; faux pour une requête refusée (4xx).
/// </summary>
public sealed class AiServiceException : AssistantApplicationException
{
    public bool Transient { get; }

    public AiServiceException(string message, bool transient = true) : base(message)
    {
        Transient = transient;
    }
}

/// <summary>
/// La sortie du modèle n'a pas la forme d'une réponse (règles de OutputRules). Levée par
/// le décorateur de validation ; le cas d'usage la compte comme une tentative ratée.
/// </summary>
public sealed class ModelOutputRejectedException : AssistantApplicationException
{
    public string Model { get; }
    public string Text { get; }
    public IReadOnlyList<string> Problems { get; }

    public ModelOutputRejectedException(string model, string text, IReadOnlyList<string> problems)
        : base("sortie du modèle rejetée : " + string.Join(" ; ", problems))
    {
        Model = model;
        Text = text;
        Problems = problems;
    }
}

public sealed class InvalidSnapshotNameException : AssistantApplicationException
{
    public InvalidSnapshotNameException(string name)
        : base($"nom d'instantané invalide : « {name} » (lettres, chiffres, . _ - ; 64 caractères au plus)") { }
}

/// <summary>
/// Aucun instantané de ce nom : fait partie du contrat du port <see cref="ISnapshotStore"/>. Les noms connus sans
/// crochets, « aucun » s'il n'y en a pas.
/// </summary>
public sealed class SnapshotNotFoundException : AssistantApplicationException
{
    public SnapshotNotFoundException(string name, IReadOnlyList<string> known)
        : base($"instantané introuvable : {name} (connus : {(known.Count > 0 ? string.Join(", ", known) : "aucun")})") { }
}

/// <summary>
/// Aucun prompt de ce nom : fait partie du contrat du port <see cref="IPromptRepository"/>. Le message nomme le prompt,
/// l'endroit où il a été cherché et les prompts connus (« aucun » s'il n'y en a pas) : un nom mal tapé se corrige d'un
/// coup d'œil.
/// </summary>
public sealed class PromptNotFoundException : AssistantApplicationException
{
    public PromptNotFoundException(string name, string location, IReadOnlyList<string> known)
        : base($"prompt introuvable : {name} dans {location} (connus : {(known.Count > 0 ? string.Join(", ", known) : "aucun")})") { }
}
