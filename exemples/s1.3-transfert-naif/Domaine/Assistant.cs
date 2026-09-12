// Le « métier » de ce jouet : répondre à partir de passages, et retrouver les passages.
// Il ne connaît que les deux ports ci-contre. Aucun nom de modèle, aucune URL.

namespace TransfertNaif.Domaine;

public sealed record Passage(string Title, string Text);

public sealed class Assistant
{
    private readonly ITextGenerator _generator;

    public Assistant(ITextGenerator generator)
    {
        _generator = generator;
    }

    public string Answer(string question, IReadOnlyList<Passage> passages)
    {
        var context = string.Join("\n\n", passages.Select((p, i) => $"[{i + 1}] {p.Title}\n{p.Text}"));
        return _generator.Generate(
            "Réponds en français à partir des passages fournis, en citant leur numéro entre crochets.",
            $"Passages :\n\n{context}\n\nQuestion : {question}");
    }
}

/// <summary>
/// Un index vectoriel en mémoire, écrit avec la même naïveté : il stocke des vecteurs
/// et ne se souvient pas de qui les a produits.
/// </summary>
public sealed class NaiveIndex
{
    private readonly List<(Passage Passage, double[] Vector)> _entries = new();

    public int Count => _entries.Count;

    public void Add(Passage passage, double[] vector) => _entries.Add((passage, Normalize(vector)));

    public IEnumerable<(Passage Passage, double[] Vector)> Entries => _entries;

    public IReadOnlyList<(Passage Passage, double Score)> Search(double[] query, int topK)
    {
        var q = Normalize(query);
        return _entries
            .Select(e => (e.Passage, Score: Cosine(q, e.Vector)))
            .OrderByDescending(e => e.Score)
            .Take(topK)
            .ToList();
    }

    private static double Cosine(double[] a, double[] b)
    {
        if (a.Length != b.Length)
        {
            throw new InvalidOperationException($"vecteurs de dimensions différentes : {a.Length} et {b.Length}");
        }
        var dot = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }
        return dot;
    }

    private static double[] Normalize(double[] v)
    {
        var norm = Math.Sqrt(v.Sum(x => x * x));
        return norm == 0 ? v : v.Select(x => x / norm).ToArray();
    }
}
