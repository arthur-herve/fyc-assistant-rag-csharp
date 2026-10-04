// Adaptateurs de fichiers : prompts versionnés, instantanés JSON, horloge système. Lecture UTF-8 stricte
// (TextFiles), JSON lu strictement (JsonText) : src/Shared, partagés avec le corpus, l'index, le client du
// service IA et la ligne de commande.

using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Application;

namespace Assistant.Infrastructure;

/// <summary>
/// Prompts stockés dans des fichiers JSON versionnés avec le code
/// (<c>prompts/&lt;nom&gt;.json</c> : version, system, user). La version tracée combine la
/// version déclarée et une empreinte du contenu : modifier un prompt sans changer
/// sa version reste détectable dans les traces.
/// L'empreinte porte sur le contenu canonique (version, system, user), pas sur le
/// fichier : un commentaire (clé « _… ») ou une autre mise en forme ne la change pas.
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
            if (JsonFiles.Text(data, "version") is not { } version || JsonFiles.Text(data, "system") is not { } system
                || JsonFiles.Text(data, "user") is not { } user)
            {
                // Les prompts sont édités à la main (docs/artefacts.md) : dire quoi corriger, et où.
                throw new FormatException("il faut trois textes, version, system et user");
            }
            (system, user) = (system.Trim(), user.Trim());
            return new PromptTemplate(name, $"{version}+{Fingerprint(version, system, user)}", system, user);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            // Fichier ou dossier absent : le nom, le dossier et les prompts connus, plutôt que « Could not find file … ».
            throw new PromptNotFoundException(name, _directory, Names());
        }
        catch (Exception error) when (JsonFiles.IsMalformed(error))
        {
            // Le fichier, puis ce qui ne va pas (syntaxe, encodage, champ manquant).
            throw new FormatException($"prompt illisible ({path}) : {error.Message}", error);
        }
    }

    /// <summary>Les noms des prompts du dossier, triés comme ceux des instantanés (<see cref="JsonSnapshotStore.Names"/>).</summary>
    private IReadOnlyList<string> Names() =>
        !Directory.Exists(_directory)
            ? Array.Empty<string>()
            : Directory.GetFiles(_directory, "*.json").Select(p => Path.GetFileNameWithoutExtension(p))
                       .OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Empreinte canonique d'un prompt : SHA-256 de « version, system, user » séparés par
    /// des sauts de ligne (fins de ligne normalisées), 8 premiers caractères hexadécimaux.
    /// </summary>
    public static string Fingerprint(string version, string system, string user) =>
        Fingerprints.Sha256Hex($"{version}\n{system}\n{user}".Replace("\r\n", "\n"))[..8];
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
                ["attempts"] = e.Attempts,
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
            // JSON strict, chaînes comprises ; puis types vérifiés champ par champ : le message nomme le champ en faute.
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
    /// Une réponse relue, types vérifiés : « abc » n'est pas une liste de documents, null n'est pas un nombre
    /// de tentatives. Les champs inconnus (instantané écrit par une version plus récente) sont ignorés.
    /// </summary>
    private static SnapshotEntry Entry(JsonNode? node)
    {
        var values = node as JsonObject ?? throw new FormatException("chaque réponse doit être un objet JSON");
        var cited = values["cited_documents"] is JsonArray documents && documents.All(JsonFiles.IsText)
            ? documents.Select(d => d!.GetValue<string>()).ToList()
            : throw new FormatException("« cited_documents » doit être une liste de textes");
        var attempts = !values.ContainsKey("attempts") ? 0
            : values["attempts"] is JsonValue value && value.TryGetValue<int>(out var count) ? count
            : throw new FormatException("« attempts » doit être un entier");
        return new SnapshotEntry(Text(values, "question_id"), Text(values, "user_id"), Text(values, "question"),
                                 Text(values, "status"), cited, Text(values, "text"), attempts);
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
/// Ce que lève la lecture d'un fichier JSON bien formé mais incomplet ou mal typé : champ absent
/// (NullReferenceException), autre type que prévu (InvalidOperationException, FormatException).
/// </summary>
internal static class JsonFiles
{
    public static bool IsMalformed(Exception error) =>
        error is JsonException or InvalidOperationException or NullReferenceException or FormatException
              or ArgumentException;

    /// <summary>Le message d'une NullReferenceException ne dit rien d'utile : c'est un champ obligatoire absent.</summary>
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
