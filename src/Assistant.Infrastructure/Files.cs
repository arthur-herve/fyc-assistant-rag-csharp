// Adaptateurs de fichiers : les prompts, les instantanés en JSON, et l'horloge système.
// La lecture stricte de l'UTF-8 (TextFiles) et du JSON (JsonText) vient de src/Shared. Le corpus,
// l'index, le client du service IA et la ligne de commande s'en servent aussi.

using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Application;

namespace Assistant.Infrastructure;

/// <summary>
/// Prompts rangés dans des fichiers JSON versionnés avec le code, <c>prompts/&lt;nom&gt;.json</c>, qui
/// contiennent deux textes : system et user.
/// </summary>
public sealed class FilePromptRepository : IPromptRepository
{
    private readonly string _directory;

    public FilePromptRepository(string directory)
    {
        _directory = directory;
    }

    public PromptTemplate Get(string name)
    {
        var path = Path.Combine(_directory, name + ".json");
        try
        {
            var data = JsonText.Parse(TextFiles.ReadUtf8(path)) as JsonObject;
            if (JsonFiles.Text(data, "system") is not { } system || JsonFiles.Text(data, "user") is not { } user)
            {
                // Les prompts s'éditent à la main : le message doit dire quoi corriger, et dans quel fichier.
                throw new FormatException("il faut deux textes, system et user");
            }
            return new PromptTemplate(name, system.Trim(), user.Trim());
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            // Fichier ou dossier absent : on donne le nom cherché, le dossier et les prompts disponibles, ce qui
            // aide plus que « Could not find file … ».
            throw new PromptNotFoundException(name, _directory, Names());
        }
        catch (Exception error) when (JsonFiles.IsMalformed(error))
        {
            // Le message nomme le fichier, puis ce qui ne va pas : la syntaxe, l'encodage ou un champ manquant.
            throw new FormatException($"prompt illisible ({path}) : {error.Message}", error);
        }
    }

    /// <summary>
    /// Les noms des prompts du dossier, triés comme ceux des instantanés (<see cref="JsonSnapshotStore.Names"/>).
    /// </summary>
    private IReadOnlyList<string> Names() =>
        !Directory.Exists(_directory)
            ? Array.Empty<string>()
            : Directory.GetFiles(_directory, "*.json").Select(p => Path.GetFileNameWithoutExtension(p))
                       .OrderBy(n => n, StringComparer.Ordinal).ToList();
}

/// <summary>Instantanés conservés en fichiers JSON lisibles, un par nom.</summary>
public sealed class JsonSnapshotStore : ISnapshotStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _directory;

    public JsonSnapshotStore(string directory)
    {
        _directory = directory;
    }

    private string PathFor(string name)
    {
        if (!RecordSnapshot.ValidName.IsMatch(name))
        {
            throw new InvalidSnapshotNameException(name);
        }
        return Path.Combine(_directory, name + ".json");
    }

    public void Save(Snapshot snapshot)
    {
        var path = PathFor(snapshot.Name);
        Directory.CreateDirectory(_directory);
        var payload = new JsonObject
        {
            ["name"] = snapshot.Name,
            ["created_at"] = snapshot.CreatedAt,
            ["configuration"] = JsonValues.FromDictionary(snapshot.Configuration),
            ["entries"] = new JsonArray(snapshot.Entries.Select(e => (JsonNode)new JsonObject
            {
                ["question_id"] = e.QuestionId,
                ["user_id"] = e.UserId,
                ["question"] = e.Question,
                ["status"] = e.Status,
                ["cited_documents"] = new JsonArray(e.CitedDocuments.Select(d => (JsonNode)d).ToArray()),
                ["text"] = e.Text,
            }).ToArray()),
        };
        File.WriteAllText(path, payload.ToJsonString(Options));
    }

    public Snapshot Load(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
        {
            throw new SnapshotNotFoundException(name, Names());
        }
        try
        {
            // JSON strict, chaînes comprises. Les types sont ensuite vérifiés champ par champ, pour que le
            // message nomme le champ en faute.
            var root = JsonText.Parse(TextFiles.ReadUtf8(path), strings: true) as JsonObject
                       ?? throw new FormatException("un objet JSON est attendu");
            var configuration = !root.ContainsKey("configuration") ? new Dictionary<string, object?>()
                : root["configuration"] is JsonObject values
                    ? values.ToDictionary(kv => kv.Key, kv => JsonValues.ToObjectOrNull(kv.Value), StringComparer.Ordinal)
                    : throw new FormatException("« configuration » doit être un objet");
            var entries = root["entries"] as JsonArray ?? throw new FormatException("champ « entries » manquant ou qui n'est pas une liste");
            return new Snapshot(Text(root, "name"), Text(root, "created_at"), configuration, entries.Select(Entry).ToList());
        }
        catch (Exception error) when (JsonFiles.IsMalformed(error))
        {
            throw new FormatException($"instantané illisible ({path}) : {error.Message}", error);
        }
    }

    /// <summary>
    /// Relit une réponse en vérifiant les types : « abc » n'est pas une liste de documents. Les champs inconnus
    /// sont ignorés, ce qui permet de lire un instantané écrit par une version plus récente.
    /// </summary>
    private static SnapshotEntry Entry(JsonNode? node)
    {
        var values = node as JsonObject ?? throw new FormatException("chaque réponse doit être un objet JSON");
        var cited = values["cited_documents"] is JsonArray documents && documents.All(JsonFiles.IsText)
            ? documents.Select(d => d!.GetValue<string>()).ToList()
            : throw new FormatException("« cited_documents » doit être une liste de textes");
        return new SnapshotEntry(Text(values, "question_id"), Text(values, "user_id"), Text(values, "question"),
                                 Text(values, "status"), cited, Text(values, "text"));
    }

    private static string Text(JsonObject values, string key) =>
        JsonFiles.Text(values, key) ?? throw new FormatException($"champ « {key} » manquant ou non textuel");

    public IReadOnlyList<string> Names() =>
        !Directory.Exists(_directory)
            ? Array.Empty<string>()
            : Directory.GetFiles(_directory, "*.json").Select(p => Path.GetFileNameWithoutExtension(p))
                       .OrderBy(n => n, StringComparer.Ordinal).ToList();
}

/// <summary>
/// Ce que lève la lecture d'un fichier JSON bien formé mais incomplet ou mal typé : une NullReferenceException
/// pour un champ absent, une InvalidOperationException ou une FormatException pour un type inattendu.
/// </summary>
internal static class JsonFiles
{
    public static bool IsMalformed(Exception error) =>
        error is JsonException or InvalidOperationException or NullReferenceException or FormatException
              or ArgumentException;

    /// <summary>Ici, une NullReferenceException signale un champ obligatoire absent. Son message n'aide pas.</summary>
    public static string Describe(Exception error) =>
        error is NullReferenceException ? "champ obligatoire manquant" : error.Message;

    public static bool IsText(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out _);

    /// <summary>Le champ texte <paramref name="key"/>, ou null s'il manque ou n'est pas un texte.</summary>
    public static string? Text(JsonObject? values, string key) =>
        values?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now() => DateTimeOffset.UtcNow;
}
