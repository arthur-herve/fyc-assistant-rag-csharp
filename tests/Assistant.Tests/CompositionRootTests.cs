// La racine de composition, montée avec la configuration livrée : la validation de la sortie reste sous le
// journal des générations, et le seuil suit le modèle d'embeddings choisi.

using Assistant.Application;
using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

public class CompositionRootTests
{
    private const string Leak = "Okay, let's see. The user is asking about remote work. The passage [1] says two days per week.";
    private static readonly GenerationRequest Request = new("système", "Passages :\n[1] x\n\nQuestion : ?", 0.2, 100, null);

    [Fact]
    public void A_rejected_output_is_refused_and_logged_whatever_else_is_stacked()
    {
        var logs = new List<string>();
        var config = AppConfig.Load() with
        {
            Decorators = new Dictionary<string, string> { ["cache_embeddings"] = "true", ["retries"] = "2", ["log"] = "true" },
        };
        var (_, generator) = Composition.Decorate(new KeywordEmbedder(), ScriptedGenerator.WithModel("qwen", Leak), config, logs.Add, () => null);
        Assert.Throws<ModelOutputRejectedException>(() => generator.Generate(Request));
        Assert.Contains(logs, line => line.Contains("rejetée"));   // la validation est sous le journal des générations
    }

    [Fact]
    public void Without_output_validation_the_output_goes_through_as_is()
    {
        var config = AppConfig.Load() with { Decorators = new Dictionary<string, string> { ["validate_output"] = "false" } };
        var (_, generator) = Composition.Decorate(new KeywordEmbedder(), ScriptedGenerator.WithModel("qwen", Leak), config, _ => { }, () => null);
        Assert.Equal(Leak, generator.Generate(Request).Text);
    }

    [Fact]
    public void The_threshold_follows_the_embedding_model_chosen_on_the_command_line()
    {
        var config = AppConfig.Load();
        Assert.NotEqual(config.MinScoreFor("nomic"), config.MinScoreFor(config.EmbeddingModel));
        Assert.Equal(config.MinScoreFor("nomic"), Composition.Build(config, embeddingModel: "nomic").Settings.MinScore);
    }

    [Fact]
    public void An_imposed_threshold_prevails_over_the_threshold_of_the_model() =>
        Assert.Equal(0.9, Composition.Build(AppConfig.Load(), new Overrides(EmbeddingModel: "nomic", MinScore: 0.9)).Settings.MinScore);
}
