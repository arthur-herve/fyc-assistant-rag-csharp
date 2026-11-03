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

    [Fact]
    public void The_passages_are_labelled_with_the_index_they_come_from()
    {
        var index = new RebuiltDuringSearch(rebuilds: 1);
        var first = index.Manifest()!.IndexId;
        var retrieval = new SearchPassages(new KeywordEmbedder(), index).Execute(Fakes.Alice, "télétravail", 4);
        Assert.NotEqual(first, retrieval.Manifest.IndexId);
        Assert.Equal(index.Manifest()!.IndexId, retrieval.Manifest.IndexId);
    }

    [Fact]
    public void An_index_rebuilt_by_another_model_meanwhile_is_refused() =>
        Assert.Throws<IndexModelMismatchException>(() =>
            new SearchPassages(new KeywordEmbedder(), new RebuiltDuringSearch(rebuilds: 1, model: "autre-modele"))
                .Execute(Fakes.Alice, "télétravail", 4));

    [Fact]
    public void An_index_rebuilt_with_another_dimension_is_an_index_error_not_a_crash() =>
        // L'index est reconstruit pendant la recherche par un modèle d'une autre dimension. La recherche refuse
        // l'index remplacé, car indexId est vérifié avant la dimension. SearchPassages recommence, et c'est le
        // contrôle du modèle qui répond : 409, il faut réindexer, au lieu d'une erreur 500.
        Assert.Throws<IndexModelMismatchException>(() =>
            new SearchPassages(new KeywordEmbedder(), new RebuiltWithAnotherDimension()).Execute(Fakes.Alice, "télétravail", 4));

    private sealed class RebuiltWithAnotherDimension : Assistant.Infrastructure.InMemoryVectorIndex
    {
        private bool _rebuilt;

        public RebuiltWithAnotherDimension()
        {
            var ready = Build.Indexed();
            base.Replace(ready.Manifest()!, ready.Chunks, ready.Vectors);
        }

        public override IReadOnlyList<Passage> Search(double[] vector, int topK, Func<Chunk, bool> predicate, string? indexId = null)
        {
            if (!_rebuilt)
            {
                _rebuilt = true;
                var dimension = vector.Length + 2;
                Replace(new IndexManifest("autre-index", "autre-modele", dimension, "fp", new Dictionary<string, object>(), 1, 1, "t"),
                        new[] { new Chunk("autre#0", "autre", "Autre", "texte", 0, new HashSet<string> { "tous" }) },
                        new[] { Enumerable.Repeat(1.0, dimension).ToArray() });
            }
            return base.Search(vector, topK, predicate, indexId);
        }
    }

    [Fact]
    public void An_index_rebuilt_at_every_attempt_is_an_error() =>
        Assert.Throws<IndexReplacedException>(() =>
            new SearchPassages(new KeywordEmbedder(), new RebuiltDuringSearch(rebuilds: 2)).Execute(Fakes.Alice, "télétravail", 4));

    /// <summary>
    /// Un autre processus reconstruit l'index juste pendant la recherche : celle-ci porte déjà sur
    /// le nouvel index, alors que le modèle a été contrôlé sur l'ancien manifeste.
    /// </summary>
    private sealed class RebuiltDuringSearch : IVectorIndex
    {
        private readonly FakeIndex _index = new();
        private readonly string _model;
        private int _rebuilds;

        public RebuiltDuringSearch(int rebuilds, string model = "fake-keywords")
        {
            (_rebuilds, _model) = (rebuilds, model);
            Rebuild(new KeywordEmbedder(), "départ");
        }

        private void Rebuild(IEmbedder embedder, string note) =>
            new IndexCorpus(new ListSource(Fakes.Doc("teletravail", "Deux jours de télétravail par semaine."),
                                           Fakes.Doc("note", $"Note de version : {note}.")),   // autre corpus, autre index_id
                            new WholeDocumentSplitter(), embedder, _index, new FixedClock()).Execute();

        public IndexManifest? Manifest() => _index.Manifest();

        public void Replace(IndexManifest manifest, IReadOnlyList<Chunk> chunks, IReadOnlyList<double[]> vectors) =>
            _index.Replace(manifest, chunks, vectors);

        public IReadOnlyList<Passage> Search(double[] vector, int topK, Func<Chunk, bool> predicate, string? indexId = null)
        {
            if (_rebuilds > 0)
            {
                _rebuilds--;
                Rebuild(new KeywordEmbedder(_model), $"reconstruit, reste {_rebuilds}");
            }
            return _index.Search(vector, topK, predicate, indexId);
        }
    }

    [Fact]
    public void An_index_replaced_then_restored_during_the_search_is_not_mixed_up()
    {
        // L'index passe de A à B puis revient à A : les passages viennent de l'index annoncé dans la trace, jamais de B.
        var retrieval = new SearchPassages(new KeywordEmbedder(), new ReplacedThenRestored()).Execute(Fakes.Alice, "télétravail", 4);
        Assert.Equal(new[] { "teletravail" }, retrieval.Passages.Select(p => p.Chunk.DocumentId));
    }

    [Fact]
    public void A_search_error_on_the_checked_index_is_a_real_error()
    {
        // Même index, recherche en échec : une vraie erreur (500), pas « reconstruit, reposez la question » (409).
        var index = new CountingIndex();
        Assert.Throws<ArgumentException>(() => new SearchPassages(new ShortVectorEmbedder(), index).Execute(Fakes.Alice, "télétravail", 4));
        Assert.Equal(1, index.Searches);
    }

    /// <summary>
    /// Pendant la recherche, un autre processus remplace l'index (B), puis l'index d'origine revient
    /// (A, même identifiant) : relire le manifeste après la recherche ne verrait rien.
    /// </summary>
    private sealed class ReplacedThenRestored : IVectorIndex
    {
        private readonly FakeIndex _index = new();
        private readonly (IndexManifest Manifest, Chunk[] Chunks, double[][] Vectors) _original;
        private bool _replaced;

        public ReplacedThenRestored()
        {
            Reindex("teletravail", "Deux jours de télétravail par semaine.");
            _original = (_index.Manifest()!, _index.Chunks.ToArray(), _index.Vectors.ToArray());
        }

        private void Reindex(string id, string text) =>
            new IndexCorpus(new ListSource(Fakes.Doc(id, text)), new WholeDocumentSplitter(), new KeywordEmbedder(), _index, new FixedClock()).Execute();

        public IndexManifest? Manifest() => _index.Manifest();

        public void Replace(IndexManifest manifest, IReadOnlyList<Chunk> chunks, IReadOnlyList<double[]> vectors) =>
            _index.Replace(manifest, chunks, vectors);

        public IReadOnlyList<Passage> Search(double[] vector, int topK, Func<Chunk, bool> predicate, string? indexId = null)
        {
            if (_replaced)
            {
                return _index.Search(vector, topK, predicate, indexId);
            }
            _replaced = true;
            Reindex("autre", "Télétravail : la règle de l'autre index.");   // B
            try
            {
                return _index.Search(vector, topK, predicate, indexId);
            }
            finally
            {
                _index.Replace(_original.Manifest, _original.Chunks, _original.Vectors);   // A revient
            }
        }
    }

    private sealed class CountingIndex : Assistant.Infrastructure.InMemoryVectorIndex
    {
        public int Searches { get; private set; }

        public CountingIndex()
        {
            var ready = Build.Indexed();
            base.Replace(ready.Manifest()!, ready.Chunks, ready.Vectors);
        }

        public override IReadOnlyList<Passage> Search(double[] vector, int topK, Func<Chunk, bool> predicate, string? indexId = null)
        {
            Searches++;
            return base.Search(vector, topK, predicate, indexId);
        }
    }

    /// <summary>Annonce le bon modèle et la bonne dimension, mais renvoie un vecteur trop court (adaptateur défaillant).</summary>
    private sealed class ShortVectorEmbedder : IEmbedder
    {
        private readonly KeywordEmbedder _inner = new();

        public EmbeddingBatch EmbedDocuments(IReadOnlyList<string> texts) => _inner.EmbedDocuments(texts);

        public EmbeddingBatch EmbedQuery(string text)
        {
            var batch = _inner.EmbedQuery(text);
            return batch with { Vectors = new[] { batch.Vectors[0][..^1] } };
        }
    }
}
