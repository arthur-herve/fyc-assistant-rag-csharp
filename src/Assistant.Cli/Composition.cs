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
        var generator = new MissingGenerator();   // un bouchon : generationModel attend l'adaptateur HTTP
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

/// <summary>
/// Le générateur, tant que son adaptateur HTTP n'est pas écrit. <c>index</c> n'en a pas besoin, et une question sans
/// passage pertinent est refusée sans lui. Seule une question qui demande une réponse générée s'arrête sur ce message.
/// </summary>
public sealed class MissingGenerator : IGenerator
{
    public string Generate(GenerationRequest request) =>
        throw new AssistantApplicationException(
            "le générateur HTTP reste à écrire : voir exercices/s2.3-adaptateur-de-generation/README.md");
}
