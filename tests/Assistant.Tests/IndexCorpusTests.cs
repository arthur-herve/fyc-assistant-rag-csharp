// Tests déterministes du cas d'usage IndexCorpus : aucun modèle, aucun réseau.

using Assistant.Application;
using Xunit;

namespace Assistant.Tests;

public class IndexCorpusTests
{
    [Fact]
    public void Every_chunk_is_indexed_and_counted()
    {
        var index = new FakeIndex();
        var summary = new IndexCorpus(new ListSource(Fakes.Doc("a", "télétravail"), Fakes.Doc("b", "frais")),
                                      new WholeDocumentSplitter(), new KeywordEmbedder(), index).Execute();
        Assert.Equal(new IndexSummary(2, 2), summary);
        Assert.Equal(new[] { "a#0", "b#0" }, index.Chunks.Select(c => c.Id));
    }

    [Fact]
    public void Empty_corpus_is_refused() =>
        Assert.Throws<EmptyCorpusException>(() =>
            new IndexCorpus(new ListSource(), new WholeDocumentSplitter(), new KeywordEmbedder(), new FakeIndex()).Execute());
}
