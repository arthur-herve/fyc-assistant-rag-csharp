// Racine de composition : le seul endroit qui connaît toutes les couches.
// Elle part de la configuration, instancie les adaptateurs et les injecte dans les
// cas d'usage. Changer de moteur, de stockage ou de modèle
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
                                  string? promptName = null) =>
        Build(config, new Overrides(embeddingModel, generationModel, PromptName: promptName));

    public static Container Build(AppConfig config, Overrides overrides)
    {
        var embeddingModel = overrides.EmbeddingModel ?? config.EmbeddingModel;
        var generationModel = overrides.GenerationModel ?? config.GenerationModel;

        var index = new JsonVectorIndex(overrides.IndexPath ?? config.IndexPath);
        var embedder = new HttpEmbedder(config.AiBaseUrl, embeddingModel, config.Timeout);
        var generator = new HttpGenerator(config.AiBaseUrl, generationModel, config.Timeout);
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
        };
        var recordSnapshot = new RecordSnapshot(askQuestion, snapshots, clock, configuration);
        return new Container(indexCorpus, askQuestion, searchPassages, recordSnapshot, snapshots,
                             prompts, settings, embeddingModel);
    }
}
