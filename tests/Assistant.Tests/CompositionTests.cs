// La racine de composition, montée avec la configuration livrée, cache d'embeddings compris, contre un faux service IA
// dont on change ce qu'il sert en cours de route (séquence 4.2).

using Assistant.Application;
using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

/// <summary>
/// Le cas d'un processus qui dure plus longtemps qu'une commande : ce qui est servi derrière l'alias change, le modèle
/// ou seulement ses vecteurs, puis on réindexe. Le cache d'embeddings ne doit pas masquer le changement.
/// </summary>
public sealed class CompositionTests : IDisposable
{
    private const string Question = "Combien de jours de télétravail par semaine ?";
    private readonly FakeAiService _ai = new("faux:modele-a");   // le même modèle pour tout alias, changé à chaud
    private readonly string _dir = Directory.CreateTempSubdirectory("fyc-composition-").FullName;
    private readonly AppConfig _config;

    public CompositionTests()
    {
        // Délai court : si le faux service ne répond pas, le test échoue vite au lieu d'attendre 5 minutes.
        _config = AppConfig.Load() with { AiBaseUrl = _ai.Url, Timeout = TimeSpan.FromSeconds(5) };
        Assert.True(_config.Decorator("cache_embeddings", false));   // c'est le cas de la configuration livrée
    }

    public void Dispose()
    {
        _ai.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private Container Build() => Composition.Build(_config, new Overrides(IndexPath: Path.Combine(_dir, "index.json")));

    [Fact]
    public void Reindexing_follows_the_served_model_and_the_cache_does_not_hide_it()
    {
        var container = Build();
        var alice = _config.User("alice");

        Assert.Equal("faux:modele-a", container.IndexCorpus.Execute().EmbeddingModel);
        container.SearchPassages.Execute(alice, Question, 4);                  // mémorisée par le cache
        Assert.True(container.CheckStatus.Execute().UpToDate);

        // Nouveau modèle derrière le même alias, avec la même dimension : sans contrôle, la panne serait silencieuse.
        _ai.Model = "faux:modele-b";
        // Question déjà posée : le cache rend des vecteurs du modèle de l'index, et la recherche reste cohérente.
        Assert.Equal("faux:modele-a", container.SearchPassages.Execute(alice, Question, 4).Manifest.EmbeddingModel);
        Assert.Throws<IndexModelMismatchException>(                           // question nouvelle : changement détecté
            () => container.SearchPassages.Execute(alice, "Quel est le plafond d'un repas d'affaires ?", 4));
        Assert.False(container.CheckStatus.Execute().UpToDate);                // status n'utilise pas le cache

        // La réindexation suit le modèle servi, et la question déjà posée repart au service.
        Assert.Equal("faux:modele-b", container.IndexCorpus.Execute().EmbeddingModel);
        Assert.Equal("faux:modele-b", container.SearchPassages.Execute(alice, Question, 4).Manifest.EmbeddingModel);
        Assert.True(container.CheckStatus.Execute().UpToDate);
    }

    [Fact]
    public void New_vectors_under_the_same_model_name_are_picked_up_by_reindexing()
    {
        // Le service change ses préfixes : même identifiant de modèle, mais d'autres vecteurs.
        var container = Build();
        var alice = _config.User("alice");
        var before = container.IndexCorpus.Execute();
        container.SearchPassages.Execute(alice, Question, 4);                   // mémorisée par le cache

        _ai.Offset = 3.0;
        var after = container.IndexCorpus.Execute();
        Assert.Equal(before.IndexId, after.IndexId);   // rien, dans l'identifiant, ne trahit le changement
        var again = container.SearchPassages.Execute(alice, Question, 4).Passages.Select(p => (p.Chunk.Id, p.Score));
        var fresh = Build().SearchPassages.Execute(alice, Question, 4).Passages.Select(p => (p.Chunk.Id, p.Score));
        Assert.Equal(fresh, again);
    }
}
