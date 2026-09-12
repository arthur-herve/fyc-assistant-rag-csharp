// Banc d'essai : mesurer, pas asserter (séquences 3.1 et 3.2).
//
// Deux mesures séparées, parce que deux composants séparés :
// 1. la recherche seule (déterministe à modèle d'embeddings fixé) : hit@1, hit@k, seuil
//    de pertinence suggéré, puis éprouvé sur un jeu de questions jamais vues ;
// 2. la génération (probabiliste) sur N passages : taux de réponse, bonne source, refus
//    justes, fuites d'accès (toujours 0 : les droits sont filtrés avant le modèle),
//    stabilité d'un passage à l'autre, latences.
// Mêmes métriques, mêmes fichiers (resultats.csv, synthese.json, rapport.md) que le
// banc de la version Python : les rapports des deux dépôts se lisent côte à côte.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Application;
using Assistant.Domain;

namespace Assistant.Cli;

public sealed record BenchmarkOptions(
    IReadOnlyList<string> EmbeddingModels,
    IReadOnlyList<string> GenerationModels,
    IReadOnlyList<EvalQuestion> Questions,
    IReadOnlyList<EvalQuestion>? Validation,
    int Runs,
    string OutDir,
    string MinScoreMode = "config",
    int? SplitterMaxChars = null,
    int? SplitterOverlapChars = null,
    int? Seed = null,
    string? PromptName = null);

public sealed record RetrievalScore(EvalQuestion Question, double Top1, bool? Hit, bool? Hit1);

public static class Benchmark
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonObject Run(AppConfig config, BenchmarkOptions options, Action<string> log)
    {
        Directory.CreateDirectory(options.OutDir);
        var retrieval = new List<JsonObject>();
        var rows = new List<JsonObject>();

        foreach (var emb in options.EmbeddingModels)
        {
            log($"\n=== Embeddings : {emb} ===");
            var indexPath = Path.Combine(options.OutDir, $"index-{emb}.json");
            var overrides = new Overrides(EmbeddingModel: emb, IndexPath: indexPath, PromptName: options.PromptName,
                                          SplitterMaxChars: options.SplitterMaxChars, SplitterOverlapChars: options.SplitterOverlapChars,
                                          Seed: options.Seed);
            var container = Composition.Build(config, overrides);
            var clock = Stopwatch.StartNew();
            IndexManifest manifest;
            try
            {
                manifest = container.IndexCorpus.Execute();
            }
            catch (AssistantApplicationException error)
            {
                log($"  ÉCHEC de l'indexation : {error.Message}");
                retrieval.Add(new JsonObject { ["embedding"] = emb, ["error"] = error.Message });
                continue;
            }
            var indexSeconds = Math.Round(clock.Elapsed.TotalSeconds, 1);
            log($"  {manifest.ChunkCount} morceaux indexés en {indexSeconds} s ({manifest.EmbeddingModel}, {manifest.Dimension} dim.)");

            var topK = container.Settings.TopK;
            var scored = RetrievalScores(container, config, options.Questions);
            var hits = scored.Where(r => r.Question.Answerable && r.Hit is not null).Select(r => r.Hit == true ? 1.0 : 0.0).ToList();
            var hits1 = scored.Where(r => r.Question.Answerable && r.Hit1 is not null).Select(r => r.Hit1 == true ? 1.0 : 0.0).ToList();
            var answerable = scored.Where(r => r.Question.Answerable).Select(r => r.Top1).ToList();
            var unanswerable = scored.Where(r => !r.Question.Answerable).Select(r => r.Top1).ToList();

            var (suggested, separation) = SuggestThreshold(answerable, unanswerable);
            var configured = config.MinScoreFor(emb);
            var used = options.MinScoreMode switch
            {
                "auto" => suggested ?? configured,
                "config" => configured,
                var value => double.Parse(value, CultureInfo.InvariantCulture),
            };
            var summary = new JsonObject
            {
                ["embedding"] = emb,
                ["model_id"] = manifest.EmbeddingModel,
                ["dimension"] = manifest.Dimension,
                ["chunks"] = manifest.ChunkCount,
                ["index_seconds"] = indexSeconds,
                ["hit@1"] = Mean(hits1),
                ["hit@k"] = Mean(hits),
                ["top_k"] = topK,
                ["top1_median_answerable"] = Median(answerable),
                ["top1_median_unanswerable"] = Median(unanswerable),
                ["configured_threshold"] = configured,
                ["suggested_threshold"] = suggested,
                ["separation_accuracy"] = separation,
                ["threshold_used"] = used,
            };
            retrieval.Add(summary);
            log($"  hit@1={F(Mean(hits1))} · hit@{topK}={F(Mean(hits))} · seuil configuré={configured} · seuil suggéré={F(suggested)} · seuil utilisé={used}");

            if (options.Validation is { Count: > 0 } validation)
            {
                // Le seuil retenu, éprouvé sur des questions qu'il n'a pas vues.
                var checked_ = ValidateThreshold(RetrievalScores(container, config, validation), used);
                summary["validation"] = checked_;
                log($"  validation ({checked_["questions"]} questions jamais vues, seuil {used}) : hit@1={F(checked_["hit@1"])} · "
                    + $"répondables retenues={F(checked_["kept_answerable"])} · refus justes={F(checked_["correct_refusals"])}");
            }

            foreach (var gen in options.GenerationModels)
            {
                log($"  --- Génération : {gen} ({options.Runs} passage(s)) ---");
                var ask = Composition.Build(config, overrides with { GenerationModel = gen, MinScore = used }).AskQuestion;
                for (var run = 1; run <= options.Runs; run++)
                {
                    foreach (var q in options.Questions)
                    {
                        var row = new JsonObject
                        {
                            ["embedding"] = emb, ["generation"] = gen, ["run"] = run,
                            ["question_id"] = q.Id, ["answerable"] = q.Answerable,
                        };
                        var started = Stopwatch.StartNew();
                        Answer answer;
                        try
                        {
                            answer = ask.Execute(config.User(q.UserName), q.Question);
                        }
                        catch (AssistantApplicationException error)
                        {
                            row["status"] = "error";
                            row["error"] = error.Message;
                            row["latency_ms"] = (int)started.ElapsedMilliseconds;
                            rows.Add(row);
                            log($"    {q.Id} : erreur — {error.Message}");
                            continue;
                        }
                        var cited = answer.Sources.Select(s => s.DocumentId).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
                        var answered = answer.Status == AnswerStatus.Answered;
                        row["status"] = StatusNames.Of(answer.Status);
                        row["latency_ms"] = (int)started.ElapsedMilliseconds;
                        row["attempts"] = answer.Trace.Attempts;
                        row["generation_model_id"] = answer.Trace.GenerationModel ?? "";
                        row["cited_documents"] = string.Join("|", cited);
                        row["source_hit"] = answered && q.ExpectedDocuments.Count > 0 ? cited.Intersect(q.ExpectedDocuments).Any() : null;
                        row["forbidden_leak"] = cited.Intersect(q.ForbiddenDocuments).Any();
                        row["keyword_coverage"] = answered ? KeywordCoverage(answer.Text, q.ExpectedKeywords) : null;
                        row["text"] = answer.Text.Replace("\n", " ");
                        rows.Add(row);
                    }
                    log($"    passage {run}/{options.Runs} terminé");
                }
            }
        }

        var result = new JsonObject { ["retrieval"] = new JsonArray(retrieval.ToArray<JsonNode>()), ["generation"] = Summarize(rows) };
        WriteCsv(rows, Path.Combine(options.OutDir, "resultats.csv"));
        File.WriteAllText(Path.Combine(options.OutDir, "synthese.json"), result.ToJsonString(Json) + "\n", new UTF8Encoding(false));
        var promptVersion = Composition.Build(config, new Overrides()).Prompts.Get(options.PromptName ?? config.PromptName).Version;
        var splitter = new Dictionary<string, object>
        {
            ["max_chars"] = options.SplitterMaxChars ?? config.SplitterMaxChars,
            ["overlap_chars"] = options.SplitterOverlapChars ?? config.SplitterOverlapChars,
            ["include_title"] = config.SplitterIncludeTitle,
        };
        File.WriteAllText(Path.Combine(options.OutDir, "rapport.md"),
            Report(result, options.Runs, options.Questions.Count, promptVersion, splitter, config), new UTF8Encoding(false));
        log($"\nRapport : {Path.Combine(options.OutDir, "rapport.md")}");
        return result;
    }

    /// <summary>Score top-1 et succès de la recherche pour chaque question (déterministe), via le cas d'usage SearchPassages.</summary>
    public static List<RetrievalScore> RetrievalScores(Container container, AppConfig config, IReadOnlyList<EvalQuestion> questions)
    {
        var rows = new List<RetrievalScore>();
        foreach (var q in questions)
        {
            var (_, passages) = container.SearchPassages.Execute(config.User(q.UserName), q.Question, container.Settings.TopK);
            var top1 = passages.Count > 0 ? passages[0].Score : 0.0;
            var found = passages.Select(p => p.Chunk.DocumentId).ToHashSet();
            var first = passages.Count > 0 ? passages[0].Chunk.DocumentId : null;
            var expected = q.ExpectedDocuments;
            rows.Add(new RetrievalScore(q, top1,
                expected.Count > 0 ? found.Overlaps(expected) : null,
                expected.Count > 0 ? first is not null && expected.Contains(first) : null));
        }
        return rows;
    }

    /// <summary>
    /// Seuil qui sépare le mieux les scores des questions répondables de ceux des questions
    /// hors corpus, et sa justesse. Calibré sur ces questions mêmes : il est optimiste, d'où
    /// le jeu de validation.
    /// </summary>
    public static (double? Threshold, double? Separation) SuggestThreshold(IReadOnlyList<double> answerable, IReadOnlyList<double> unanswerable)
    {
        if (answerable.Count == 0 || unanswerable.Count == 0)
        {
            return (null, null);
        }
        var candidates = answerable.Concat(unanswerable).Distinct().OrderBy(v => v).ToList();
        double? best = null;
        var bestCorrect = -1;
        for (var i = 0; i < candidates.Count; i++)
        {
            var upper = i + 1 < candidates.Count ? candidates[i + 1] : candidates[i] + 0.01;
            var threshold = (candidates[i] + upper) / 2;
            var correct = answerable.Count(s => s >= threshold) + unanswerable.Count(s => s < threshold);
            if (correct > bestCorrect)
            {
                (best, bestCorrect) = (threshold, correct);
            }
        }
        var low = candidates[0] - 0.01;
        if (answerable.Count(s => s >= low) > bestCorrect)
        {
            (best, bestCorrect) = (low, answerable.Count(s => s >= low));
        }
        return (Math.Round(best!.Value, 3), Math.Round((double)bestCorrect / (answerable.Count + unanswerable.Count), 3));
    }

    /// <summary>Le seuil tient-il sur des questions qu'il n'a pas vues ? (séquence 3.1)</summary>
    public static JsonObject ValidateThreshold(IReadOnlyList<RetrievalScore> rows, double threshold)
    {
        var answerable = rows.Where(r => r.Question.Answerable).ToList();
        var unanswerable = rows.Where(r => !r.Question.Answerable).ToList();
        return new JsonObject
        {
            ["questions"] = rows.Count,
            ["hit@1"] = Mean(answerable.Where(r => r.Hit1 is not null).Select(r => r.Hit1 == true ? 1.0 : 0.0).ToList()),
            ["kept_answerable"] = Mean(answerable.Select(r => r.Top1 >= threshold ? 1.0 : 0.0).ToList()),
            ["correct_refusals"] = Mean(unanswerable.Select(r => r.Top1 < threshold ? 1.0 : 0.0).ToList()),
            ["threshold"] = threshold,
        };
    }

    public static double? KeywordCoverage(string text, IReadOnlyList<string> keywords)
    {
        if (keywords.Count == 0)
        {
            return null;
        }
        var plain = Plain(text);
        return (double)keywords.Count(k => plain.Contains(Plain(k), StringComparison.Ordinal)) / keywords.Count;
    }

    /// <summary>
    /// Minuscules sans accents : comparaison grossière, comme dans la version Python.
    /// Table explicite plutôt que <c>Normalize</c> : l'application tourne en globalisation
    /// invariante (Directory.Build.props), où la décomposition Unicode n'est pas garantie.
    /// </summary>
    public static string Plain(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            var folded = Accents.IndexOf(c);
            if (folded >= 0)
            {
                builder.Append(Folded[folded]);
            }
            else if (c < 128)
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }

    private const string Accents = "àáâãäåçèéêëìíîïñòóôõöùúûüýÿ";
    private const string Folded = "aaaaaaceeeeiiiinooooouuuuyy";

    private static JsonArray Summarize(List<JsonObject> rows)
    {
        var generation = new JsonArray();
        foreach (var group in rows.GroupBy(r => (Emb: S(r["embedding"]), Gen: S(r["generation"]))))
        {
            var all = group.ToList();
            var answerable = all.Where(r => r["answerable"]!.GetValue<bool>()).ToList();
            var unanswerable = all.Where(r => !r["answerable"]!.GetValue<bool>()).ToList();
            var ok = all.Where(r => S(r["status"]) != "error").ToList();
            var stability = ok.GroupBy(r => S(r["question_id"]))
                .Select(g => g.Select(r => (S(r["status"]), S(r["cited_documents"]))).ToList())
                .Where(outcomes => outcomes.Count > 1)
                .Select(outcomes => (double)outcomes.GroupBy(o => o).Max(g => g.Count()) / outcomes.Count)
                .ToList();
            generation.Add(new JsonObject
            {
                ["embedding"] = group.Key.Emb,
                ["generation"] = group.Key.Gen,
                ["calls"] = all.Count,
                ["errors"] = all.Count(r => S(r["status"]) == "error"),
                ["answer_rate"] = Mean(answerable.Select(r => S(r["status"]) == "answered" ? 1.0 : 0.0).ToList()),
                ["source_hit_rate"] = Mean(answerable.Where(r => r["source_hit"] is not null).Select(r => r["source_hit"]!.GetValue<bool>() ? 1.0 : 0.0).ToList()),
                ["keyword_coverage"] = Mean(answerable.Where(r => r["keyword_coverage"] is not null).Select(r => r["keyword_coverage"]!.GetValue<double>()).ToList()),
                ["unsourced_rate"] = Mean(all.Select(r => S(r["status"]) == "unsourced" ? 1.0 : 0.0).ToList()),
                ["correct_refusal_rate"] = Mean(unanswerable.Select(r => S(r["status"]) == "no_relevant_source" ? 1.0 : 0.0).ToList()),
                ["forbidden_leaks"] = all.Count(r => r["forbidden_leak"]?.GetValue<bool>() == true),
                ["stability"] = Mean(stability),
                ["mean_attempts"] = Mean(ok.Where(r => r["attempts"]?.GetValue<int>() > 0).Select(r => (double)r["attempts"]!.GetValue<int>()).ToList()),   // les refus (0 tentative) ne comptent pas
                ["latency_median_ms"] = Median(ok.Select(r => (double)r["latency_ms"]!.GetValue<int>()).ToList()),
                ["latency_p90_ms"] = P90(ok.Select(r => (double)r["latency_ms"]!.GetValue<int>()).ToList()),
            });
        }
        return generation;
    }

    private static readonly string[] CsvFields =
    {
        "embedding", "generation", "run", "question_id", "answerable", "status", "attempts", "latency_ms",
        "generation_model_id", "cited_documents", "source_hit", "forbidden_leak", "keyword_coverage", "error", "text",
    };

    private static void WriteCsv(List<JsonObject> rows, string path)
    {
        var lines = new StringBuilder();
        lines.Append(string.Join(",", CsvFields)).Append("\r\n");
        foreach (var row in rows)
        {
            lines.Append(string.Join(",", CsvFields.Select(f => Csv(row[f])))).Append("\r\n");
        }
        File.WriteAllText(path, lines.ToString(), new UTF8Encoding(false));
    }

    private static string Csv(JsonNode? node)
    {
        var value = node switch
        {
            null => "",
            JsonValue v when v.TryGetValue<bool>(out var b) => b ? "True" : "False",
            JsonValue v when v.TryGetValue<double>(out var d) => d.ToString(CultureInfo.InvariantCulture),
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => node.ToJsonString(),
        };
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    private static string Report(JsonObject summary, int runs, int questionCount, string promptVersion,
                                 IReadOnlyDictionary<string, object> splitter, AppConfig config)
    {
        var lines = new List<string>
        {
            $"# Banc d'essai — {DateTime.Now:yyyy-MM-dd HH:mm}",
            "",
            $"{questionCount} questions · {runs} passage(s) par question · prompt `{promptVersion}` · "
            + $"découpage `{Presenter.Splitter(splitter).ToJsonString()}` · top_k={config.TopK} · température={config.Temperature}",
            "",
            "## Recherche (sans génération)",
            "",
            "| Embeddings | Modèle servi | Dim. | Morceaux | Indexation (s) | Hit@1 | Hit@k | Score top-1 médian (répondables) | (hors corpus) | Seuil configuré | Seuil suggéré | Séparation | Seuil utilisé |",
            "|---|---|---|---|---|---|---|---|---|---|---|---|---|",
        };
        var retrieval = summary["retrieval"]!.AsArray().Select(n => n!.AsObject()).ToList();
        foreach (var r in retrieval)
        {
            if (r["error"] is not null)
            {
                lines.Add($"| {S(r["embedding"])} | ÉCHEC : {S(r["error"])} |" + string.Concat(Enumerable.Repeat(" |", 11)));
                continue;
            }
            lines.Add($"| {S(r["embedding"])} | `{S(r["model_id"])}` | {r["dimension"]} | {r["chunks"]} | {F(r["index_seconds"])} | "
                      + $"{F(r["hit@1"])} | {F(r["hit@k"])} | {F(r["top1_median_answerable"])} | {F(r["top1_median_unanswerable"])} | "
                      + $"{F(r["configured_threshold"])} | {F(r["suggested_threshold"])} | {F(r["separation_accuracy"])} | {F(r["threshold_used"])} |");
        }
        if (retrieval.Any(r => r["validation"] is not null))
        {
            lines.AddRange(new[]
            {
                "",
                "## Validation du seuil sur des questions jamais vues",
                "",
                "| Embeddings | Seuil éprouvé | Questions | Hit@1 | Répondables retenues | Refus justes (hors corpus) |",
                "|---|---|---|---|---|---|",
            });
            foreach (var r in retrieval)
            {
                if (r["validation"] is JsonObject v)
                {
                    lines.Add($"| {S(r["embedding"])} | {F(v["threshold"])} | {v["questions"]} | {F(v["hit@1"])} | {F(v["kept_answerable"])} | {F(v["correct_refusals"])} |");
                }
            }
        }
        lines.AddRange(new[]
        {
            "",
            "## Réponses (avec génération)",
            "",
            "| Embeddings | Génération | Répond (répondables) | Bonne source | Mots-clés | Non sourcé | Refus justes (hors corpus) | Fuites d'accès | Stabilité | Tentatives | Latence médiane (ms) | p90 (ms) | Erreurs |",
            "|---|---|---|---|---|---|---|---|---|---|---|---|---|",
        });
        foreach (var g in summary["generation"]!.AsArray().Select(n => n!.AsObject()))
        {
            lines.Add($"| {S(g["embedding"])} | {S(g["generation"])} | {F(g["answer_rate"])} | {F(g["source_hit_rate"])} | {F(g["keyword_coverage"])} | "
                      + $"{F(g["unsourced_rate"])} | {F(g["correct_refusal_rate"])} | {g["forbidden_leaks"]} | {F(g["stability"])} | {F(g["mean_attempts"])} | "
                      + $"{Ms(g["latency_median_ms"])} | {Ms(g["latency_p90_ms"])} | {g["errors"]} |");
        }
        lines.AddRange(new[]
        {
            "",
            "## Lecture",
            "",
            "- **Hit@1 / Hit@k** : part des questions répondables dont un document attendu arrive en tête / figure dans les k passages retrouvés. Sur un petit corpus, Hit@k est vite saturé : regarder Hit@1.",
            "- **Seuil suggéré** : sépare au mieux les questions répondables des questions hors corpus. Calibré sur ces mêmes questions, il est optimiste : la section « Validation » l'éprouve sur des questions jamais vues.",
            "- **Bonne source** : parmi les réponses données, part qui cite un document attendu.",
            "- **Mots-clés** : part des mots-clés attendus présents dans la réponse (indicateur grossier).",
            "- **Non sourcé** : le modèle n'a pas cité correctement ses sources malgré les tentatives.",
            "- **Fuites d'accès** : doit toujours valoir 0, le filtrage est fait avant le modèle.",
            "- **Stabilité** : pour une même question, part des passages qui donnent le même statut et les mêmes documents cités (1 = parfaitement stable). Nécessite au moins 2 passages.",
            "",
        });
        return string.Join("\n", lines);
    }

    // --- petits utilitaires numériques, arrondis comme en Python ---

    public static double? Mean(IReadOnlyList<double> values) => values.Count == 0 ? null : Math.Round(values.Average(), 3);

    public static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }
        var ordered = values.OrderBy(v => v).ToList();
        var mid = ordered.Count / 2;
        return Math.Round(ordered.Count % 2 == 1 ? ordered[mid] : (ordered[mid - 1] + ordered[mid]) / 2, 3);
    }

    public static double? P90(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }
        var ordered = values.OrderBy(v => v).ToList();
        return Math.Round(ordered[Math.Min(ordered.Count - 1, (int)(0.9 * ordered.Count))], 1);
    }

    public static string F(JsonNode? node) => node is null ? "—" : node is JsonValue v && v.TryGetValue<double>(out var d) ? F(d) : node.ToString();
    public static string F(double? value) => value is null ? "—" : value.Value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Ms(JsonNode? node) => node is null ? "—" : node.GetValue<double>().ToString("0", CultureInfo.InvariantCulture);
    private static string S(JsonNode? node) => node?.GetValue<string>() ?? "";
}
