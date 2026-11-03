// La racine de composition, montée avec la configuration livrée : le seuil suit le modèle d'embeddings choisi.

using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

public class CompositionRootTests
{
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
