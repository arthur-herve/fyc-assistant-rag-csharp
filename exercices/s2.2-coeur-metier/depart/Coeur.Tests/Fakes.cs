// Doubles de test : le cœur métier se teste sans IA, sans réseau, sans disque.


namespace Coeur.Tests;

public static class Fakes
{
    public static readonly string[] Vocabulary = { "télétravail", "jours", "frais", "repas", "salaire", "senior", "congés", "australie" };

    public static Document Doc(string id, string text, params string[] groups) =>
        new(id, char.ToUpperInvariant(id[0]) + id[1..], text, (groups.Length == 0 ? new[] { "tous" } : groups).ToHashSet());

    public static readonly User Alice = new("alice", new HashSet<string> { "tous" });
    public static readonly User Bruno = new("bruno", new HashSet<string> { "tous", "rh" });
}

/// <summary>Un axe par mot du vocabulaire : les scores sont prévisibles à la main.</summary>
public sealed class KeywordEmbedder : IEmbedder
{
    public string Model { get; }
    public List<(string Kind, IReadOnlyList<string> Texts)> Calls { get; } = new();

    public KeywordEmbedder(string model = "fake-keywords") => Model = model;

    private static double[] Vector(string text)
    {
        var lowered = text.ToLowerInvariant();
        return Fakes.Vocabulary.Select(w => lowered.Contains(w) ? 1.0 : 0.0).ToArray();
    }

    public EmbeddingBatch EmbedDocuments(IReadOnlyList<string> texts)
    {
        Calls.Add(("document", texts));
        return new EmbeddingBatch(Model, Fakes.Vocabulary.Length, texts.Select(Vector).ToList());
    }

    public EmbeddingBatch EmbedQuery(string text)
    {
        Calls.Add(("query", new[] { text }));
        return new EmbeddingBatch(Model, Fakes.Vocabulary.Length, new[] { Vector(text) });
    }
}

/// <summary>Renvoie des réponses écrites à l'avance et enregistre les requêtes reçues.</summary>
public sealed class ScriptedGenerator : IGenerator
{
    private readonly string[] _outputs;
    public string Model { get; }
    public List<GenerationRequest> Requests { get; } = new();

    public ScriptedGenerator(params string[] outputs)
    {
        Model = "fake-llm";
        _outputs = outputs;
    }

    private ScriptedGenerator(string model, string[] outputs)
    {
        Model = model;
        _outputs = outputs;
    }

    /// <summary>Un générateur qui s'annonce sous un autre identifiant de modèle.</summary>
    public static ScriptedGenerator WithModel(string model, params string[] outputs) => new(model, outputs);

    public Generation Generate(GenerationRequest request)
    {
        Requests.Add(request);
        return new Generation(Model, _outputs[Math.Min(Requests.Count, _outputs.Length) - 1]);
    }
}

public sealed class FakeIndex : IVectorIndex
{
    private IndexManifest? _manifest;
    public List<Chunk> Chunks { get; } = new();
    public List<double[]> Vectors { get; } = new();

    public IndexManifest? Manifest() => _manifest;

    public void Replace(IndexManifest manifest, IReadOnlyList<Chunk> chunks, IReadOnlyList<double[]> vectors)
    {
        _manifest = manifest;
        Chunks.Clear();
        Chunks.AddRange(chunks);
        Vectors.Clear();
        Vectors.AddRange(vectors);
    }

    public IReadOnlyList<Passage> Search(double[] vector, int topK, Func<Chunk, bool> predicate)
    {
        static double Cosine(double[] a, double[] b)
        {
            double na = Math.Sqrt(a.Sum(x => x * x)), nb = Math.Sqrt(b.Sum(x => x * x));
            return na == 0 || nb == 0 ? 0 : a.Zip(b, (x, y) => x * y).Sum() / (na * nb);
        }
        return Chunks.Zip(Vectors, (c, v) => new Passage(c, Cosine(vector, v)))
            .Where(p => predicate(p.Chunk)).OrderByDescending(p => p.Score).Take(topK).ToList();
    }
}

public sealed class ListSource : IDocumentSource
{
    private readonly IReadOnlyList<Document> _documents;
    public ListSource(params Document[] documents) => _documents = documents;
    public IReadOnlyList<Document> Load() => _documents;
}

public sealed class WholeDocumentSplitter : ITextSplitter
{
    public IReadOnlyList<Chunk> Split(Document d) =>
        d.Text.Length == 0 ? Array.Empty<Chunk>() : new[] { new Chunk($"{d.Id}#0", d.Id, d.Title, d.Text, 0, d.AllowedGroups) };

    public IReadOnlyDictionary<string, object> Describe() => new Dictionary<string, object> { ["type"] = "whole" };
}

public sealed class StaticPrompts : IPromptRepository
{
    public PromptTemplate Get(string name) =>
        new(name, "test-v1", "Cite tes sources.", "Passages :\n{passages}\n\nQuestion : {question}");
}

/// <summary>Toujours la même heure : deux manifestes ou instantanés se comparent octet pour octet.</summary>
public sealed class FixedClock : IClock
{
    public DateTimeOffset Moment { get; } = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    public DateTimeOffset Now() => Moment;
}

public sealed class MemorySnapshotStore : ISnapshotStore
{
    public Dictionary<string, Snapshot> Saved { get; } = new();
    public void Save(Snapshot snapshot) => Saved[snapshot.Name] = snapshot;
    public Snapshot Load(string name) => Saved[name];
    public IReadOnlyList<string> Names() => Saved.Keys.Order().ToList();
}

public static class Build
{
    public static FakeIndex Indexed(IEmbedder? embedder = null, params Document[] documents)
    {
        var index = new FakeIndex();
        var docs = documents.Length > 0 ? documents : new[]
        {
            Fakes.Doc("teletravail", "Deux jours de télétravail par semaine."),
            Fakes.Doc("frais", "Le repas est plafonné à 25 euros, frais remboursés."),
            Fakes.Doc("grille", "Salaire senior : 56 000 à 68 000 euros.", "rh"),
        };
        new IndexCorpus(new ListSource(docs), new WholeDocumentSplitter(), embedder ?? new KeywordEmbedder(), index, new FixedClock()).Execute();
        return index;
    }

    public static AskQuestion Ask(IGenerator generator, IVectorIndex? index = null, IEmbedder? embedder = null,
                                  double minScore = 0.5, int maxAttempts = 2, int? seed = null) =>
        new(embedder ?? new KeywordEmbedder(), index ?? Indexed(), generator, new StaticPrompts(),
            new AskSettings(MinScore: minScore, MaxAttempts: maxAttempts, Seed: seed));
}
