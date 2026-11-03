// Les calculs du banc d'essai sont déterministes et se testent sans modèle : lignes CSV, synthèse, latences et
// seuil suggéré.

using System.Text.Json.Nodes;
using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

public class BenchmarkTests
{
    [Fact]
    public void Csv_lines_quote_commas_quotes_and_line_breaks_as_in_rfc_4180()
    {
        var row = new BenchmarkRow("e", "g", 1, "q", true, "answered", 100, Attempts: 1, GenerationModelId: "m", CitedDocuments: "d",
                                   SourceHit: true, KeywordCoverage: 0.5, Text: "Réponse, \"citée\"\nsuite");
        // CsvLine rend la ligne sans sa fin. Virgules et guillemets sont protégés, « sans objet » donne un champ vide.
        Assert.Equal("e,g,1,q,True,answered,1,100,m,d,True,False,0.5,,\"Réponse, \"\"citée\"\"\nsuite\"", row.CsvLine());
        // 1.0 s'écrit 1 (format invariant). Un retour chariot se met entre guillemets, comme un saut de ligne.
        var whole = new BenchmarkRow("e", "g", 2, "q", false, "no_relevant_source", 3, Attempts: 0, KeywordCoverage: 1.0, Text: "a\rb");
        Assert.Equal("e,g,2,q,False,no_relevant_source,0,3,,,,False,1,,\"a\rb\"", whole.CsvLine());
    }

    [Fact]
    public void Csv_columns_are_those_of_the_reference_results()
    {
        // Mêmes colonnes, dans le même ordre, que les résultats de référence.
        var references = Directory.GetFiles(Path.Combine(AppConfig.ProjectRoot, "eval", "resultats"), "resultats.csv", SearchOption.AllDirectories);
        Assert.NotEmpty(references);
        Assert.All(references, path => Assert.Equal(File.ReadLines(path).First().Split(','), BenchmarkRow.CsvFields));
    }

    /// <summary>Une réponse générée en 900 ms, en une tentative, et deux refus de 2 et 3 ms.</summary>
    private static readonly List<BenchmarkRow> Rows = new()
    {
        new("e", "g", 1, "q1", true, "answered", 900, Attempts: 1),
        new("e", "g", 1, "q2", false, "no_relevant_source", 2, Attempts: 0),   // refus sans appel au modèle
        new("e", "g", 1, "q3", false, "no_relevant_source", 3, Attempts: 0),
    };

    [Fact]
    public void Latency_is_measured_on_generated_answers_only()
    {
        var summary = Benchmark.Summarize(Rows).Single();
        Assert.Equal(900.0, summary.LatencyMedianMs);
        Assert.Equal(900.0, summary.LatencyP90Ms);
    }

    [Fact]
    public void Mean_attempts_leave_out_refusals() => Assert.Equal(1.0, Benchmark.Summarize(Rows).Single().MeanAttempts);

    [Fact]
    public void Refusals_without_generation_are_counted() => Assert.Equal(2, Benchmark.Summarize(Rows).Single().RefusalsWithoutGeneration);

    [Fact]
    public void Synthesis_is_written_under_fixed_keys_in_a_fixed_order()
    {
        // Les clés de synthese.json sont fixes, dans un ordre fixe, et ne dépendent pas des noms des propriétés C#.
        var validation = new ThresholdValidation(16, 0.923, 0.923, 1.0, 0.65);
        var retrieval = new RetrievalSummary("bge-m3", "ollama:bge-m3@7907", 1024, 740, 19.9, 0.969, 0.969, 4, 0.737, 0.535,
                                             0.65, false, 0.635, 1.0, 0.65, validation);
        var generation = new GenerationSummary("bge-m3", "llama3-2-3b", 42, 0, 0.969, 1.0, 0.871, 0.024, 0.9, 0, null, 1.1, 850.0, 1200.0, 3);
        Assert.Equal(new[]
        {
            "embedding", "model_id", "dimension", "chunks", "index_seconds", "hit@1", "hit@k", "top_k", "top1_median_answerable",
            "top1_median_unanswerable", "configured_threshold", "configured_threshold_is_default", "suggested_threshold",
            "separation_accuracy", "threshold_used", "validation",
        }, retrieval.ToJson().Select(kv => kv.Key));
        Assert.Equal("""{"questions":16,"hit@1":0.923,"kept_answerable":0.923,"correct_refusals":1,"threshold":0.65}""",
                     retrieval.ToJson()["validation"]!.ToJsonString());
        Assert.Null((retrieval with { Validation = null }).ToJson()["validation"]);   // sans --validate-with : clé absente
        Assert.False((retrieval with { Validation = null }).ToJson().ContainsKey("validation"));
        Assert.Equal("""{"embedding":"nomic","error":"panne"}""", new IndexingFailure("nomic", "panne").ToJson().ToJsonString());
        Assert.Equal(new[]
        {
            "embedding", "generation", "calls", "errors", "answer_rate", "source_hit_rate", "keyword_coverage", "unsourced_rate",
            "correct_refusal_rate", "forbidden_leaks", "stability", "mean_attempts", "latency_median_ms", "latency_p90_ms",
            "refusals_without_generation",
        }, generation.ToJson().Select(kv => kv.Key));
    }

    [Fact]
    public void Synthesis_writes_not_applicable_as_null()
    {
        // « Sans objet » s'écrit null : la clé reste, avec la valeur null.
        var generation = new GenerationSummary("bge-m3", "llama3-2-3b", 42, 0, 0.969, 1.0, 0.871, 0.024, 0.9, 0, Stability: null, 1.1,
                                               850.0, 1200.0, 3);
        Assert.True(generation.ToJson().TryGetPropertyValue("stability", out var stability));
        Assert.Null(stability);
    }

    [Fact]
    public void Synthesis_is_indented_json_that_reads_back_the_same()
    {
        // Écrit comme la sortie de ask --json (Presenter.ToJson) : indenté. Relu, il redonne les mêmes valeurs,
        // y compris les null de « sans objet ».
        var summary = new BenchmarkSummary(
            new RetrievalResult[]
            {
                new RetrievalSummary("hashing", "hashing:256@abc", 256, 15, 0.3, 1.0, 1.0, 4, 0.5, 0.25, 0.4, true, 0.375, 1.0, 0.4,
                                     new ThresholdValidation(2, 1.0, 1.0, 0.0, 0.4)),
                new IndexingFailure("nomic", "Service IA : HTTP 502 — « modèle absent »"),
            },
            new[] { new GenerationSummary("hashing", "extractive", 4, 0, 1.0, 0.5, null, 0.0, 1.0, 0, null, 1.0, 12.0, 15.0, 2) });
        var written = Presenter.ToJson(summary.ToJson());
        Assert.StartsWith("{" + Environment.NewLine + "  \"retrieval\": [", written);
        Assert.True(JsonNode.DeepEquals(summary.ToJson(), JsonNode.Parse(written)));
    }

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
    public void Median_and_p90_are_taken_from_the_sorted_values()
    {
        Assert.Equal(2.5, Benchmark.Median(new[] { 4.0, 1.0, 3.0, 2.0 }));
        Assert.Equal(3.0, Benchmark.Median(new[] { 3.0, 1.0, 5.0 }));
        Assert.Equal(9.0, Benchmark.P90(Enumerable.Range(0, 10).Select(i => (double)i).ToList()));
    }

    [Fact]
    public void Mean_of_no_values_is_null() => Assert.Null(Benchmark.Mean(Array.Empty<double>()));

    [Fact]
    public void Min_score_is_config_auto_or_a_threshold_of_the_configuration()
    {
        Assert.Null(Benchmark.FixedMinScore("config"));
        Assert.Null(Benchmark.FixedMinScore("auto"));
        Assert.Equal(new double?[] { 0.6, -1.0, 1.0 }, new[] { "0.6", "-1", "1" }.Select(Benchmark.FixedMinScore));
        foreach (var mode in new[] { "abc", "nan", "inf", "5", "-1.5" })
        {
            var error = Assert.Throws<ArgumentException>(() => Benchmark.FixedMinScore(mode));
            Assert.Equal($"--min-score attend config, auto ou un nombre de [-1, 1] (ex. 0.6), pas « {mode} »", error.Message);
        }
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
}
