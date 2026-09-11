// Configuration de l'application, lue depuis config/app.json (System.Text.Json, rien d'autre).
// Elle ne contient AUCUN détail de modèle : seulement l'adresse du service IA et des alias.

using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Domain;

namespace Assistant.Cli;

public sealed class UnknownUserException : Exception
{
    public UnknownUserException(string name, IEnumerable<string> known)
        : base($"utilisateur inconnu : {name} (connus : [{string.Join(", ", known)}])") { }
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
    /// <summary>Racine du projet : le dossier qui contient config/, corpus/, prompts/.</summary>
    public static string ProjectRoot { get; } = FindProjectRoot();

    public static AppConfig Load(string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("ASSISTANT_CONFIG") ?? Path.Combine(ProjectRoot, "config", "app.json");
        var raw = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var ai = raw["ai_service"]!.AsObject();
        var gen = raw["generation"]?.AsObject() ?? new JsonObject();
        var retrieval = raw["retrieval"]?.AsObject() ?? new JsonObject();
        var splitter = raw["splitter"]?.AsObject() ?? new JsonObject();
        string Rel(string relative) => Path.IsPathRooted(relative) ? relative : Path.Combine(ProjectRoot, relative);
        return new AppConfig(
            AiBaseUrl: Environment.GetEnvironmentVariable("AI_SERVICE_URL") ?? ai["base_url"]!.GetValue<string>(),
            EmbeddingModel: ai["embedding_model"]!.GetValue<string>(),
            GenerationModel: ai["generation_model"]!.GetValue<string>(),
            Timeout: TimeSpan.FromSeconds(ai["timeout_seconds"]?.GetValue<double>() ?? 300),
            CorpusDir: Rel(raw["corpus"]!["directory"]!.GetValue<string>()),
            IndexPath: Rel(raw["index"]!["path"]!.GetValue<string>()),
            SnapshotsDir: Rel(raw["snapshots"]?["directory"]?.GetValue<string>() ?? "eval/instantanes"),
            SplitterMaxChars: splitter["max_chars"]?.GetValue<int>() ?? 800,
            SplitterOverlapChars: splitter["overlap_chars"]?.GetValue<int>() ?? 120,
            SplitterIncludeTitle: splitter["include_title"]?.GetValue<bool>() ?? true,
            TopK: retrieval["top_k"]?.GetValue<int>() ?? 4,
            MinScores: (retrieval["min_score"]?.AsObject() ?? new JsonObject())
                .ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<double>(), StringComparer.Ordinal),
            PromptName: gen["prompt"]?.GetValue<string>() ?? "answer",
            Temperature: gen["temperature"]?.GetValue<double>() ?? 0.2,
            MaxTokens: gen["max_tokens"]?.GetValue<int>() ?? 400,
            MaxAttempts: gen["max_attempts"]?.GetValue<int>() ?? 2,
            Seed: gen["seed"]?.GetValue<int>(),
            Decorators: (raw["decorators"]?.AsObject() ?? new JsonObject())
                .ToDictionary(kv => kv.Key, kv => kv.Value!.ToJsonString(), StringComparer.Ordinal),
            Users: (raw["users"]?.AsObject() ?? new JsonObject()).ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlySet<string>)(kv.Value!["groups"]?.AsArray().Select(g => g!.GetValue<string>()).ToHashSet()
                                             ?? new HashSet<string> { AccessPolicy.PublicGroup }),
                StringComparer.Ordinal));
    }

    public double MinScoreFor(string embeddingModel) =>
        MinScores.TryGetValue(embeddingModel, out var score) ? score : MinScores.GetValueOrDefault("default", 0.4);

    public User User(string name) =>
        Users.TryGetValue(name, out var groups) ? new User(name, groups) : throw new UnknownUserException(name, Users.Keys.Order());

    public bool Decorator(string key, bool fallback) =>
        Decorators.TryGetValue(key, out var value) ? value == "true" : fallback;

    public int DecoratorInt(string key, int fallback) =>
        Decorators.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

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
