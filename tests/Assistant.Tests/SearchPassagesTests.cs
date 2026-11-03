// Tests déterministes du cas d'usage SearchPassages : aucun modèle, aucun réseau.

using Assistant.Application;
using Assistant.Domain;
using Xunit;

namespace Assistant.Tests;

public class SearchPassagesTests
{
    [Fact]
    public void Returns_only_readable_passages_with_their_manifest()
    {
        var search = new SearchPassages(new KeywordEmbedder(), Build.Indexed());
        var (manifest, passages) = search.Execute(Fakes.Alice, "salaire senior", topK: 4);
        Assert.NotEmpty(manifest.IndexId);
        Assert.DoesNotContain(passages, p => p.Chunk.DocumentId == "grille");   // réservé au groupe rh
        var (_, forBruno) = search.Execute(Fakes.Bruno, "salaire senior", topK: 4);
        Assert.Equal("grille", forBruno[0].Chunk.DocumentId);
    }

    [Fact]
    public void Refuses_an_index_built_by_another_model() =>
        Assert.Throws<IndexModelMismatchException>(() =>
            new SearchPassages(new KeywordEmbedder("autre-modele"), Build.Indexed()).Execute(Fakes.Alice, "Q ?", 4));

    [Fact]
    public void Requires_an_index() =>
        Assert.Throws<IndexNotBuiltException>(() =>
            new SearchPassages(new KeywordEmbedder(), new FakeIndex()).Execute(Fakes.Alice, "Q ?", 4));
}
