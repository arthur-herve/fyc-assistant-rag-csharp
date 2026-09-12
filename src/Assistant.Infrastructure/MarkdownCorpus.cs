// Adaptateur : lit un dossier de fichiers Markdown avec un en-tête simple.
//
//     ---
//     id: teletravail
//     titre: Politique de télétravail
//     groupes: tous
//     ---
//     Texte du document…
//
// Même format que la version Python : les deux applications partagent les corpus.

using Assistant.Application;
using Assistant.Domain;

namespace Assistant.Infrastructure;

public sealed class CorpusFormatException : FormatException
{
    public CorpusFormatException(string message) : base(message) { }
}

public sealed class MarkdownCorpus : IDocumentSource
{
    private readonly string _directory;

    public MarkdownCorpus(string directory)
    {
        _directory = directory;
    }

    public static Document Parse(string content, string origin = "<texte>")
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            throw new CorpusFormatException($"{origin} : en-tête '---' manquant");
        }
        var end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
        if (end < 0)
        {
            throw new CorpusFormatException($"{origin} : en-tête non refermé");
        }
        var meta = new Dictionary<string, string>();
        foreach (var line in lines.Skip(1).Take(end - 1))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                throw new CorpusFormatException($"{origin} : ligne d'en-tête invalide « {line} »");
            }
            meta[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }
        if (!meta.TryGetValue("id", out var id) || id.Length == 0)
        {
            throw new CorpusFormatException($"{origin} : champ 'id' obligatoire");
        }
        var groups = (meta.GetValueOrDefault("groupes", AccessPolicy.PublicGroup))
            .Split(',').Select(g => g.Trim()).Where(g => g.Length > 0).ToHashSet();
        if (groups.Count == 0)
        {
            groups.Add(AccessPolicy.PublicGroup);
        }
        return new Document(id, meta.GetValueOrDefault("titre", id),
                            string.Join("\n", lines.Skip(end + 1)).Trim(), groups);
    }

    public IReadOnlyList<Document> Load()
    {
        if (!Directory.Exists(_directory))
        {
            throw new CorpusFormatException($"Dossier de corpus introuvable : {_directory}");
        }
        var documents = Directory.GetFiles(_directory, "*.md")
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
            .Select(p => Parse(File.ReadAllText(p), p))
            .ToList();
        var duplicates = documents.GroupBy(d => d.Id).Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(x => x).ToList();
        if (duplicates.Count > 0)
        {
            throw new CorpusFormatException($"Identifiants de documents en double : [{string.Join(", ", duplicates)}]");
        }
        return documents;
    }
}
