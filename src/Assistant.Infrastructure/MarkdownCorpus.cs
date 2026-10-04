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
// `groupes` est obligatoire (« tous » pour un document public) : un droit d'accès oublié ou
// mal écrit est une erreur, jamais un document rendu public en silence.

using System.Text.RegularExpressions;
using Assistant.Application;
using Assistant.Domain;

namespace Assistant.Infrastructure;

public sealed class CorpusFormatException : FormatException
{
    public CorpusFormatException(string message) : base(message) { }
}

public sealed class MarkdownCorpus : IDocumentSource
{
    private static readonly Regex Group = new("^[a-z0-9][a-z0-9_-]*$", RegexOptions.CultureInvariant);
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
        var groups = meta.GetValueOrDefault("groupes", "")
            .Split(',').Select(g => g.Trim()).Where(g => g.Length > 0).ToHashSet();
        if (groups.Count == 0)
        {
            throw new CorpusFormatException($"{origin} : champ 'groupes' obligatoire (« tous » pour un document public)");
        }
        var invalid = groups.Where(g => !Group.IsMatch(g)).Order(StringComparer.Ordinal).ToList();
        if (invalid.Count > 0)
        {
            throw new CorpusFormatException(
                $"{origin} : groupe(s) invalide(s) [{string.Join(", ", invalid)}] : des noms en minuscules séparés par des virgules, sans commentaire");
        }
        return new Document(id, meta.GetValueOrDefault("titre", id),
                            string.Join("\n", lines.Skip(end + 1)).Trim(), groups);
    }

    private static string Read(string path)
    {
        try
        {
            return TextFiles.ReadUtf8(path);
        }
        catch (FormatException error)
        {
            // Un document enregistré en latin-1 n'est pas lu de travers : on dit lequel.
            throw new CorpusFormatException($"{path} : {error.Message}");
        }
    }

    public IReadOnlyList<Document> Load()
    {
        if (!Directory.Exists(_directory))
        {
            throw new CorpusFormatException($"Dossier de corpus introuvable : {_directory}");
        }
        var documents = Directory.GetFiles(_directory, "*.md")
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
            .Select(p => Parse(Read(p), p))
            .ToList();
        var duplicates = documents.GroupBy(d => d.Id).Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(x => x).ToList();
        if (duplicates.Count > 0)
        {
            throw new CorpusFormatException($"Identifiants de documents en double : [{string.Join(", ", duplicates)}]");
        }
        return documents;
    }
}
