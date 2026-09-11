// Adaptateur : découpage des documents en morceaux. Les paramètres de découpage
// changent les réponses (principe CACE, séquence 3.2) : ils sont donc enregistrés
// dans le manifeste de l'index. Même algorithme que la version Python.

using System.Text.RegularExpressions;
using Assistant.Application;
using Assistant.Domain;

namespace Assistant.Infrastructure;

public sealed class ParagraphSplitter : ITextSplitter
{
    private static readonly Regex BlankLine = new(@"\n\s*\n", RegexOptions.Compiled);

    public int MaxChars { get; }
    public int OverlapChars { get; }
    public bool IncludeTitle { get; }

    public ParagraphSplitter(int maxChars = 800, int overlapChars = 120, bool includeTitle = true)
    {
        if (maxChars < 100)
        {
            throw new ArgumentException("max_chars doit valoir au moins 100", nameof(maxChars));
        }
        if (overlapChars < 0 || overlapChars >= maxChars / 2)
        {
            throw new ArgumentException("overlap_chars doit être compris entre 0 et max_chars / 2", nameof(overlapChars));
        }
        MaxChars = maxChars;
        OverlapChars = overlapChars;
        IncludeTitle = includeTitle;
    }

    public IReadOnlyDictionary<string, object> Describe() => new Dictionary<string, object>
    {
        ["type"] = "paragraph",
        ["max_chars"] = MaxChars,
        ["overlap_chars"] = OverlapChars,
        ["include_title"] = IncludeTitle,
    };

    public IReadOnlyList<Chunk> Split(Document document)
    {
        var chunks = new List<Chunk>();
        var current = "";
        foreach (var piece in Pieces(document.Text))
        {
            if (current.Length > 0 && current.Length + 2 + piece.Length > MaxChars)
            {
                chunks.Add(MakeChunk(document, current, chunks.Count));
                current = Tail(current);
            }
            current = current.Length > 0 ? current + "\n\n" + piece : piece;
        }
        if (current.Trim().Length > 0)
        {
            chunks.Add(MakeChunk(document, current, chunks.Count));
        }
        return chunks;
    }

    /// <summary>Paragraphes, eux-mêmes redécoupés s'ils dépassent la taille maximale.</summary>
    private IEnumerable<string> Pieces(string text)
    {
        var budget = MaxChars - OverlapChars - 2;
        foreach (var raw in BlankLine.Split(text.Replace("\r\n", "\n")))
        {
            var paragraph = raw.Trim();
            if (paragraph.Length == 0)
            {
                continue;
            }
            while (paragraph.Length > budget)
            {
                var cut = paragraph.LastIndexOf(' ', budget - 1, budget);
                cut = cut > budget / 2 ? cut : budget;
                yield return paragraph[..cut].Trim();
                paragraph = paragraph[cut..].Trim();
            }
            if (paragraph.Length > 0)
            {
                yield return paragraph;
            }
        }
    }

    private string Tail(string text)
    {
        if (OverlapChars == 0)
        {
            return "";
        }
        var tail = text.Length <= OverlapChars ? text : text[^OverlapChars..];
        var space = tail.IndexOf(' ');
        return space >= 0 && space < tail.Length - 1 ? tail[(space + 1)..] : tail;
    }

    private Chunk MakeChunk(Document document, string text, int position)
    {
        var body = text.Trim();
        if (IncludeTitle)
        {
            body = document.Title + "\n" + body;
        }
        return new Chunk($"{document.Id}#{position}", document.Id, document.Title, body, position, document.AllowedGroups);
    }
}
