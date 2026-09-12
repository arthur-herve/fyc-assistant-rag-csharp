// Adaptateurs de fichiers : prompts versionnés, instantanés JSON, horloge système.

using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Application;

namespace Assistant.Infrastructure;

/// <summary>
/// Prompts stockés dans des fichiers JSON versionnés avec le code
/// (<c>prompts/&lt;nom&gt;.json</c> : version, system, user). La version tracée combine la
/// version déclarée et une empreinte du contenu : modifier un prompt sans changer
/// sa version reste détectable dans les traces.
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
        // Fins de ligne normalisées : l'empreinte est la même sous Windows et Linux.
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var data = JsonNode.Parse(text)!.AsObject();
        var fingerprint = Fingerprints.Sha256Hex(text)[..8];
        return new PromptTemplate(
            name,
            $"{data["version"]!.GetValue<string>()}+{fingerprint}",
            data["system"]!.GetValue<string>().Trim(),
            data["user"]!.GetValue<string>().Trim());
    }
}

public sealed class SnapshotNotFoundException : AssistantApplicationException
{
    public SnapshotNotFoundException(string name, IReadOnlyList<string> known)
        : base($"instantané introuvable : {name} (connus : [{string.Join(", ", known)}])") { }
}

/// <summary>Instantanés conservés en fichiers JSON lisibles, un par nom. Même format que la version Python.</summary>
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
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var configuration = root["configuration"]?.AsObject()
            .ToDictionary(kv => kv.Key, kv => JsonValues.ToObjectOrNull(kv.Value), StringComparer.Ordinal)
            ?? new Dictionary<string, object?>();
        // Les champs inconnus (instantané écrit par une version plus récente) sont ignorés.
        var entries = root["entries"]!.AsArray().Select(e => new SnapshotEntry(
            e!["question_id"]!.GetValue<string>(), e["user_id"]!.GetValue<string>(), e["question"]!.GetValue<string>(),
            e["status"]!.GetValue<string>(),
            e["cited_documents"]!.AsArray().Select(d => d!.GetValue<string>()).ToList(),
            e["text"]!.GetValue<string>(), e["attempts"]?.GetValue<int>() ?? 0)).ToList();
        return new Snapshot(root["name"]!.GetValue<string>(), root["created_at"]!.GetValue<string>(), configuration, entries);
    }

    public IReadOnlyList<string> Names() =>
        !Directory.Exists(_directory)
            ? Array.Empty<string>()
            : Directory.GetFiles(_directory, "*.json").Select(p => Path.GetFileNameWithoutExtension(p))
                       .OrderBy(n => n, StringComparer.Ordinal).ToList();
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now() => DateTimeOffset.UtcNow;
}
