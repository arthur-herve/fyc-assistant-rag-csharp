// Racine de composition : le seul endroit qui connaît toutes les couches.
// Elle part de la configuration, instancie les adaptateurs et les injecte dans les
// cas d'usage. Changer de moteur, de stockage ou de modèle
// se fait ici ou dans les fichiers de configuration, jamais dans le domaine.

using Assistant.Application;
using Assistant.Infrastructure;

namespace Assistant.Cli;

public sealed record Container(
    IndexCorpus IndexCorpus,
    AskQuestion AskQuestion);

/// <summary>
/// Ce que la ligne de commande peut changer sans
/// toucher au fichier de configuration : chaque champ nul garde la valeur configurée.
/// </summary>
public sealed record Overrides(
    string? EmbeddingModel = null,
    string? GenerationModel = null);

public static class Composition
{
    public static string PromptsDir => Path.Combine(AppConfig.ProjectRoot, "prompts");

    public static Container Build(AppConfig config, string? embeddingModel = null, string? generationModel = null) =>
        Build(config, new Overrides(embeddingModel, generationModel));

    public static Container Build(AppConfig config, Overrides overrides)
    {
        var embeddingModel = overrides.EmbeddingModel ?? config.EmbeddingModel;
        var generationModel = overrides.GenerationModel ?? config.GenerationModel;

        var index = new JsonVectorIndex(config.IndexPath);
        var embedder = new HttpEmbedder(config.AiBaseUrl, embeddingModel, config.Timeout);
        var generator = new HttpGenerator(config.AiBaseUrl, generationModel, config.Timeout);
        var source = new MarkdownCorpus(config.CorpusDir);
        var splitter = new ParagraphSplitter(config.SplitterMaxChars,
                                             config.SplitterOverlapChars,
                                             config.SplitterIncludeTitle);
        var prompts = new FilePromptRepository(PromptsDir);

        var indexCorpus = new IndexCorpus(source, splitter, embedder, index);
        var settings = new AskSettings(
            TopK: config.TopK,
            MinScore: config.MinScore,
            Temperature: config.Temperature,
            MaxTokens: config.MaxTokens,
            PromptName: config.PromptName);
        var askQuestion = new AskQuestion(embedder, index, generator, prompts, settings);
        return new Container(indexCorpus, askQuestion);
    }
}
