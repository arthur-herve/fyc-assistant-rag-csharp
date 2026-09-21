// Configuration de l'application, lue depuis config/app.json (System.Text.Json, rien d'autre).
// Elle ne connaît les modèles que par leurs alias ; seuls les seuils de pertinence en dépendent,
// et c'est voulu (ADR 0004). Toute erreur de saisie se voit au chargement, avec le fichier et la clé.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Domain;

namespace Assistant.Cli;

public sealed class UnknownUserException : Exception
{
    public UnknownUserException(string name, IEnumerable<string> known)
        : base($"utilisateur inconnu : {name} (connus : [{string.Join(", ", known)}])") { }
}

/// <summary>Configuration absente, incomplète ou hors bornes : le message nomme le fichier et la clé.</summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string message) : base(message) { }
}

public sealed record AppConfig(
    string AiBaseUrl,
    string EmbeddingModel,
    string GenerationModel,
    TimeSpan Timeout,
    string CorpusDir,
    string IndexPath,
    string SnapshotsDir,
    int SplitterMaxChars,
    int SplitterOverlapChars,
    bool SplitterIncludeTitle,
    int TopK,
    IReadOnlyDictionary<string, double> MinScores,
    string PromptName,
    double Temperature,
    int MaxTokens,
    int MaxAttempts,
    int? Seed,
    IReadOnlyDictionary<string, string> Decorators,
    IReadOnlyDictionary<string, IReadOnlySet<string>> Users)
{
    /// <summary>Décorateurs connus : une faute de frappe ou une valeur comme « oui » ne coupe pas un garde-fou en silence.</summary>
    private static readonly Dictionary<string, (bool IsBool, int Minimum)> DecoratorKinds = new(StringComparer.Ordinal)
    {
        ["validate_output"] = (true, 0),
        ["max_output_chars"] = (false, 1),
        ["cache_embeddings"] = (true, 0),
        ["log"] = (true, 0),
        ["retries"] = (false, 0),
    };

    /// <summary>
    /// Clés attendues, section par section ("" : le premier niveau). Même raison : « topk » ou « max_char »
    /// ne doivent pas laisser la valeur par défaut s'appliquer sans rien dire. Une clé qui commence par
    /// « _ » est un commentaire (JSON n'en a pas).
    /// </summary>
    private static readonly Dictionary<string, string[]> KnownKeys = new(StringComparer.Ordinal)
    {
        [""] = new[] { "ai_service", "corpus", "index", "snapshots", "splitter", "retrieval", "generation", "decorators", "users" },
        ["ai_service"] = new[] { "base_url", "embedding_model", "generation_model", "timeout_seconds" },
        ["corpus"] = new[] { "directory" },
        ["index"] = new[] { "path" },
        ["snapshots"] = new[] { "directory" },
        ["splitter"] = new[] { "max_chars", "overlap_chars", "include_title" },
        ["retrieval"] = new[] { "top_k", "min_score" },
        ["generation"] = new[] { "prompt", "temperature", "max_tokens", "max_attempts", "seed" },
    };

    /// <summary>Racine du projet : le dossier qui contient config/, corpus/, prompts/.</summary>
    public static string ProjectRoot { get; } = FindProjectRoot();

    /// <summary>Le fichier d'où vient cette configuration (pour les rapports).</summary>
    public string Source { get; init; } = "";

    public static AppConfig Load(string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("ASSISTANT_CONFIG") ?? Path.Combine(ProjectRoot, "config", "app.json");
        JsonObject raw;
        try
        {
            raw = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new ConfigException($"{path} : un objet JSON est attendu");
        }
        catch (JsonException error)
        {
            throw new ConfigException($"{path} : JSON invalide — {error.Message}");
        }
        CheckKnownKeys(raw, path);
        var ai = Section(raw, "ai_service", path);
        var gen = Section(raw, "generation", path, required: false);
        var retrieval = Section(raw, "retrieval", path, required: false);
        var splitter = Section(raw, "splitter", path, required: false);
        string Rel(string relative) => Path.IsPathRooted(relative) ? relative : Path.Combine(ProjectRoot, relative);
        string Where(string section) => $"{path} [{section}]";
        return new AppConfig(
            AiBaseUrl: Environment.GetEnvironmentVariable("AI_SERVICE_URL") ?? Value<string>(ai, "base_url", Where("ai_service")),
            EmbeddingModel: Value<string>(ai, "embedding_model", Where("ai_service")),
            GenerationModel: Value<string>(ai, "generation_model", Where("ai_service")),
            Timeout: TimeSpan.FromSeconds(Value(ai, "timeout_seconds", Where("ai_service"), 300.0, min: 1)),
            CorpusDir: Rel(Value<string>(Section(raw, "corpus", path), "directory", Where("corpus"))),
            IndexPath: Rel(Value<string>(Section(raw, "index", path), "path", Where("index"))),
            SnapshotsDir: Rel(Value(Section(raw, "snapshots", path, required: false), "directory", Where("snapshots"), "eval/instantanes")),
            SplitterMaxChars: Value(splitter, "max_chars", Where("splitter"), 800, min: 1),
            SplitterOverlapChars: Value(splitter, "overlap_chars", Where("splitter"), 120, min: 0),
            SplitterIncludeTitle: Value(splitter, "include_title", Where("splitter"), true),
            TopK: Value(retrieval, "top_k", Where("retrieval"), 4, min: 1),
            MinScores: Section(retrieval, "min_score", path, required: false)
                .ToDictionary(kv => kv.Key, kv => Value<double>(Section(retrieval, "min_score", path), kv.Key, Where("retrieval.min_score"), min: -1, max: 1),
                              StringComparer.Ordinal),
            PromptName: Value(gen, "prompt", Where("generation"), "answer"),
            Temperature: Value(gen, "temperature", Where("generation"), 0.2, min: 0, max: 2),
            MaxTokens: Value(gen, "max_tokens", Where("generation"), 400, min: 1),
            MaxAttempts: Value(gen, "max_attempts", Where("generation"), 2, min: 1),
            Seed: gen["seed"] is null ? null : Value<int>(gen, "seed", Where("generation")),
            Decorators: LoadDecorators(Section(raw, "decorators", path, required: false), Where("decorators")),
            Users: Section(raw, "users", path, required: false).ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlySet<string>)Groups(Section(Section(raw, "users", path), kv.Key, path), Where($"users.{kv.Key}")),
                StringComparer.Ordinal))
        {
            Source = path,
        };
    }

    public double MinScoreFor(string embeddingModel) =>
        MinScores.TryGetValue(embeddingModel, out var score) ? score : MinScores.GetValueOrDefault("default", 0.4);

    /// <summary>
    /// Un seuil est-il configuré pour cet alias ? Configuré ne veut pas dire mesuré : le
    /// <c>_commentaire</c> de chaque configuration dit lesquels le sont (ADR 0004).
    /// </summary>
    public bool HasThresholdFor(string embeddingModel) => MinScores.ContainsKey(embeddingModel);

    public User User(string name) =>
        Users.TryGetValue(name, out var groups) ? new User(name, groups) : throw new UnknownUserException(name, Users.Keys.Order());

    public bool Decorator(string key, bool fallback) =>
        Decorators.TryGetValue(key, out var value) ? value == "true" : fallback;

    public int DecoratorInt(string key, int fallback) =>
        Decorators.TryGetValue(key, out var value) && int.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static void CheckKnownKeys(JsonObject raw, string path)
    {
        var sections = KnownKeys.Select(kv => (Name: kv.Key, Values: kv.Key.Length == 0 ? raw : raw[kv.Key] as JsonObject, Known: kv.Value))
            .Concat((raw["users"] as JsonObject ?? new JsonObject())
                .Select(kv => (Name: $"users.{kv.Key}", Values: kv.Value as JsonObject, Known: new[] { "groups" })));
        foreach (var (name, values, known) in sections)
        {
            if (values is null)
            {
                continue;   // absente ou mal formée : Section le dira
            }
            var unknown = values.Select(kv => kv.Key).Where(k => !k.StartsWith('_') && !known.Contains(k))
                .Order(StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
            {
                var where = name.Length == 0 ? path : $"{path} [{name}]";
                throw new ConfigException($"{where} : clé(s) inconnue(s) [{string.Join(", ", unknown)}] "
                                          + $"(connues : {string.Join(", ", known.Order(StringComparer.Ordinal))})");
            }
        }
    }

    private static JsonObject Section(JsonObject raw, string name, string path, bool required = true) =>
        raw[name] switch
        {
            null when required => throw new ConfigException($"{path} : section « {name} » absente"),
            null => new JsonObject(),
            JsonObject section => section,
            _ => throw new ConfigException($"{path} : section « {name} » mal formée"),
        };

    /// <summary>Valeur obligatoire d'un type donné.</summary>
    private static T Value<T>(JsonObject section, string key, string where, double? min = null, double? max = null)
    {
        var node = section[key] ?? throw new ConfigException($"{where} : clé « {key} » obligatoire");
        return Typed<T>(node, key, where, min, max);
    }

    /// <summary>Valeur facultative d'un type donné, avec sa valeur par défaut.</summary>
    private static T Value<T>(JsonObject section, string key, string where, T fallback, double? min = null, double? max = null) =>
        section[key] is { } node ? Typed<T>(node, key, where, min, max) : fallback;

    private static T Typed<T>(JsonNode? node, string key, string where, double? min, double? max)
    {
        if (node is not JsonValue value || !value.TryGetValue<T>(out var parsed) || parsed is null)
        {
            throw new ConfigException($"{where} : « {key} » doit être de type {typeof(T).Name}, pas {node?.ToJsonString() ?? "null"}");
        }
        if (parsed is IConvertible number and not string and not bool)
        {
            var d = number.ToDouble(CultureInfo.InvariantCulture);
            if (d < min || d > max)
            {
                throw new ConfigException($"{where} : « {key} » = {d.ToString(CultureInfo.InvariantCulture)} hors de [{min}, {max}]");
            }
        }
        return parsed;
    }

    private static Dictionary<string, string> LoadDecorators(JsonObject section, string where)
    {
        var unknown = section.Select(kv => kv.Key).Where(k => !DecoratorKinds.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new ConfigException($"{where} : clé(s) inconnue(s) [{string.Join(", ", unknown)}] (connues : {string.Join(", ", DecoratorKinds.Keys.Order())})");
        }
        return section.ToDictionary(
            kv => kv.Key,
            kv => DecoratorKinds[kv.Key].IsBool
                ? Typed<bool>(kv.Value, kv.Key, where, null, null) ? "true" : "false"
                : Typed<int>(kv.Value, kv.Key, where, DecoratorKinds[kv.Key].Minimum, null).ToString(CultureInfo.InvariantCulture),
            StringComparer.Ordinal);
    }

    private static HashSet<string> Groups(JsonObject user, string where) =>
        user["groups"] switch
        {
            null => new HashSet<string> { AccessPolicy.PublicGroup },
            JsonArray groups => groups.Select(g => g is JsonValue v && v.TryGetValue<string>(out var s)
                ? s
                : throw new ConfigException($"{where} : « groups » doit être une liste de chaînes")).ToHashSet(),
            _ => throw new ConfigException($"{where} : « groups » doit être une liste de chaînes"),
        };

    private static string FindProjectRoot()
    {
        // Le binaire vit dans src/Assistant.Cli/bin/…/net8.0 : on remonte jusqu'à config/app.json.
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "config", "app.json")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
        }
        return Directory.GetCurrentDirectory();
    }
}
