// Entités du domaine de l'assistant documentaire.
// Ce projet ne connaît ni l'IA, ni HTTP, ni le stockage : il décrit ce que
// manipule le métier (documents, utilisateurs, réponses sourcées).

namespace Coeur;

/// <summary>Document interne, avec les groupes autorisés à le lire.</summary>
public sealed record Document(string Id, string Title, string Text, IReadOnlySet<string> AllowedGroups);

/// <summary>Morceau de document indexable. Il hérite des droits de son document.</summary>
public sealed record Chunk(
    string Id,
    string DocumentId,
    string DocumentTitle,
    string Text,
    int Position,
    IReadOnlySet<string> AllowedGroups);

public sealed record User(string Id, IReadOnlySet<string> Groups);

/// <summary>Morceau retrouvé pour une question, avec son score de similarité.</summary>
public sealed record Passage(Chunk Chunk, double Score);

/// <summary>Source citée dans une réponse : le numéro renvoie au passage fourni au modèle.</summary>
public sealed record Source(int Number, string DocumentId, string DocumentTitle, string ChunkId);

public enum AnswerStatus
{
    Answered,
    NoRelevantSource,
    Unsourced,
}

/// <summary>Un passage retrouvé, réduit à ce qu'il faut pour expliquer la recherche.</summary>
public sealed record Retrieved(string ChunkId, double Score);

/// <summary>Tout ce qu'il faut pour expliquer et reproduire une réponse (séquence 4.2).</summary>
public sealed record AnswerTrace(
    string IndexId,
    string EmbeddingModel,
    string? GenerationModel,
    string? PromptVersion,
    IReadOnlyList<Retrieved> Retrieved,
    double MinScore,
    int Attempts,
    IReadOnlyList<string> RawOutputs);

public sealed record Answer(
    string Question,
    AnswerStatus Status,
    string Text,
    IReadOnlyList<Source> Sources,
    AnswerTrace Trace);

public static class Messages
{
    public const string NoRelevantSource =
        "Je n'ai trouvé aucun document accessible qui réponde à cette question.";

    public const string Unsourced =
        "Je n'ai pas pu produire de réponse correctement sourcée. Consultez directement les passages ci-dessous.";
}

public static class StatusNames
{
    /// <summary>Le nom sérialisé d'un statut, identique à celui de la version Python.</summary>
    public static string Of(AnswerStatus status) => status switch
    {
        AnswerStatus.Answered => "answered",
        AnswerStatus.NoRelevantSource => "no_relevant_source",
        AnswerStatus.Unsourced => "unsourced",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
