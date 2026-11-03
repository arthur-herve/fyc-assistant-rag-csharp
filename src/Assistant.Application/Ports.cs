// Ports : ce dont les cas d'usage ont besoin, sans dire comment c'est fait.
// Les adaptateurs de l'infrastructure implémentent ces interfaces, et les cas
// d'usage n'en connaissent que les signatures.

using System.Text.RegularExpressions;
using Assistant.Domain;

namespace Assistant.Application;

// --- Embeddings ------------------------------------------------------------

public interface IEmbedder
{
    IReadOnlyList<double[]> EmbedDocuments(IReadOnlyList<string> texts);
    double[] EmbedQuery(string text);
}

// --- Génération ------------------------------------------------------------

public sealed record GenerationRequest(string System, string Prompt, double Temperature, int MaxTokens);

public interface IGenerator
{
    string Generate(GenerationRequest request);
}

// --- Index vectoriel -------------------------------------------------------

public interface IVectorIndex
{
    bool IsBuilt();
    void Replace(IReadOnlyList<Chunk> chunks, IReadOnlyList<double[]> vectors);
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

/// <summary>Prompt nommé : le texte système, et le message où l'on place les passages et la question.</summary>
public sealed record PromptTemplate(string Name, string System, string User)
{
    /// <summary>Remplacement en une seule passe : un « {question} » écrit dans un passage reste tel quel.</summary>
    public string Render(string question, string passages) =>
        Regex.Replace(User, @"\{(passages|question)\}", m => m.Groups[1].Value == "passages" ? passages : question);
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
    string Text);

/// <summary>
/// Comportement du système figé à un instant, avec la configuration qui l'a produit :
/// comparer deux instantanés sans comparer leurs configurations n'aurait aucun sens.
/// </summary>
public sealed record Snapshot(
    string Name,
    string CreatedAt,
    IReadOnlyDictionary<string, object?> Configuration,
    IReadOnlyList<SnapshotEntry> Entries);

public interface ISnapshotStore
{
    void Save(Snapshot snapshot);
    Snapshot Load(string name);
    IReadOnlyList<string> Names();
}
