// Racine de composition : le seul endroit qui connaît toutes les couches.
// Elle lit la configuration, instancie les adaptateurs, empile les décorateurs et
// injecte le tout dans les cas d'usage. Changer de moteur, de stockage ou de modèle
// se fait ici (ou dans les fichiers de configuration), jamais dans le domaine.

using Assistant.Application;
using Assistant.Infrastructure;

namespace Assistant.Cli;

public sealed record Container(
    AppConfig Config,
    IndexCorpus IndexCorpus,
    AskQuestion AskQuestion,
    SearchPassages SearchPassages,
    CheckStatus CheckStatus,
    RecordSnapshot RecordSnapshot,
    ISnapshotStore Snapshots,
    IVectorIndex Index,
    IEmbedder Embedder,
    IPromptRepository Prompts,
    AskSettings Settings,
    string EmbeddingModel,
    string GenerationModel);

/// <summary>
/// Ce que la ligne de commande, le banc d'essai et les expériences peuvent changer sans
/// toucher au fichier de configuration : chaque champ nul garde la valeur configurée.
/// </summary>
public sealed record Overrides(
    string? EmbeddingModel = null,
    string? GenerationModel = null,
    string? IndexPath = null,
    double? MinScore = null,
    string? PromptName = null,
    int? SplitterMaxChars = null,
    int? SplitterOverlapChars = null,
    int? Seed = null,
    string? SnapshotsDir = null);

public static class Composition
{
    public static string PromptsDir => Path.Combine(AppConfig.ProjectRoot, "prompts");

    public static Container Build(AppConfig config, string? embeddingModel = null, string? generationModel = null,
                                  string? indexPath = null, double? minScore = null, string? promptName = null,
                                  Action<string>? log = null) =>
        Build(config, new Overrides(embeddingModel, generationModel, indexPath, minScore, promptName), log);

    public static Container Build(AppConfig config, Overrides overrides, Action<string>? log = null)
    {
        var embeddingModel = overrides.EmbeddingModel ?? config.EmbeddingModel;
        var generationModel = overrides.GenerationModel ?? config.GenerationModel;
        log ??= _ => { };

        // L'adaptateur nu sert à `status` : un cache d'embeddings masquerait un changement de modèle servi.
        var rawEmbedder = new HttpEmbedder(config.AiBaseUrl, embeddingModel, config.Timeout);
        var (embedder, generator) = Decorate(rawEmbedder, new HttpGenerator(config.AiBaseUrl, generationModel, config.Timeout), config, log);
        var index = new JsonVectorIndex(overrides.IndexPath ?? config.IndexPath);
        var source = new MarkdownCorpus(config.CorpusDir);
        var splitter = new ParagraphSplitter(overrides.SplitterMaxChars ?? config.SplitterMaxChars,
                                             overrides.SplitterOverlapChars ?? config.SplitterOverlapChars,
                                             config.SplitterIncludeTitle);
        var prompts = new FilePromptRepository(PromptsDir);
        var clock = new SystemClock();

        var indexCorpus = new IndexCorpus(source, splitter, embedder, index, clock);
        var settings = new AskSettings(
            TopK: config.TopK,
            MinScore: overrides.MinScore ?? config.MinScoreFor(embeddingModel),
            MaxAttempts: config.MaxAttempts,
            Temperature: config.Temperature,
            MaxTokens: config.MaxTokens,
            Seed: overrides.Seed ?? config.Seed,
            PromptName: overrides.PromptName ?? config.PromptName);
        var askQuestion = new AskQuestion(embedder, index, generator, prompts, settings);
        var searchPassages = new SearchPassages(embedder, index);
        var checkStatus = new CheckStatus(source, splitter, rawEmbedder, index, prompts, settings.PromptName);
        var snapshots = new JsonSnapshotStore(overrides.SnapshotsDir ?? config.SnapshotsDir);
        // Empreinte de configuration d'un instantané : tout ce qui change les réponses.
        // Valeurs typées, comme dans la version Python : un instantané C# se compare à un instantané Python.
        var configuration = new Dictionary<string, object?>
        {
            ["corpus"] = Path.GetFileName(config.CorpusDir.TrimEnd(Path.DirectorySeparatorChar, '/')),
            ["embedding_model"] = embeddingModel,
            ["generation_model"] = generationModel,
            ["splitter"] = splitter.Describe(),
            ["top_k"] = settings.TopK,
            ["min_score"] = settings.MinScore,
            ["temperature"] = settings.Temperature,
            ["max_tokens"] = settings.MaxTokens,
            ["seed"] = settings.Seed,
            ["prompt"] = settings.PromptName,
        };
        var recordSnapshot = new RecordSnapshot(askQuestion, snapshots, clock, configuration);
        return new Container(config, indexCorpus, askQuestion, searchPassages, checkStatus, recordSnapshot, snapshots,
                             index, embedder, prompts, settings, embeddingModel, generationModel);
    }

    /// <summary>
    /// Empile les décorateurs choisis dans la section "decorators" (séquence 4.1). Ordre, de
    /// l'intérieur vers l'extérieur : nouvelles tentatives (au plus près du réseau), journal
    /// des embeddings, cache, validation de la sortie (règle métier), puis journal des
    /// générations (pour voir aussi les rejets). Les cas d'usage ne voient que les ports.
    /// </summary>
    public static (IEmbedder, IGenerator) Decorate(IEmbedder embedder, IGenerator generator, AppConfig config, Action<string> log)
    {
        var retries = config.DecoratorInt("retries", 0);
        if (retries > 0)
        {
            embedder = new RetryingEmbedder(embedder, retries, log: log);
            generator = new RetryingGenerator(generator, retries, log: log);
        }
        if (config.Decorator("log", false))
        {
            embedder = new LoggingEmbedder(embedder, log);
        }
        if (config.Decorator("cache_embeddings", false))
        {
            embedder = new CachedEmbedder(embedder);
        }
        if (config.Decorator("validate_output", true))
        {
            generator = new OutputValidatingGenerator(generator, config.DecoratorInt("max_output_chars", 1500));
        }
        if (config.Decorator("log", false))
        {
            generator = new LoggingGenerator(generator, log);
        }
        return (embedder, generator);
    }
}
