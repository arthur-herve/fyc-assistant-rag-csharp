// Le métier de ce jouet : répondre à une question à partir de passages.
// Il ne dépend que du port de Ports.cs, sans aucun nom de modèle ni aucune URL.

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
