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

/// <summary>Une réponse mesurée par le banc : une ligne de resultats.csv, mêmes colonnes que la version Python.</summary>
public sealed record BenchmarkRow(
    string Embedding,
    string Generation,
    int Run,
    string QuestionId,
    bool Answerable,
    string Status,
    int LatencyMs,
    int? Attempts = null,
    string GenerationModelId = "",
    string CitedDocuments = "",
    bool? SourceHit = null,
    bool ForbiddenLeak = false,
    double? KeywordCoverage = null,
    string? Error = null,
    string Text = "")
{
    public const string ErrorStatus = "error";

    public static readonly string[] CsvFields =
    {
        "embedding", "generation", "run", "question_id", "answerable", "status", "attempts", "latency_ms",
        "generation_model_id", "cited_documents", "source_hit", "forbidden_leak", "keyword_coverage", "error", "text",
    };

    /// <summary>Le modèle a-t-il été appelé ? Un refus sans passage pertinent n'en a pas besoin.</summary>
    public bool Generated => Attempts > 0;

    /// <summary>Valeurs écrites comme le module csv de Python : True/False, 1.0, champ vide pour « sans objet ».</summary>
    public string CsvLine() => string.Join(",", new[]
    {
        Embedding, Generation, Int(Run), QuestionId, Bool(Answerable), Status, Attempts is null ? "" : Int(Attempts.Value),
        Int(LatencyMs), GenerationModelId, CitedDocuments, SourceHit is null ? "" : Bool(SourceHit.Value), Bool(ForbiddenLeak),
        KeywordCoverage is null ? "" : Number(KeywordCoverage.Value), Error ?? "", Text,
    }.Select(Escape));

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Bool(bool value) => value ? "True" : "False";
    private static string Number(double value) =>
        value == Math.Floor(value) ? value.ToString("0.0", CultureInfo.InvariantCulture) : value.ToString("R", CultureInfo.InvariantCulture);
    private static string Escape(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}

public static class Benchmark
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string Answered = StatusNames.Of(AnswerStatus.Answered);
    private static readonly string NoRelevantSource = StatusNames.Of(AnswerStatus.NoRelevantSource);
    private static readonly string Unsourced = StatusNames.Of(AnswerStatus.Unsourced);

    public static JsonObject Run(AppConfig config, BenchmarkOptions options, Action<string> log)
    {
        // Tout ce qui peut être faux dans les options l'est dit avant la première indexation.
        foreach (var q in options.Questions.Concat(options.Validation ?? Array.Empty<EvalQuestion>()))
        {
            config.User(q.UserName);   // utilisateur inconnu : UnknownUserException tout de suite
        }
        Directory.CreateDirectory(options.OutDir);
        var retrieval = new List<JsonObject>();
        var rows = new List<BenchmarkRow>();
        // Chaque ligne est écrite dès qu'elle est connue : une coupure en fin de campagne ne perd rien.
        using var csv = new StreamWriter(Path.Combine(options.OutDir, "resultats.csv"), false, new UTF8Encoding(false)) { NewLine = "\r\n" };
        csv.WriteLine(string.Join(",", BenchmarkRow.CsvFields));
        void Record(BenchmarkRow row)
        {
            rows.Add(row);
            csv.WriteLine(row.CsvLine());
            csv.Flush();
        }

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
                ["configured_threshold_is_default"] = !config.HasThresholdFor(emb),
                ["suggested_threshold"] = suggested,
                ["separation_accuracy"] = separation,
                ["threshold_used"] = used,
            };
            retrieval.Add(summary);
            var byDefault = config.HasThresholdFor(emb) ? "" : " (default : aucun seuil pour cet alias)";
            log($"  hit@1={F(Mean(hits1))} · hit@{topK}={F(Mean(hits))} · seuil configuré={configured}{byDefault} · seuil suggéré={F(suggested)} · seuil utilisé={used}");

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
                        var started = Stopwatch.StartNew();
                        Answer answer;
                        try
                        {
                            answer = ask.Execute(config.User(q.UserName), q.Question);
                        }
                        catch (AssistantApplicationException error)
                        {
                            Record(new BenchmarkRow(emb, gen, run, q.Id, q.Answerable, BenchmarkRow.ErrorStatus,
                                                    (int)started.ElapsedMilliseconds, Error: error.Message));
                            log($"    {q.Id} : erreur — {error.Message}");
                            continue;
                        }
                        var cited = answer.Sources.Select(s => s.DocumentId).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
                        var answered = answer.Status == AnswerStatus.Answered;
                        Record(new BenchmarkRow(
                            emb, gen, run, q.Id, q.Answerable, StatusNames.Of(answer.Status), (int)started.ElapsedMilliseconds,
                            Attempts: answer.Trace.Attempts,
                            GenerationModelId: answer.Trace.GenerationModel ?? "",
                            CitedDocuments: string.Join("|", cited),
                            SourceHit: answered && q.ExpectedDocuments.Count > 0 ? cited.Intersect(q.ExpectedDocuments).Any() : null,
                            ForbiddenLeak: cited.Intersect(q.ForbiddenDocuments).Any(),
                            KeywordCoverage: answered ? KeywordCoverage(answer.Text, q.ExpectedKeywords) : null,
                            Text: answer.Text.Replace("\n", " ")));
                    }
                    log($"    passage {run}/{options.Runs} terminé");
                }
            }
        }

        var result = new JsonObject { ["retrieval"] = new JsonArray(retrieval.ToArray<JsonNode>()), ["generation"] = Summarize(rows) };
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

    internal static JsonArray Summarize(List<BenchmarkRow> rows)
    {
        var generation = new JsonArray();
        foreach (var group in rows.GroupBy(r => (r.Embedding, r.Generation)))
        {
            var all = group.ToList();
            var answerable = all.Where(r => r.Answerable).ToList();
            var unanswerable = all.Where(r => !r.Answerable).ToList();
            var ok = all.Where(r => r.Status != BenchmarkRow.ErrorStatus).ToList();
            var stability = ok.GroupBy(r => r.QuestionId)
                .Select(g => g.Select(r => (r.Status, r.CitedDocuments)).ToList())
                .Where(outcomes => outcomes.Count > 1)
                .Select(outcomes => (double)outcomes.GroupBy(o => o).Max(g => g.Count()) / outcomes.Count)
                .ToList();
            // Latence des seules réponses générées : un refus sans passage pertinent ne coûte presque
            // rien et tirerait la médiane vers le bas.
            var generated = ok.Where(r => r.Generated).Select(r => (double)r.LatencyMs).ToList();
            generation.Add(new JsonObject
            {
                ["embedding"] = group.Key.Embedding,
                ["generation"] = group.Key.Generation,
                ["calls"] = all.Count,
                ["errors"] = all.Count - ok.Count,
                ["answer_rate"] = Mean(answerable.Select(r => r.Status == Answered ? 1.0 : 0.0).ToList()),
                ["source_hit_rate"] = Mean(answerable.Where(r => r.SourceHit is not null).Select(r => r.SourceHit == true ? 1.0 : 0.0).ToList()),
                ["keyword_coverage"] = Mean(answerable.Where(r => r.KeywordCoverage is not null).Select(r => r.KeywordCoverage!.Value).ToList()),
                ["unsourced_rate"] = Mean(all.Select(r => r.Status == Unsourced ? 1.0 : 0.0).ToList()),
                ["correct_refusal_rate"] = Mean(unanswerable.Select(r => r.Status == NoRelevantSource ? 1.0 : 0.0).ToList()),
                ["forbidden_leaks"] = all.Count(r => r.ForbiddenLeak),
                ["stability"] = Mean(stability),
                ["mean_attempts"] = Mean(ok.Where(r => r.Generated).Select(r => (double)r.Attempts!.Value).ToList()),   // les refus (0 tentative) ne comptent pas
                ["latency_median_ms"] = Median(generated),
                ["latency_p90_ms"] = P90(generated),
                ["refusals_without_generation"] = ok.Count(r => !r.Generated),
            });
        }
        return generation;
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
                      + $"{F(r["configured_threshold"])}{(r["configured_threshold_is_default"]?.GetValue<bool>() == true ? " (default)" : "")} | "
                      + $"{F(r["suggested_threshold"])} | {F(r["separation_accuracy"])} | {F(r["threshold_used"])} |");
        }
        if (retrieval.Any(r => r["validation"] is not null))
        {
            lines.AddRange(new[]
            {
                "",
                "## Validation du seuil sur des questions jamais vues",
                "",
                "| Embeddings | Seuil éprouvé | Questions | Hit@1 | Répondables retenues | Refus justes (sans réponse accessible) |",
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
            "| Embeddings | Génération | Répond (répondables) | Bonne source | Mots-clés | Non sourcé | Refus justes (sans réponse accessible) | Fuites d'accès | Stabilité | Tentatives | Latence médiane des réponses générées (ms) | p90 (ms) | Erreurs |",
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
            "- **Répond** : statut « answered ». Une réponse qui dit « je ne sais pas » en citant une source compte comme une réponse : l'indicateur ne lit pas le texte.",
            "- **Mots-clés** : part des mots-clés attendus présents dans la réponse (indicateur grossier).",
            "- **Refus justes** : questions sans réponse dans un document accessible (hors corpus ou accès refusé) auxquelles l'assistant n'a pas répondu.",
            "- **Latence** : sur les seules réponses générées ; un refus sans passage pertinent est quasi immédiat.",
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
