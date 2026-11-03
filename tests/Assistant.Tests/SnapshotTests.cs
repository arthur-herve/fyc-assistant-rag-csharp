// Tests déterministes des instantanés, enregistrés puis comparés : aucun modèle, aucun réseau.

using Assistant.Application;
using Xunit;

namespace Assistant.Tests;

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
        var ask = Build.Ask(generator, Build.Indexed(
            Fakes.Doc("teletravail", "Deux jours de télétravail par semaine."), Fakes.Doc("frais", "Les frais de repas sont plafonnés.")),
            minScore: minScore);
        var snapshot = new RecordSnapshot(ask, store, new FixedClock(), new Dictionary<string, object?> { ["generation_model"] = generator.Model })
            .Execute(name, Questions);
        return (snapshot, store);
    }

    [Fact]
    public void Records_answers_refusals_and_configuration()
    {
        var (snapshot, store) = Record("ref", ScriptedGenerator.WithModel("llm-a", "Deux jours [1]."));
        Assert.Equal(new[] { "answered", "answered", "no_relevant_source" }, snapshot.Entries.Select(e => e.Status));
        Assert.Equal("llm-a", snapshot.Configuration["generation_model"]);
        Assert.Same(snapshot, store.Load("ref"));
    }

    [Fact]
    public void A_snapshot_is_dated_by_the_clock()
    {
        var (snapshot, _) = Record("ref", new ScriptedGenerator("Deux jours [1]."));
        Assert.Equal("2026-09-11T12:00:00+00:00", snapshot.CreatedAt);   // l'heure de FixedClock
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
        Assert.Equal(new[] { "generation_model" }, comparison.ConfigurationDifferences.Select(d => d.Key));
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
        // Les textes sont comparés après Trim(). Pour char.IsWhiteSpace, U+2028 est un blanc, U+001C n'en est pas un.
        static Snapshot With(string name, string text) => new(name, "t", new Dictionary<string, object?>(),
            new[] { new SnapshotEntry("q", "alice", "?", "answered", new[] { "a" }, text) });
        var baseline = With("a", "Deux jours [1].");
        Assert.Equal(DifferenceKind.TextChanged, SnapshotComparer.Compare(baseline, With("b", "Deux jours [1].\u001c")).Differences[0].Kind);
        Assert.Equal(DifferenceKind.Identical, SnapshotComparer.Compare(baseline, With("b", "Deux jours [1].\u2028")).Differences[0].Kind);
    }

    [Fact]
    public void Configuration_values_are_compared_by_value_not_by_type()
    {
        var a = new Snapshot("a", "t", new Dictionary<string, object?> { ["top_k"] = 4, ["splitter"] = new Dictionary<string, object> { ["max_chars"] = 800 } }, Array.Empty<SnapshotEntry>());
        var b = new Snapshot("b", "t", new Dictionary<string, object?> { ["top_k"] = 4.0, ["splitter"] = new Dictionary<string, object> { ["max_chars"] = 800.0 } }, Array.Empty<SnapshotEntry>());
        Assert.Empty(SnapshotComparer.Compare(a, b).ConfigurationDifferences);
        var c = b with { Configuration = new Dictionary<string, object?> { ["top_k"] = 5, ["splitter"] = new Dictionary<string, object> { ["max_chars"] = 800 } } };
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
