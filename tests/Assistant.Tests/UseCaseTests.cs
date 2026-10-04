// Tests déterministes des cas d'usage : aucun modèle, aucun réseau.

using System.Text.RegularExpressions;
using Assistant.Application;
using Assistant.Domain;
using Xunit;

namespace Assistant.Tests;

public class AskQuestionTests
{
    [Fact]
    public void Answers_with_cited_sources()
    {
        var generator = new ScriptedGenerator("Deux jours par semaine [1].");
        var answer = Build.Ask(generator).Execute(Fakes.Alice, "Combien de jours de télétravail ?");
        Assert.Equal(AnswerStatus.Answered, answer.Status);
        Assert.Equal(new[] { "teletravail" }, answer.Sources.Select(s => s.DocumentId));
        Assert.Equal("fake-llm", answer.Trace.GenerationModel);
        Assert.Equal("test-v1", answer.Trace.PromptVersion);
    }

    [Fact]
    public void Does_not_call_the_model_without_relevant_passage()
    {
        var generator = new ScriptedGenerator("ne devrait pas être appelé [1]");
        var answer = Build.Ask(generator).Execute(Fakes.Alice, "Quelle est la capitale de l'Australie ?");
        Assert.Equal(AnswerStatus.NoRelevantSource, answer.Status);
        Assert.Empty(generator.Requests);
    }

    [Fact]
    public void Restricted_passages_never_reach_the_prompt()
    {
        var generator = new ScriptedGenerator("[1]");
        Build.Ask(generator, minScore: 0.0).Execute(Fakes.Alice, "Quel salaire pour un senior ?");
        Assert.NotEmpty(generator.Requests);
        Assert.All(generator.Requests, r => Assert.DoesNotContain("56 000", r.Prompt));
    }

    [Fact]
    public void A_citation_points_to_the_passage_it_numbers()
    {
        // [2] désigne le deuxième passage du prompt, pas le premier ni le dernier.
        var generator = new ScriptedGenerator("Le repas est remboursé [2].");
        var answer = Build.Ask(generator, minScore: 0.1).Execute(Fakes.Alice, "télétravail jours frais");
        Assert.Equal(2, Regex.Matches(generator.Requests[0].Prompt, @"^\[\d+\]", RegexOptions.Multiline).Count);
        Assert.Equal(new[] { (2, "frais") }, answer.Sources.Select(s => (s.Number, s.DocumentId)));
    }

    [Fact]
    public void A_huge_or_non_ascii_citation_number_is_invalid_not_a_crash()
    {
        var answer = Build.Ask(new ScriptedGenerator("Appelez le [33612345678].", "Deux jours [1].")).Execute(Fakes.Alice, "Combien de jours de télétravail ?");
        Assert.Equal(AnswerStatus.Answered, answer.Status);
        Assert.Equal(2, answer.Trace.Attempts);
    }

    [Fact]
    public void A_passage_containing_the_placeholder_is_not_substituted_twice()
    {
        var template = new PromptTemplate("t", "v", "s", "P: {passages} Q: {question}");
        Assert.Equal("P: texte {question} Q: réelle ?", template.Render("réelle ?", "texte {question}"));
    }

    [Fact]
    public void Authorized_user_gets_restricted_passages()
    {
        var answer = Build.Ask(new ScriptedGenerator("Entre 56 000 et 68 000 euros [1].")).Execute(Fakes.Bruno, "Quel salaire pour un senior ?");
        Assert.Equal(new[] { "grille" }, answer.Sources.Select(s => s.DocumentId));
    }

    [Fact]
    public void Retries_when_the_model_forgets_to_cite()
    {
        var answer = Build.Ask(new ScriptedGenerator("Deux jours.", "Deux jours [1].")).Execute(Fakes.Alice, "Combien de jours de télétravail ?");
        Assert.Equal(AnswerStatus.Answered, answer.Status);
        Assert.Equal(2, answer.Trace.Attempts);
        Assert.Equal(2, answer.Trace.RawOutputs.Count);
    }

    [Fact]
    public void Refuses_an_unsourced_answer_after_all_attempts()
    {
        var answer = Build.Ask(new ScriptedGenerator("Deux jours.", "Deux jours [9].")).Execute(Fakes.Alice, "Combien de jours de télétravail ?");
        Assert.Equal(AnswerStatus.Unsourced, answer.Status);
        Assert.DoesNotContain("Deux jours", answer.Text);   // la sortie non sourcée n'est pas montrée
        Assert.NotEmpty(answer.Sources);                     // mais les passages le sont
    }

    [Fact]
    public void Passages_are_numbered_in_the_prompt()
    {
        var generator = new ScriptedGenerator("[1]");
        Build.Ask(generator, minScore: 0.0).Execute(Fakes.Alice, "télétravail et frais de repas");
        Assert.Equal(2, Regex.Matches(generator.Requests[0].Prompt, @"^\[\d+\]", RegexOptions.Multiline).Count);
    }

    [Fact]
    public void Changing_the_embedding_model_without_reindexing_is_refused()
    {
        var index = Build.Indexed(new KeywordEmbedder("modele-a"));
        var ask = Build.Ask(new ScriptedGenerator("[1]"), index, new KeywordEmbedder("modele-b"));
        Assert.Throws<IndexModelMismatchException>(() => ask.Execute(Fakes.Alice, "télétravail"));
    }

    [Fact]
    public void Changing_the_generation_model_needs_no_reindexing()
    {
        var index = Build.Indexed();
        foreach (var model in new[] { "llm-a", "llm-b" })
        {
            var answer = Build.Ask(ScriptedGenerator.WithModel(model, "Deux jours [1]."), index).Execute(Fakes.Alice, "jours de télétravail");
            Assert.Equal(model, answer.Trace.GenerationModel);
        }
    }

    [Fact]
    public void Empty_question_is_rejected() =>
        Assert.Throws<EmptyQuestionException>(() => Build.Ask(new ScriptedGenerator("[1]")).Execute(Fakes.Alice, "   "));

    [Fact]
    public void Asking_before_indexing_is_explicit() =>
        Assert.Throws<IndexNotBuiltException>(() => Build.Ask(new ScriptedGenerator("[1]"), new FakeIndex()).Execute(Fakes.Alice, "télétravail"));

    [Fact]
    public void Seed_changes_between_attempts()
    {
        var generator = new ScriptedGenerator("sans citation", "avec [1]");
        Build.Ask(generator, seed: 42).Execute(Fakes.Alice, "jours de télétravail");
        Assert.Equal(new int?[] { 42, 43 }, generator.Requests.Select(r => r.Seed));
    }

    [Fact]
    public void A_rejected_output_counts_as_a_failed_attempt()
    {
        const string leak = "Okay, let's see. The user is asking about remote work. The passage [1] says two days per week.";
        var inner = new ScriptedGenerator(leak, "Deux jours par semaine [1].");
        var answer = Build.Ask(new OutputValidatingGenerator(inner), minScore: 0.1).Execute(Fakes.Alice, "Combien de jours de télétravail ?");
        Assert.Equal(AnswerStatus.Answered, answer.Status);
        Assert.Equal(2, answer.Trace.Attempts);
        Assert.StartsWith("<rejetée : ", answer.Trace.RawOutputs[0]);
    }
}

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
        // Reconstruit par un modèle d'une autre dimension pendant la recherche : la recherche refuse l'index
        // remplacé (indexId, vérifié avant la dimension), SearchPassages recommence, et le contrôle du modèle
        // donne 409 : réindexer, pas 500.
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
        // A → B → A : les passages viennent de l'index annoncé dans la trace, jamais de B.
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
    public void Fingerprint_separates_the_fields_and_matches_the_python_version()
    {
        var tous = new HashSet<string> { "tous" };
        Assert.NotEqual(Fingerprints.Corpus(new[] { new Document("a", "t", "bc", tous) }),
                        Fingerprints.Corpus(new[] { new Document("a", "tb", "c", tous) }));
        var documents = new[]
        {
            new Document("b", "Congés", "Vingt-cinq jours.", tous),
            new Document("a", "Grille", "Salaire senior.", new HashSet<string> { "rh", "direction" }),
        };
        // Même valeur attendue dans test_index_corpus.py : les deux versions calculent la même empreinte.
        Assert.Equal("ec3df17f9b790082ea28ee7ca48f688a23e23a893f3bce5ce89c61e9d9798db0", Fingerprints.Corpus(documents));
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
        Assert.Equal("fake-keywords", report.EmbeddingModel);
    }

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
        // L'ancien et le nouveau découpage, clés triées, true plutôt que True.
        var issue = Status(Build.Indexed(null, Docs), splitter: new OtherSplitter()).Issues[0];
        Assert.Contains("découpage modifié ({type: whole} → ", issue);
        Assert.Contains("{include_title: true, max_chars: 300, type: whole}", issue);
        Assert.Contains("réindexer", issue);
    }

    [Fact]
    public void Detects_that_the_ai_service_now_serves_another_model() =>
        Assert.Contains("fake-keywords-v2", Status(Build.Indexed(null, Docs), embedder: new KeywordEmbedder("fake-keywords-v2")).Issues[0]);

    [Fact]
    public void Unreachable_ai_service_is_unverified_not_fatal()
    {
        var report = Status(Build.Indexed(null, Docs), embedder: new BrokenEmbedder());
        Assert.True(report.Unverified);
        Assert.False(report.UpToDate);
        Assert.NotNull(report.Index);
    }
}

public class SnapshotTests
{
    private static readonly SnapshotQuestion[] Questions =
    {
        new("tt", Fakes.Alice, "Combien de jours de télétravail ?"),
        new("frais", Fakes.Alice, "Quel plafond pour les frais de repas ?"),
        new("hors", Fakes.Alice, "Quelle est la capitale de l'Australie ?"),
    };

    private static (Snapshot, MemorySnapshotStore) Record(string name, ScriptedGenerator generator, double minScore = 0.5)
    {
        var store = new MemorySnapshotStore();
        var ask = Build.Ask(generator, Build.Indexed(null,
            Fakes.Doc("teletravail", "Deux jours de télétravail par semaine."), Fakes.Doc("frais", "Les frais de repas sont plafonnés.")),
            minScore: minScore, maxAttempts: 1);
        var snapshot = new RecordSnapshot(ask, store, new FixedClock(), new Dictionary<string, object?> { ["generation_model"] = generator.Model })
            .Execute(name, Questions);
        return (snapshot, store);
    }

    [Fact]
    public void Records_answers_refusals_and_configuration()
    {
        var (snapshot, store) = Record("ref", ScriptedGenerator.WithModel("llm-a", "Deux jours [1]."));
        Assert.Equal("2026-09-11T12:00:00+00:00", snapshot.CreatedAt);
        Assert.Equal(new[] { "answered", "answered", "no_relevant_source" }, snapshot.Entries.Select(e => e.Status));
        Assert.Equal("llm-a", snapshot.Configuration["generation_model_id"]);
        Assert.True(snapshot.Configuration.ContainsKey("index_id"));
        Assert.Same(snapshot, store.Load("ref"));
    }

    [Fact]
    public void Invalid_name_is_refused_before_any_question()
    {
        var generator = new ScriptedGenerator("Deux jours [1].");
        var record = new RecordSnapshot(Build.Ask(generator), new MemorySnapshotStore(), new FixedClock(), new Dictionary<string, object?>());
        Assert.Throws<InvalidSnapshotNameException>(() => record.Execute("../evil", Questions));
        Assert.Empty(generator.Requests);
    }

    [Fact]
    public void Same_configuration_gives_zero_drift()
    {
        var (a, _) = Record("a", new ScriptedGenerator("Deux jours [1]."));
        var (b, _) = Record("b", new ScriptedGenerator("Deux jours [1]."));
        var comparison = SnapshotComparer.Compare(a, b);
        Assert.Empty(comparison.ConfigurationDifferences);
        Assert.Equal(0.0, comparison.DriftRate);
    }

    [Fact]
    public void Changing_the_generator_is_visible_in_configuration_and_text()
    {
        var (a, _) = Record("a", ScriptedGenerator.WithModel("llm-a", "Deux jours [1]."));
        var (b, _) = Record("b", ScriptedGenerator.WithModel("llm-b", "Vous avez droit à deux jours [1]."));
        var comparison = SnapshotComparer.Compare(a, b);
        Assert.Equal(new[] { "generation_model", "generation_model_id" }, comparison.ConfigurationDifferences.Select(d => d.Key));
        Assert.Equal(2, comparison.Count(DifferenceKind.TextChanged));
        Assert.Equal(1, comparison.Count(DifferenceKind.Identical));
    }

    [Fact]
    public void A_refusal_that_becomes_an_answer_is_a_status_change()
    {
        var (a, _) = Record("a", new ScriptedGenerator("Deux jours [1]."), minScore: 0.5);
        var (b, _) = Record("b", new ScriptedGenerator("Deux jours [1]."), minScore: 0.0);
        Assert.Equal(1, SnapshotComparer.Compare(a, b).Count(DifferenceKind.StatusChanged));
    }

    [Fact]
    public void A_separator_at_the_end_of_a_text_is_a_change_a_blank_is_not()
    {
        // Les textes se comparent rognés par Trim() : U+2028 est un blanc, U+001C n'en est pas un (char.IsWhiteSpace).
        static Snapshot With(string name, string text) => new(name, "t", new Dictionary<string, object?>(),
            new[] { new SnapshotEntry("q", "alice", "?", "answered", new[] { "a" }, text) });
        var baseline = With("a", "Deux jours [1].");
        Assert.Equal(DifferenceKind.TextChanged, SnapshotComparer.Compare(baseline, With("b", "Deux jours [1].\u001c")).Differences[0].Kind);
        Assert.Equal(DifferenceKind.Identical, SnapshotComparer.Compare(baseline, With("b", "Deux jours [1].\u2028")).Differences[0].Kind);
    }

    [Fact]
    public void Configuration_values_are_compared_by_value_not_by_type()
    {
        var a = new Snapshot("a", "t", new Dictionary<string, object?> { ["top_k"] = 4, ["seed"] = null, ["splitter"] = new Dictionary<string, object> { ["max_chars"] = 800 } }, Array.Empty<SnapshotEntry>());
        var b = new Snapshot("b", "t", new Dictionary<string, object?> { ["top_k"] = 4.0, ["seed"] = null, ["splitter"] = new Dictionary<string, object> { ["max_chars"] = 800.0 } }, Array.Empty<SnapshotEntry>());
        Assert.Empty(SnapshotComparer.Compare(a, b).ConfigurationDifferences);
        var c = b with { Configuration = new Dictionary<string, object?> { ["top_k"] = 5, ["seed"] = null, ["splitter"] = new Dictionary<string, object> { ["max_chars"] = 800 } } };
        Assert.Equal(new[] { "top_k" }, SnapshotComparer.Compare(a, c).ConfigurationDifferences.Select(d => d.Key));
    }

    [Fact]
    public void Missing_questions_are_reported_but_not_counted_as_drift()
    {
        var a = new Snapshot("a", "t", new Dictionary<string, object?>(), new[] { new SnapshotEntry("q1", "alice", "?", "answered", new[] { "a" }, "x") });
        var b = new Snapshot("b", "t", new Dictionary<string, object?>(), new[] { new SnapshotEntry("q2", "alice", "?", "answered", new[] { "a" }, "x") });
        var comparison = SnapshotComparer.Compare(a, b);
        Assert.Equal(2, comparison.Count(DifferenceKind.Missing));
        Assert.Null(comparison.DriftRate);
    }
}
