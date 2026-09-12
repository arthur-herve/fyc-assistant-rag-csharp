namespace Coeur;

// --- Erreurs du domaine ---

public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
}

public sealed class EmptyQuestionException : DomainException
{
    public EmptyQuestionException() : base("La question est vide.") { }
}

// --- Erreurs des cas d'usage ---

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
