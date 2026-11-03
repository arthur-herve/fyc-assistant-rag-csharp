// Tests déterministes du cas d'usage CheckStatus : aucun modèle, aucun réseau.

using Assistant.Application;
using Assistant.Domain;
using Xunit;

namespace Assistant.Tests;

public class CheckStatusTests
{
    private static readonly Document[] Docs = { Fakes.Doc("a", "télétravail deux jours"), Fakes.Doc("b", "frais de repas") };

    private static StatusReport Status(FakeIndex index, Document[]? documents = null, ITextSplitter? splitter = null, IEmbedder? embedder = null) =>
        new CheckStatus(new ListSource(documents ?? Docs), splitter ?? new WholeDocumentSplitter(),
                        embedder ?? new KeywordEmbedder(), index, new StaticPrompts()).Execute();

    private sealed class OtherSplitter : ITextSplitter
    {
        public IReadOnlyList<Chunk> Split(Document d) => new WholeDocumentSplitter().Split(d);
        public IReadOnlyDictionary<string, object> Describe() =>
            new Dictionary<string, object> { ["type"] = "whole", ["max_chars"] = 300, ["include_title"] = true };
    }

    private sealed class BrokenEmbedder : IEmbedder
    {
        public EmbeddingBatch EmbedDocuments(IReadOnlyList<string> texts) => throw new AiServiceException("Service IA injoignable");
        public EmbeddingBatch EmbedQuery(string text) => throw new AiServiceException("Service IA injoignable");
    }

    [Fact]
    public void Up_to_date_when_nothing_changed()
    {
        var report = Status(Build.Indexed(null, Docs));
        Assert.True(report.UpToDate);
    }

    [Fact]
    public void Names_the_model_the_ai_service_serves() =>
        Assert.Equal("fake-keywords-v2", Status(Build.Indexed(null, Docs), embedder: new KeywordEmbedder("fake-keywords-v2")).EmbeddingModel);

    [Fact]
    public void Detects_a_modified_corpus()
    {
        var report = Status(Build.Indexed(null, Docs), Docs.Append(Fakes.Doc("c", "congés")).ToArray());
        Assert.Single(report.Issues);
        Assert.Contains("corpus modifié", report.Issues[0]);
    }

    [Fact]
    public void Detects_a_text_modified_under_the_same_id()
    {
        // Mêmes documents, mêmes identifiants : seul un texte a changé, et cela suffit.
        var report = Status(Build.Indexed(null, Docs), new[] { Fakes.Doc("a", "télétravail trois jours"), Docs[1] });
        Assert.Contains("corpus modifié", Assert.Single(report.Issues));
    }

    [Fact]
    public void Detects_a_changed_splitter()
    {
        // Le message montre l'ancien et le nouveau découpage, clés triées, et true plutôt que le True de bool.ToString().
        var issue = Status(Build.Indexed(null, Docs), splitter: new OtherSplitter()).Issues[0];
        Assert.Contains("découpage modifié ({type: whole} → ", issue);
        Assert.Contains("{include_title: true, max_chars: 300, type: whole}", issue);
        Assert.Contains("réindexer", issue);
    }

    [Fact]
    public void Detects_that_the_ai_service_now_serves_another_model() =>
        Assert.Contains("fake-keywords-v2", Status(Build.Indexed(null, Docs), embedder: new KeywordEmbedder("fake-keywords-v2")).Issues[0]);

    [Fact]
    public void Detects_an_index_id_computed_otherwise_since_the_indexing()
    {
        // Même corpus, même découpage, même modèle, mais un identifiant calculé autrement, par une autre version du code :
        // l'index est à refaire, que le service IA réponde ou non.
        var index = Build.Indexed(null, Docs);
        var current = index.Manifest()!.IndexId;
        index.Replace(index.Manifest()! with { IndexId = "0123456789ab" }, index.Chunks.ToList(), index.Vectors.ToList());
        Assert.Equal($"identifiant d'index calculé autrement depuis l'indexation (0123456789ab → {current}) : réindexer",
                     Assert.Single(Status(index).Issues));
        var unreachable = Status(index, embedder: new BrokenEmbedder());
        Assert.Equal((false, false, 2), (unreachable.UpToDate, unreachable.Unverified, unreachable.Issues.Count));
    }

    [Fact]
    public void Unreachable_ai_service_is_unverified_not_fatal()
    {
        var report = Status(Build.Indexed(null, Docs), embedder: new BrokenEmbedder());
        Assert.True(report.Unverified);
        Assert.False(report.UpToDate);
        Assert.NotNull(report.Index);
    }
}
