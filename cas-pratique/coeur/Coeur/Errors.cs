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
    public IndexModelMismatchException(string indexModel, int indexDimension, string currentModel, int currentDimension)
        : base($"L'index a été construit avec « {indexModel} » ({indexDimension} dim.) mais le modèle d'embeddings "
               + $"actuel est « {currentModel} » ({currentDimension} dim.). Il faut réindexer le corpus.") { }
}

public sealed class InconsistentEmbeddingsException : AssistantApplicationException
{
    public InconsistentEmbeddingsException(string detail)
        : base($"Embeddings incohérents pendant l'indexation : {detail}") { }
}
