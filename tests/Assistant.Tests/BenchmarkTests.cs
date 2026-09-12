// Le banc d'essai et les expériences se testent sans modèle : leurs calculs sont déterministes.

using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

public class BenchmarkTests
{
    [Fact]
    public void Suggested_threshold_separates_the_two_populations()
    {
        var (threshold, separation) = Benchmark.SuggestThreshold(new[] { 0.8, 0.7, 0.65 }, new[] { 0.4, 0.5 });
        Assert.NotNull(threshold);
        Assert.InRange(threshold!.Value, 0.5, 0.65);
        Assert.Equal(1.0, separation);
    }

    [Fact]
    public void Suggested_threshold_needs_both_populations() =>
        Assert.Equal((null, null), Benchmark.SuggestThreshold(new[] { 0.8 }, Array.Empty<double>()));

    [Fact]
    public void Keyword_coverage_ignores_case_and_accents()
    {
        Assert.Equal(1.0, Benchmark.KeywordCoverage("Deux jours de TÉLÉTRAVAIL par semaine.", new[] { "deux jours", "teletravail" }));
        Assert.Equal(0.5, Benchmark.KeywordCoverage("Deux jours.", new[] { "deux jours", "30 euros" }));
        Assert.Null(Benchmark.KeywordCoverage("Deux jours.", Array.Empty<string>()));
    }

    [Fact]
    public void Median_and_p90_match_the_python_definitions()
    {
        Assert.Equal(2.5, Benchmark.Median(new[] { 4.0, 1.0, 3.0, 2.0 }));
        Assert.Equal(3.0, Benchmark.Median(new[] { 3.0, 1.0, 5.0 }));
        Assert.Equal(9.0, Benchmark.P90(Enumerable.Range(0, 10).Select(i => (double)i).ToList()));
        Assert.Null(Benchmark.Mean(Array.Empty<double>()));
    }

    [Fact]
    public void Evaluation_questions_load_with_their_expectations()
    {
        var questions = EvalQuestions.Load(Path.Combine(AppConfig.ProjectRoot, "eval", "questions.json"));
        Assert.True(questions.Count >= 20);
        Assert.Contains(questions, q => !q.Answerable);
        Assert.Contains(questions, q => q.ForbiddenDocuments.Count > 0);
        Assert.All(questions.Where(q => q.Answerable), q => Assert.NotEmpty(q.ExpectedDocuments));
    }

    [Fact]
    public void Multi_valued_options_stop_at_the_next_option()
    {
        var args = new Args(new[] { "benchmark", "--embedding", "hashing", "nomic", "--generation", "extractive", "--runs", "2", "--json" });
        Assert.Equal(new[] { "hashing", "nomic" }, args.Values("--embedding"));
        Assert.Equal(new[] { "extractive" }, args.Values("--generation"));
        Assert.Equal("2", args.Value("--runs"));
        Assert.True(args.Has("--json"));
        Assert.Equal(new[] { "benchmark" }, args.Positional);
    }

    [Fact]
    public void Experiment_statistics_read_what_the_questions_expect()
    {
        var questions = new List<EvalQuestion>
        {
            new("q1", "alice", "Q1", new[] { "teletravail" }, Array.Empty<string>(), true, Array.Empty<string>()),
            new("q2", "alice", "Q2", Array.Empty<string>(), Array.Empty<string>(), false, Array.Empty<string>()),
            new("q3", "alice", "Q3", new[] { "frais" }, Array.Empty<string>(), true, new[] { "grille" }),
        };
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var config = AppConfig.Load();
            var exp = new Experiment("test", config, questions, dir.FullName, "questions", "config", _ => { });
            var snapshot = new Application.Snapshot("s", "2026-09-12T00:00:00+00:00", new Dictionary<string, object?>(), new[]
            {
                new Application.SnapshotEntry("q1", "alice", "Q1", "answered", new[] { "teletravail" }, "Deux jours [1]", 1),
                new Application.SnapshotEntry("q2", "alice", "Q2", "no_relevant_source", Array.Empty<string>(), "…", 0),
                new Application.SnapshotEntry("q3", "alice", "Q3", "answered", new[] { "grille" }, "Fuite [1]", 1),
            });
            var stats = exp.Stats(snapshot);
            Assert.Equal(1.0, stats["répond (répondables)"]);
            Assert.Equal(0.5, stats["bonne source"]);
            Assert.Equal(1.0, stats["refus justes (hors corpus)"]);
            Assert.Equal(0.0, stats["non sourcé"]);
            Assert.Equal(1.0, stats["fuites d'accès"]);   // q3 cite un document interdit : le banc doit le voir
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
