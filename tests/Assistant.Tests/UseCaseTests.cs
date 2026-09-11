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
        Assert.All(generator.Requests, r => Assert.DoesNotContain("56 000", r.Prompt));
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
        public IReadOnlyDictionary<string, object> Describe() => new Dictionary<string, object> { ["type"] = "whole", ["max_chars"] = 300 };
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
    public void Detects_a_changed_splitter() =>
        Assert.Contains("découpage modifié", Status(Build.Indexed(null, Docs), splitter: new OtherSplitter()).Issues[0]);

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
        var snapshot = new RecordSnapshot(ask, store, new FixedClock(), new Dictionary<string, string> { ["generation_model"] = generator.Model })
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
        var record = new RecordSnapshot(Build.Ask(generator), new MemorySnapshotStore(), new FixedClock(), new Dictionary<string, string>());
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
    public void A_refusal_that_becomes_an_answer_is_the_gravest_kind()
    {
        var (a, _) = Record("a", new ScriptedGenerator("Deux jours [1]."), minScore: 0.5);
        var (b, _) = Record("b", new ScriptedGenerator("Deux jours [1]."), minScore: 0.0);
        Assert.Equal(1, SnapshotComparer.Compare(a, b).Count(DifferenceKind.StatusChanged));
    }

    [Fact]
    public void Missing_questions_are_reported_but_not_counted_as_drift()
    {
        var a = new Snapshot("a", "t", new Dictionary<string, string>(), new[] { new SnapshotEntry("q1", "alice", "?", "answered", new[] { "a" }, "x") });
        var b = new Snapshot("b", "t", new Dictionary<string, string>(), new[] { new SnapshotEntry("q2", "alice", "?", "answered", new[] { "a" }, "x") });
        var comparison = SnapshotComparer.Compare(a, b);
        Assert.Equal(2, comparison.Count(DifferenceKind.Missing));
        Assert.Null(comparison.DriftRate);
    }
}
