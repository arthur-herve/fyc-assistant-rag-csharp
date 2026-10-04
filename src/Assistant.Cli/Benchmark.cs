// Banc d'essai : mesurer, pas asserter (séquences 3.1 et 3.2).
//
// Deux mesures séparées, parce que deux composants séparés :
// 1. la recherche seule (déterministe à modèle d'embeddings fixé) : hit@1, hit@k, seuil
//    de pertinence suggéré, puis éprouvé sur un jeu de questions jamais vues ;
// 2. la génération (probabiliste) sur N passages : taux de réponse, bonne source, refus
//    justes, fuites d'accès (toujours 0 : les droits sont filtrés avant le modèle),
//    stabilité d'un passage à l'autre, latences.
// Dans son dossier, le banc écrit resultats.csv, synthese.json et rapport.md, ainsi que
// l'index qu'il construit pour chaque modèle d'embeddings (index-<modèle>.json).

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Assistant.Application;
using Assistant.Domain;

namespace Assistant.Cli;

/// <summary>
/// Les options du banc. <paramref name="NewOutDir"/> : le dossier par défaut, daté à la seconde, n'est jamais un dossier
/// qui existe déjà (<see cref="AppConfig.CreateNewDir"/>) ; --out, lui, peut exister.
/// </summary>
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
    string? PromptName = null,
    bool NewOutDir = false);

public sealed record RetrievalScore(EvalQuestion Question, double Top1, bool? Hit, bool? Hit1);

/// <summary>Une réponse mesurée par le banc : une ligne de resultats.csv.</summary>
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

    /// <summary>
    /// Les colonnes de resultats.csv, dans l'ordre des résultats de référence (eval/resultats) : une seule
    /// liste, d'où viennent l'en-tête et les valeurs, qui ne peuvent donc pas se décaler. Valeurs au format
    /// invariant de .NET (True/False, 0.5, 1), champ vide pour « sans objet ».
    /// </summary>
    private static readonly (string Name, Func<BenchmarkRow, string> Value)[] Columns =
    {
        ("embedding", r => r.Embedding),
        ("generation", r => r.Generation),
        ("run", r => Int(r.Run)),
        ("question_id", r => r.QuestionId),
        ("answerable", r => Bool(r.Answerable)),
        ("status", r => r.Status),
        ("attempts", r => r.Attempts is { } attempts ? Int(attempts) : ""),
        ("latency_ms", r => Int(r.LatencyMs)),
        ("generation_model_id", r => r.GenerationModelId),
        ("cited_documents", r => r.CitedDocuments),
        ("source_hit", r => r.SourceHit is { } hit ? Bool(hit) : ""),
        ("forbidden_leak", r => Bool(r.ForbiddenLeak)),
        ("keyword_coverage", r => r.KeywordCoverage is { } coverage ? Number(coverage) : ""),
        ("error", r => r.Error ?? ""),
        ("text", r => r.Text),
    };

    public static readonly string[] CsvFields = Columns.Select(c => c.Name).ToArray();

    /// <summary>Le modèle a-t-il été appelé ? Un refus sans passage pertinent n'en a pas besoin.</summary>
    public bool Generated => Attempts > 0;

    public string CsvLine() => string.Join(",", Columns.Select(c => Escape(c.Value(this))));

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Bool(bool value) => value ? "True" : "False";
    private static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Escape(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}

// --- synthèses : typées ici, écrites dans synthese.json ---

/// <summary>Le seuil retenu, éprouvé sur des questions qu'il n'a pas vues (séquence 3.1).</summary>
public sealed record ThresholdValidation(int Questions, double? HitAt1, double? KeptAnswerable, double? CorrectRefusals, double Threshold)
{
    public JsonObject ToJson() => new()
    {
        ["questions"] = Questions,
        ["hit@1"] = HitAt1,
        ["kept_answerable"] = KeptAnswerable,
        ["correct_refusals"] = CorrectRefusals,
        ["threshold"] = Threshold,
    };
}

/// <summary>Une entrée « retrieval » de synthese.json : la recherche mesurée pour un modèle d'embeddings, ou l'échec de son indexation.</summary>
public abstract record RetrievalResult(string Embedding)
{
    public abstract JsonObject ToJson();
}

/// <summary>Un modèle d'embeddings dont l'indexation a échoué : le banc passe au suivant.</summary>
public sealed record IndexingFailure(string Embedding, string Error) : RetrievalResult(Embedding)
{
    public override JsonObject ToJson() => new() { ["embedding"] = Embedding, ["error"] = Error };
}

/// <summary>La recherche seule, pour un modèle d'embeddings : hit@1, hit@k, seuils configuré, suggéré, utilisé.</summary>
public sealed record RetrievalSummary(
    string Embedding,
    string ModelId,
    int Dimension,
    int Chunks,
    double IndexSeconds,
    double? HitAt1,
    double? HitAtK,
    int TopK,
    double? Top1MedianAnswerable,
    double? Top1MedianUnanswerable,
    double ConfiguredThreshold,
    bool ConfiguredThresholdIsDefault,
    double? SuggestedThreshold,
    double? SeparationAccuracy,
    double ThresholdUsed,
    ThresholdValidation? Validation = null) : RetrievalResult(Embedding)
{
    public override JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["embedding"] = Embedding,
            ["model_id"] = ModelId,
            ["dimension"] = Dimension,
            ["chunks"] = Chunks,
            ["index_seconds"] = IndexSeconds,
            ["hit@1"] = HitAt1,
            ["hit@k"] = HitAtK,
            ["top_k"] = TopK,
            ["top1_median_answerable"] = Top1MedianAnswerable,
            ["top1_median_unanswerable"] = Top1MedianUnanswerable,
            ["configured_threshold"] = ConfiguredThreshold,
            ["configured_threshold_is_default"] = ConfiguredThresholdIsDefault,
            ["suggested_threshold"] = SuggestedThreshold,
            ["separation_accuracy"] = SeparationAccuracy,
            ["threshold_used"] = ThresholdUsed,
        };
        if (Validation is not null)
        {
            json["validation"] = Validation.ToJson();
        }
        return json;
    }
}

/// <summary>Les réponses d'un couple (embeddings, génération), mesurées en proportions (séquence 3.1).</summary>
public sealed record GenerationSummary(
    string Embedding,
    string Generation,
    int Calls,
    int Errors,
    double? AnswerRate,
    double? SourceHitRate,
    double? KeywordCoverage,
    double? UnsourcedRate,
    double? CorrectRefusalRate,
    int ForbiddenLeaks,
    double? Stability,
    double? MeanAttempts,
    double? LatencyMedianMs,
    double? LatencyP90Ms,
    int RefusalsWithoutGeneration)
{
    public JsonObject ToJson() => new()
    {
        ["embedding"] = Embedding,
        ["generation"] = Generation,
        ["calls"] = Calls,
        ["errors"] = Errors,
        ["answer_rate"] = AnswerRate,
        ["source_hit_rate"] = SourceHitRate,
        ["keyword_coverage"] = KeywordCoverage,
        ["unsourced_rate"] = UnsourcedRate,
        ["correct_refusal_rate"] = CorrectRefusalRate,
        ["forbidden_leaks"] = ForbiddenLeaks,
        ["stability"] = Stability,
        ["mean_attempts"] = MeanAttempts,
        ["latency_median_ms"] = LatencyMedianMs,
        ["latency_p90_ms"] = LatencyP90Ms,
        ["refusals_without_generation"] = RefusalsWithoutGeneration,
    };
}

/// <summary>Le contenu de synthese.json : la recherche par modèle d'embeddings, les réponses par couple de modèles.</summary>
public sealed record BenchmarkSummary(IReadOnlyList<RetrievalResult> Retrieval, IReadOnlyList<GenerationSummary> Generation)
{
    public JsonObject ToJson() => new()
    {
        ["retrieval"] = new JsonArray(Retrieval.Select(r => (JsonNode)r.ToJson()).ToArray()),
        ["generation"] = new JsonArray(Generation.Select(g => (JsonNode)g.ToJson()).ToArray()),
    };
}

public static class Benchmark
{
    private static readonly string Answered = StatusNames.Of(AnswerStatus.Answered);
    private static readonly string NoRelevantSource = StatusNames.Of(AnswerStatus.NoRelevantSource);
    private static readonly string Unsourced = StatusNames.Of(AnswerStatus.Unsourced);

    public static BenchmarkSummary Run(AppConfig config, BenchmarkOptions options, Action<string> log)
    {
        var (imposedMinScore, promptVersion) = CheckOptions(config, options);
        // Les options d'abord, toutes : une faute de frappe se dit sans service IA.
        AiService.Require(config.AiBaseUrl);
        // Le dossier par défaut, daté à la seconde : jamais un dossier qui existe déjà. --out : un dossier existant convient.
        var outDir = options.NewOutDir ? AppConfig.CreateNewDir(options.OutDir) : options.OutDir;
        Directory.CreateDirectory(outDir);
        var retrieval = new List<RetrievalResult>();
        var rows = new List<BenchmarkRow>();
        // Chaque ligne est écrite dès qu'elle est connue : une coupure en fin de campagne ne perd rien.
        using var csv = new StreamWriter(Path.Combine(outDir, "resultats.csv"), false, new UTF8Encoding(false)) { NewLine = "\r\n" };
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
            var indexPath = Path.Combine(outDir, $"index-{emb}.json");
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
                retrieval.Add(new IndexingFailure(emb, error.Message));
                continue;
            }
            var measured = MeasureRetrieval(container, config, emb, manifest, Math.Round(clock.Elapsed.TotalSeconds, 1), options, imposedMinScore, log);
            retrieval.Add(measured);
            foreach (var gen in options.GenerationModels)
            {
                log($"  --- Génération : {gen} ({options.Runs} passage(s)) ---");
                var ask = Composition.Build(config, overrides with { GenerationModel = gen, MinScore = measured.ThresholdUsed }).AskQuestion;
                MeasureGeneration(ask, config, emb, gen, options, Record, log);
            }
        }

        var summary = new BenchmarkSummary(retrieval, Summarize(rows));
        // Le JSON de status --json : indenté, accents tels quels.
        File.WriteAllText(Path.Combine(outDir, "synthese.json"), Presenter.ToJson(summary.ToJson()) + Environment.NewLine, new UTF8Encoding(false));
        var splitter = new Dictionary<string, object>
        {
            ["max_chars"] = options.SplitterMaxChars ?? config.SplitterMaxChars,
            ["overlap_chars"] = options.SplitterOverlapChars ?? config.SplitterOverlapChars,
            ["include_title"] = config.SplitterIncludeTitle,
        };
        File.WriteAllText(Path.Combine(outDir, "rapport.md"),
            Report(summary, options.Runs, options.Questions.Count, promptVersion, splitter, config), new UTF8Encoding(false));
        log($"\nRapport : {Path.Combine(outDir, "rapport.md").Replace('\\', '/')}");   // des « / » : la même sortie sous Windows et Linux
        return summary;
    }

    /// <summary>
    /// Tout ce qui peut être faux dans les options l'est dit avant la première indexation, et avant de
    /// créer le dossier de sortie : utilisateurs, passages, seuil, découpage, prompt. Renvoie le seuil
    /// imposé par --min-score (null : config ou auto) et la version du prompt.
    /// </summary>
    private static (double? ImposedMinScore, string PromptVersion) CheckOptions(AppConfig config, BenchmarkOptions options)
    {
        foreach (var q in options.Questions.Concat(options.Validation ?? Array.Empty<EvalQuestion>()))
        {
            config.User(q.UserName);   // utilisateur inconnu : UnknownUserException tout de suite
        }
        if (options.Runs < 1)
        {
            throw new ArgumentException($"--runs doit valoir au moins 1, pas {options.Runs}");
        }
        var imposed = FixedMinScore(options.MinScoreMode);
        // Monter la configuration ne calcule rien : un découpage hors bornes (ArgumentException) ou un
        // prompt introuvable se voient ici, pas après une indexation.
        var container = Composition.Build(config, new Overrides(PromptName: options.PromptName, SplitterMaxChars: options.SplitterMaxChars,
                                                                SplitterOverlapChars: options.SplitterOverlapChars));
        return (imposed, container.Prompts.Get(container.Settings.PromptName).Version);
    }

    /// <summary>
    /// --min-score : config, auto (null : pas de seuil imposé) ou un seuil de [-1, 1], comme ceux de la
    /// configuration. NaN échoue à la comparaison : il est refusé comme 5 ou -2.
    /// </summary>
    public static double? FixedMinScore(string mode)
    {
        if (mode is "config" or "auto")
        {
            return null;
        }
        return double.TryParse(mode, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= -1 and <= 1
            ? value
            : throw new ArgumentException($"--min-score attend config, auto ou un nombre de [-1, 1] (ex. 0.6), pas « {mode} »");
    }

    /// <summary>La recherche seule, sans génération : hit@1, hit@k, seuil suggéré puis éprouvé.</summary>
    private static RetrievalSummary MeasureRetrieval(Container container, AppConfig config, string emb, IndexManifest manifest, double indexSeconds,
                                                     BenchmarkOptions options, double? imposedMinScore, Action<string> log)
    {
        log($"  {manifest.ChunkCount} morceaux indexés en {indexSeconds} s ({manifest.EmbeddingModel}, {manifest.Dimension} dim.)");
        var topK = container.Settings.TopK;
        var scored = RetrievalScores(container, config, options.Questions);
        var hits = scored.Where(r => r.Question.Answerable && r.Hit is not null).Select(r => r.Hit == true ? 1.0 : 0.0).ToList();
        var hits1 = scored.Where(r => r.Question.Answerable && r.Hit1 is not null).Select(r => r.Hit1 == true ? 1.0 : 0.0).ToList();
        var answerable = scored.Where(r => r.Question.Answerable).Select(r => r.Top1).ToList();
        var unanswerable = scored.Where(r => !r.Question.Answerable).Select(r => r.Top1).ToList();

        var (suggested, separation) = SuggestThreshold(answerable, unanswerable);
        var configured = config.MinScoreFor(emb);
        var isDefault = !config.HasThresholdFor(emb);
        var used = imposedMinScore ?? (options.MinScoreMode == "auto" ? suggested ?? configured : configured);
        var byDefault = isDefault ? " (default : aucun seuil pour cet alias)" : "";
        log($"  hit@1={F(Mean(hits1))} · hit@{topK}={F(Mean(hits))} · seuil configuré={configured}{byDefault} · seuil suggéré={F(suggested)} · seuil utilisé={used}");

        ThresholdValidation? checked_ = null;
        if (options.Validation is { Count: > 0 } validation)
        {
            // Le seuil retenu, éprouvé sur des questions qu'il n'a pas vues.
            checked_ = ValidateThreshold(RetrievalScores(container, config, validation), used);
            log($"  validation ({checked_.Questions} questions jamais vues, seuil {used}) : hit@1={F(checked_.HitAt1)} · "
                + $"répondables retenues={F(checked_.KeptAnswerable)} · refus justes={F(checked_.CorrectRefusals)}");
        }
        return new RetrievalSummary(emb, manifest.EmbeddingModel, manifest.Dimension, manifest.ChunkCount, indexSeconds,
            HitAt1: Mean(hits1), HitAtK: Mean(hits), TopK: topK,
            Top1MedianAnswerable: Median(answerable), Top1MedianUnanswerable: Median(unanswerable),
            ConfiguredThreshold: configured, ConfiguredThresholdIsDefault: isDefault,
            SuggestedThreshold: suggested, SeparationAccuracy: separation, ThresholdUsed: used, Validation: checked_);
    }

    /// <summary>Chaque question <c>Runs</c> fois : la génération se mesure en proportions (séquence 3.1).</summary>
    private static void MeasureGeneration(AskQuestion ask, AppConfig config, string emb, string gen, BenchmarkOptions options,
                                          Action<BenchmarkRow> record, Action<string> log)
    {
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
                    record(new BenchmarkRow(emb, gen, run, q.Id, q.Answerable, BenchmarkRow.ErrorStatus,
                                            (int)started.ElapsedMilliseconds, Error: error.Message));
                    log($"    {q.Id} : erreur — {error.Message}");
                    continue;
                }
                var cited = answer.Sources.Select(s => s.DocumentId).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
                var answered = answer.Status == AnswerStatus.Answered;
                record(new BenchmarkRow(
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
    public static ThresholdValidation ValidateThreshold(IReadOnlyList<RetrievalScore> rows, double threshold)
    {
        var answerable = rows.Where(r => r.Question.Answerable).ToList();
        var unanswerable = rows.Where(r => !r.Question.Answerable).ToList();
        return new ThresholdValidation(
            Questions: rows.Count,
            HitAt1: Mean(answerable.Where(r => r.Hit1 is not null).Select(r => r.Hit1 == true ? 1.0 : 0.0).ToList()),
            KeptAnswerable: Mean(answerable.Select(r => r.Top1 >= threshold ? 1.0 : 0.0).ToList()),
            CorrectRefusals: Mean(unanswerable.Select(r => r.Top1 < threshold ? 1.0 : 0.0).ToList()),
            Threshold: threshold);
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
    /// Minuscules sans accents : comparaison grossière.
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

    internal static List<GenerationSummary> Summarize(List<BenchmarkRow> rows)
    {
        var generation = new List<GenerationSummary>();
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
            generation.Add(new GenerationSummary(
                Embedding: group.Key.Embedding,
                Generation: group.Key.Generation,
                Calls: all.Count,
                Errors: all.Count - ok.Count,
                AnswerRate: Mean(answerable.Select(r => r.Status == Answered ? 1.0 : 0.0).ToList()),
                SourceHitRate: Mean(answerable.Where(r => r.SourceHit is not null).Select(r => r.SourceHit == true ? 1.0 : 0.0).ToList()),
                KeywordCoverage: Mean(answerable.Where(r => r.KeywordCoverage is not null).Select(r => r.KeywordCoverage!.Value).ToList()),
                UnsourcedRate: Mean(all.Select(r => r.Status == Unsourced ? 1.0 : 0.0).ToList()),
                CorrectRefusalRate: Mean(unanswerable.Select(r => r.Status == NoRelevantSource ? 1.0 : 0.0).ToList()),
                ForbiddenLeaks: all.Count(r => r.ForbiddenLeak),
                Stability: Mean(stability),
                MeanAttempts: Mean(ok.Where(r => r.Generated).Select(r => (double)r.Attempts!.Value).ToList()),   // les refus (0 tentative) ne comptent pas
                LatencyMedianMs: Median(generated),
                LatencyP90Ms: P90(generated),
                RefusalsWithoutGeneration: ok.Count(r => !r.Generated)));
        }
        return generation;
    }

    private static string Report(BenchmarkSummary summary, int runs, int questionCount, string promptVersion,
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
        var measured = summary.Retrieval.OfType<RetrievalSummary>().ToList();
        foreach (var result in summary.Retrieval)
        {
            switch (result)
            {
                case IndexingFailure failure:
                    lines.Add($"| {failure.Embedding} | ÉCHEC : {failure.Error} |" + string.Concat(Enumerable.Repeat(" |", 11)));
                    break;
                case RetrievalSummary r:
                    lines.Add($"| {r.Embedding} | `{r.ModelId}` | {r.Dimension} | {r.Chunks} | {F(r.IndexSeconds)} | "
                              + $"{F(r.HitAt1)} | {F(r.HitAtK)} | {F(r.Top1MedianAnswerable)} | {F(r.Top1MedianUnanswerable)} | "
                              + $"{F(r.ConfiguredThreshold)}{(r.ConfiguredThresholdIsDefault ? " (default)" : "")} | "
                              + $"{F(r.SuggestedThreshold)} | {F(r.SeparationAccuracy)} | {F(r.ThresholdUsed)} |");
                    break;
            }
        }
        if (measured.Any(r => r.Validation is not null))
        {
            lines.AddRange(new[]
            {
                "",
                "## Validation du seuil sur des questions jamais vues",
                "",
                "| Embeddings | Seuil éprouvé | Questions | Hit@1 | Répondables retenues | Refus justes (sans réponse accessible) |",
                "|---|---|---|---|---|---|",
            });
            foreach (var r in measured)
            {
                if (r.Validation is { } v)
                {
                    lines.Add($"| {r.Embedding} | {F(v.Threshold)} | {v.Questions} | {F(v.HitAt1)} | {F(v.KeptAnswerable)} | {F(v.CorrectRefusals)} |");
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
        foreach (var g in summary.Generation)
        {
            lines.Add($"| {g.Embedding} | {g.Generation} | {F(g.AnswerRate)} | {F(g.SourceHitRate)} | {F(g.KeywordCoverage)} | "
                      + $"{F(g.UnsourcedRate)} | {F(g.CorrectRefusalRate)} | {g.ForbiddenLeaks} | {F(g.Stability)} | {F(g.MeanAttempts)} | "
                      + $"{Ms(g.LatencyMedianMs)} | {Ms(g.LatencyP90Ms)} | {g.Errors} |");
        }
        lines.AddRange(new[]
        {
            "",
            "## Lecture",
            "",
            "- **Hit@1 / Hit@k** : part des questions répondables dont un document attendu arrive en tête / figure dans les k passages retrouvés. Sur un petit corpus, Hit@k est vite saturé : regarder Hit@1.",
            "- **Seuil suggéré** : sépare au mieux les questions répondables des questions hors corpus. Calibré sur ces mêmes questions, il est optimiste.",
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

    // --- petits utilitaires numériques ---

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

    public static string F(double? value) => value is null ? "—" : value.Value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Ms(double? value) => value is null ? "—" : value.Value.ToString("0", CultureInfo.InvariantCulture);
}
