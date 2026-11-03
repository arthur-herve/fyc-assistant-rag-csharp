// Tests déterministes du cas d'usage IndexCorpus : aucun modèle, aucun réseau.

using Assistant.Application;
using Xunit;

namespace Assistant.Tests;

public class IndexCorpusTests
{
    [Fact]
    public void Manifest_records_what_is_needed_to_explain_the_index()
    {
        var index = new FakeIndex();
        var manifest = new IndexCorpus(new ListSource(Fakes.Doc("a", "télétravail"), Fakes.Doc("b", "frais")),
                                       new WholeDocumentSplitter(), new KeywordEmbedder(), index).Execute();
        Assert.Equal("fake-keywords", manifest.EmbeddingModel);
        Assert.Equal(2, manifest.ChunkCount);
        Assert.Same(manifest, index.Manifest());
    }

    [Fact]
    public void A_model_change_between_two_batches_is_refused()
    {
        // Le service change de modèle au milieu d'une indexation : on refuse un index mélangé.
        var embedder = new SwitchingEmbedder();
        var use = new IndexCorpus(new ListSource(Fakes.Doc("a", "télétravail"), Fakes.Doc("b", "frais"), Fakes.Doc("c", "congés")),
                                  new WholeDocumentSplitter(), embedder, new FakeIndex(), batchSize: 2);
        Assert.Throws<InconsistentEmbeddingsException>(() => use.Execute());
        Assert.Equal(2, embedder.Calls);
    }

    private sealed class SwitchingEmbedder : IEmbedder
    {
        private readonly KeywordEmbedder _inner = new();
        public int Calls { get; private set; }

        public EmbeddingBatch EmbedDocuments(IReadOnlyList<string> texts)
        {
            var batch = _inner.EmbedDocuments(texts);
            return batch with { Model = ++Calls == 1 ? "modele-a" : "modele-b" };
        }

        public EmbeddingBatch EmbedQuery(string text) => _inner.EmbedQuery(text);
    }

    [Fact]
    public void Empty_corpus_is_refused() =>
        Assert.Throws<EmptyCorpusException>(() =>
            new IndexCorpus(new ListSource(), new WholeDocumentSplitter(), new KeywordEmbedder(), new FakeIndex()).Execute());
}
