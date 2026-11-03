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

public sealed class InconsistentEmbeddingsException : AssistantApplicationException
{
    public InconsistentEmbeddingsException(string detail)
        : base($"Embeddings incohérents pendant l'indexation : {detail}") { }
}
