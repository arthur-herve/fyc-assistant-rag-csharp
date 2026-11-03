// Racine de composition : le seul endroit qui connaît toutes les couches.
// Elle part de la configuration, instancie les adaptateurs, empile les décorateurs et
// injecte le tout dans les cas d'usage. Changer de moteur, de stockage ou de modèle
// se fait ici ou dans les fichiers de configuration, jamais dans le domaine.

using Assistant.Application;
using Assistant.Infrastructure;

namespace Assistant.Cli;

public sealed record Container(
    IndexCorpus IndexCorpus,
    AskQuestion AskQuestion,
    SearchPassages SearchPassages,
    RecordSnapshot RecordSnapshot,
    ISnapshotStore Snapshots,
    IPromptRepository Prompts,
    AskSettings Settings,
    string EmbeddingModel);

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
                                  string? promptName = null, Action<string>? log = null) =>
        Build(config, new Overrides(embeddingModel, generationModel, PromptName: promptName), log);

    public static Container Build(AppConfig config, Overrides overrides, Action<string>? log = null)
    {
        var embeddingModel = overrides.EmbeddingModel ?? config.EmbeddingModel;
        var generationModel = overrides.GenerationModel ?? config.GenerationModel;
        log ??= _ => { };

        var index = new JsonVectorIndex(overrides.IndexPath ?? config.IndexPath);
        var (embedder, generator) = Decorate(new HttpEmbedder(config.AiBaseUrl, embeddingModel, config.Timeout),
                                             new HttpGenerator(config.AiBaseUrl, generationModel, config.Timeout), config, log,
                                             currentIndex: index.Manifest);
        var source = new MarkdownCorpus(config.CorpusDir);
        var splitter = new ParagraphSplitter(overrides.SplitterMaxChars ?? config.SplitterMaxChars,
                                             overrides.SplitterOverlapChars ?? config.SplitterOverlapChars,
                                             config.SplitterIncludeTitle);
        var prompts = new FilePromptRepository(PromptsDir);
        var clock = new SystemClock();

        var indexCorpus = new IndexCorpus(source, splitter, embedder, index);
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
        var snapshots = new JsonSnapshotStore(overrides.SnapshotsDir ?? config.SnapshotsDir);
        // L'empreinte de configuration d'un instantané : tout ce qui change les réponses côté application.
        // Les réglages propres au service IA, comme le budget de réflexion, n'y figurent pas, parce qu'ils
        // vivent dans l'autre déployable. C'est une limite à nommer, pas un oubli.
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
            ["max_attempts"] = settings.MaxAttempts,
            ["validate_output"] = config.Decorator("validate_output", true),
            ["max_output_chars"] = config.DecoratorInt("max_output_chars", 1500),
        };
        var recordSnapshot = new RecordSnapshot(askQuestion, snapshots, clock, configuration);
        return new Container(indexCorpus, askQuestion, searchPassages, recordSnapshot, snapshots,
                             prompts, settings, embeddingModel);
    }

    /// <summary>
    /// Empile les décorateurs choisis dans la section "decorators" (séquence 4.1), de l'intérieur
    /// vers l'extérieur. Les nouvelles tentatives sont au plus près du réseau. Viennent ensuite le
    /// journal des embeddings et le cache. Côté génération, la validation de la sortie, qui est une
    /// règle métier, est enveloppée par le journal des générations, pour que celui-ci montre aussi
    /// les rejets. Les cas d'usage ne voient que les ports.
    /// <paramref name="currentIndex"/> renvoie le manifeste de l'index courant : le cache
    /// d'embeddings ne sert que cet index.
    /// </summary>
    public static (IEmbedder, IGenerator) Decorate(IEmbedder embedder, IGenerator generator, AppConfig config, Action<string> log,
                                                   Func<IndexManifest?> currentIndex)
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
            embedder = new CachedEmbedder(embedder, currentIndex);
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
