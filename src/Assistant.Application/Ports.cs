// Ports : ce dont les cas d'usage ont besoin, sans dire comment c'est fait.
// Les adaptateurs de l'infrastructure implémentent ces interfaces ;
// les cas d'usage ne connaissent que ces signatures.

using Assistant.Domain;

namespace Assistant.Application;

// --- Embeddings ------------------------------------------------------------

/// <summary>
/// Vecteurs produits par un modèle. <c>Model</c> est l'identifiant du modèle
/// *réellement* utilisé, tel que renvoyé par le service IA, et non l'alias demandé.
/// </summary>
public sealed record EmbeddingBatch(string Model, int Dimension, IReadOnlyList<double[]> Vectors);

public interface IEmbedder
{
    EmbeddingBatch EmbedDocuments(IReadOnlyList<string> texts);
    EmbeddingBatch EmbedQuery(string text);
}

// --- Génération ------------------------------------------------------------

public sealed record GenerationRequest(string System, string Prompt, double Temperature, int MaxTokens, int? Seed = null);

public sealed record Generation(string Model, string Text);

public interface IGenerator
{
    Generation Generate(GenerationRequest request);
}

// --- Index vectoriel -------------------------------------------------------

/// <summary>Carte d'identité d'un index : sans elle, un index est inexplicable.</summary>
public sealed record IndexManifest(
    string IndexId,
    string EmbeddingModel,
    int Dimension,
    string CorpusFingerprint,
    IReadOnlyDictionary<string, object> Splitter,
    int DocumentCount,
    int ChunkCount,
    string CreatedAt);

public interface IVectorIndex
{
    IndexManifest? Manifest();
    void Replace(IndexManifest manifest, IReadOnlyList<Chunk> chunks, IReadOnlyList<double[]> vectors);
    IReadOnlyList<Passage> Search(double[] vector, int topK, Func<Chunk, bool> predicate);
}

// --- Corpus et découpage ---------------------------------------------------

public interface IDocumentSource
{
    IReadOnlyList<Document> Load();
}

public interface ITextSplitter
{
    IReadOnlyList<Chunk> Split(Document document);
    IReadOnlyDictionary<string, object> Describe();
}

// --- Prompts ---------------------------------------------------------------

/// <summary>Prompt versionné. Sa version fait partie de la trace de chaque réponse.</summary>
public sealed record PromptTemplate(string Name, string Version, string System, string User)
{
    public string Render(string question, string passages) =>
        User.Replace("{passages}", passages).Replace("{question}", question);
}

public interface IPromptRepository
{
    PromptTemplate Get(string name);
}

// --- Horloge ---------------------------------------------------------------

/// <summary>Le plus petit port du projet : l'heure est une entrée-sortie déguisée.</summary>
public interface IClock
{
    DateTimeOffset Now();
}

// --- Instantanés -----------------------------------------------------------

/// <summary>Ce qu'a répondu l'assistant à une question, réduit à ce qui se compare.</summary>
public sealed record SnapshotEntry(
    string QuestionId,
    string UserId,
    string Question,
    string Status,
    IReadOnlyList<string> CitedDocuments,
    string Text,
    int Attempts = 0);

/// <summary>
/// Comportement du système figé à un instant, avec la configuration qui l'a produit :
/// comparer deux instantanés sans comparer leurs configurations n'aurait aucun sens.
/// </summary>
public sealed record Snapshot(
    string Name,
    string CreatedAt,
    IReadOnlyDictionary<string, string> Configuration,
    IReadOnlyList<SnapshotEntry> Entries);

public interface ISnapshotStore
{
    void Save(Snapshot snapshot);
    Snapshot Load(string name);
    IReadOnlyList<string> Names();
}
