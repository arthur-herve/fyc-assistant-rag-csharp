// Tests déterministes du cas d'usage IndexCorpus : aucun modèle, aucun réseau.

using Assistant.Application;
using Assistant.Domain;
using Xunit;

namespace Assistant.Tests;

public class IndexCorpusTests
{
    [Fact]
    public void Manifest_records_what_is_needed_to_explain_the_index()
    {
        var index = new FakeIndex();
        var manifest = new IndexCorpus(new ListSource(Fakes.Doc("a", "télétravail"), Fakes.Doc("b", "frais")),
                                       new WholeDocumentSplitter(), new KeywordEmbedder(), index, new FixedClock()).Execute();
        Assert.Equal("fake-keywords", manifest.EmbeddingModel);
        Assert.Equal(2, manifest.ChunkCount);
        Assert.Equal("whole", manifest.Splitter["type"]);
        Assert.Equal("2026-09-11T12:00:00+00:00", manifest.CreatedAt);
        Assert.Same(manifest, index.Manifest());
    }

    [Fact]
    public void Fingerprint_changes_with_access_rights()
    {
        var pub = Fingerprints.Corpus(new[] { Fakes.Doc("a", "texte") });
        var restricted = Fingerprints.Corpus(new[] { Fakes.Doc("a", "texte", "rh") });
        Assert.NotEqual(pub, restricted);
    }

    [Fact]
    public void Fingerprint_separates_the_fields_and_keeps_its_value()
    {
        var tous = new HashSet<string> { "tous" };
        Assert.NotEqual(Fingerprints.Corpus(new[] { new Document("a", "t", "bc", tous) }),
                        Fingerprints.Corpus(new[] { new Document("a", "tb", "c", tous) }));
        var documents = new[]
        {
            new Document("b", "Congés", "Vingt-cinq jours.", tous),
            new Document("a", "Grille", "Salaire senior.", new HashSet<string> { "rh", "direction" }),
        };
        // Valeur figée : l'empreinte est inscrite dans chaque index, et changer la formule ferait voir « corpus modifié »
        // à tous les index déjà construits.
        Assert.Equal("ec3df17f9b790082ea28ee7ca48f688a23e23a893f3bce5ce89c61e9d9798db0", Fingerprints.Corpus(documents));
    }

    [Fact]
    public void Index_id_changes_with_the_corpus_the_model_the_dimension_or_the_splitter_not_with_the_key_order()
    {
        var splitter = new Dictionary<string, object> { ["type"] = "paragraph", ["max_chars"] = 800, ["overlap_chars"] = 120, ["include_title"] = true };
        var reordered = new Dictionary<string, object> { ["include_title"] = true, ["overlap_chars"] = 120, ["max_chars"] = 800, ["type"] = "paragraph" };
        var id = Fingerprints.IndexId("fp", "m", 64, splitter);
        Assert.Equal(id, Fingerprints.IndexId("fp", "m", 64, reordered));   // mêmes valeurs, autre ordre des clés
        var changed = new[]
        {
            Fingerprints.IndexId("fp2", "m", 64, splitter),   // corpus
            Fingerprints.IndexId("fp", "m2", 64, splitter),   // modèle
            Fingerprints.IndexId("fp", "m", 65, splitter),    // dimension
            Fingerprints.IndexId("fp", "m", 64, new Dictionary<string, object>(splitter) { ["max_chars"] = 300 }),   // découpage
        };
        Assert.Equal(5, changed.Append(id).Distinct().Count());
    }

    [Fact]
    public void Indexing_computes_the_id_from_the_corpus_the_model_the_dimension_and_the_splitter()
    {
        // L'indexation se sert de Fingerprints.IndexId, et deux indexations du même corpus donnent le même identifiant.
        static IndexManifest Run() => new IndexCorpus(new ListSource(Fakes.Doc("a", "télétravail")), new WholeDocumentSplitter(),
                                                      new KeywordEmbedder(), new FakeIndex(), new FixedClock()).Execute();
        var manifest = Run();
        Assert.Equal(Fingerprints.IndexId(manifest.CorpusFingerprint, manifest.EmbeddingModel, manifest.Dimension, manifest.Splitter),
                     manifest.IndexId);
        Assert.Equal(manifest.IndexId, Run().IndexId);
    }

    [Fact]
    public void Index_id_keeps_its_value()
    {
        // Valeur figée : l'identifiant est inscrit dans chaque index. Il est l'empreinte d'un JSON qui ne doit changer en
        // rien, ni l'ordre des champs, ni le tri des clés, ni les espaces ou les échappements : sinon, tous les index déjà
        // construits seraient vus « à refaire ». Le second modèle a la forme d'un identifiant renvoyé par le service IA,
        // avec un « + » que System.Text.Json échappe.
        var splitter = new Dictionary<string, object> { ["type"] = "paragraph", ["max_chars"] = 800, ["overlap_chars"] = 120, ["include_title"] = true };
        Assert.Equal("e3ac097f2b1d", Fingerprints.IndexId("fp", "m", 64, splitter));
        Assert.Equal("c6ce05776677", Fingerprints.IndexId("fp", "ollama:nomic-embed-text@0a109f422b47+prefixes-03aa22a9", 768, splitter));
    }

    [Fact]
    public void A_model_change_between_two_batches_is_refused()
    {
        // Le service change de modèle au milieu d'une indexation : on refuse un index mélangé.
        var embedder = new SwitchingEmbedder();
        var use = new IndexCorpus(new ListSource(Fakes.Doc("a", "télétravail"), Fakes.Doc("b", "frais"), Fakes.Doc("c", "congés")),
                                  new WholeDocumentSplitter(), embedder, new FakeIndex(), new FixedClock(), batchSize: 2);
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
            new IndexCorpus(new ListSource(), new WholeDocumentSplitter(), new KeywordEmbedder(), new FakeIndex(), new FixedClock()).Execute());
}
