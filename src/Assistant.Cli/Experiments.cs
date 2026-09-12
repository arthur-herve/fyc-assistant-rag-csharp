// Expériences reproductibles : chacune change UNE chose, enregistre deux instantanés et
// écrit un rapport Markdown. Ce sont les « ruptures » de la problématique, jouées sur
// l'application propre : dépendance de données instable (découpage, modèle d'embeddings),
// non-déterminisme (stabilité), et ce que le générateur et le prompt changent malgré tout.
//
//     assistant experience cace-decoupage        --config config/app-ollama.json --questions eval/questions-service-public.json
//     assistant experience changement-embeddings --other nomic
//     assistant experience changement-generateur --other qwen3-4b
//     assistant experience prompt-v2             [--other answer-v2]
//     assistant experience stabilite             [--runs 3] [--seed 42]
//
// Mêmes sections de rapport que tools/experiences/ de la version Python : les deux se lisent côte à côte.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Assistant.Application;

namespace Assistant.Cli;

public sealed class Experiment
{
    public string Name { get; }
    public string OutDir { get; }
    public AppConfig Config { get; }
    public IReadOnlyList<EvalQuestion> Questions { get; }
    private readonly List<string> _lines = new();
    private readonly Action<string> _log;

    public Experiment(string name, AppConfig config, IReadOnlyList<EvalQuestion> questions, string? outDir, string questionsPath, string configPath, Action<string> log)
    {
        Name = name;
        Config = config;
        Questions = questions;
        _log = log;
        OutDir = outDir ?? Path.Combine(AppConfig.ProjectRoot, "eval", "resultats", $"exp-{name}-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(OutDir);
        if (questions.Count == 0)
        {
            throw new ArgumentException($"aucune question dans {questionsPath}");
        }
        Log($"# Expérience « {name} » — {DateTime.Now:yyyy-MM-dd HH:mm}");
        Log("");
        Log($"Configuration `{configPath}` · {questions.Count} questions de `{questionsPath}` · corpus `{Path.GetFileName(config.CorpusDir.TrimEnd('/', '\\'))}`.");
        Log("");
    }

    /// <summary>Index et instantanés vivent dans le dossier de l'expérience : rien n'est écrasé ailleurs.</summary>
    public Container Build(string indexName, Overrides? overrides = null) =>
        Composition.Build(Config, (overrides ?? new Overrides()) with
        {
            IndexPath = Path.Combine(OutDir, $"index-{indexName}.json"),
            SnapshotsDir = Path.Combine(OutDir, "instantanes"),
        }, _log);

    public (Container Container, IndexManifest Manifest, double Seconds) Index(string indexName, Overrides? overrides = null)
    {
        var container = Build(indexName, overrides);
        var clock = Stopwatch.StartNew();
        var manifest = container.IndexCorpus.Execute();
        return (container, manifest, clock.Elapsed.TotalSeconds);
    }

    public (Snapshot Snapshot, double Seconds) Record(string snapshotName, Container container)
    {
        var clock = Stopwatch.StartNew();
        var snapshot = container.RecordSnapshot.Execute(snapshotName, Questions.Select(q => q.ToSnapshotQuestion(Config)).ToList());
        return (snapshot, clock.Elapsed.TotalSeconds);
    }

    /// <summary>Cinq mesures lues dans un instantané, à partir de ce que les questions attendent.</summary>
    public Dictionary<string, object?> Stats(Snapshot snapshot)
    {
        var byId = Questions.ToDictionary(q => q.Id);
        var entries = snapshot.Entries.Where(e => byId.ContainsKey(e.QuestionId)).ToList();
        var answerable = entries.Where(e => byId[e.QuestionId].Answerable).ToList();
        var unanswerable = entries.Where(e => !byId[e.QuestionId].Answerable).ToList();
        var sourced = answerable.Where(e => e.Status == "answered" && byId[e.QuestionId].ExpectedDocuments.Count > 0).ToList();
        return new Dictionary<string, object?>
        {
            ["répond (répondables)"] = Rate(answerable, e => e.Status == "answered"),
            ["bonne source"] = Rate(sourced, e => e.CitedDocuments.Intersect(byId[e.QuestionId].ExpectedDocuments).Any()),
            ["refus justes (hors corpus)"] = Rate(unanswerable, e => e.Status == "no_relevant_source"),
            ["non sourcé"] = Rate(entries, e => e.Status == "unsourced"),
            ["fuites d'accès"] = (double)entries.Count(e => e.CitedDocuments.Intersect(byId[e.QuestionId].ForbiddenDocuments).Any()),
        };
    }

    private static double? Rate(List<SnapshotEntry> population, Func<SnapshotEntry, bool> predicate) =>
        population.Count == 0 ? null : Math.Round((double)population.Count(predicate) / population.Count, 2);

    public static Dictionary<string, object?> DriftSummary(SnapshotComparison comparison) => new()
    {
        ["questions comparées"] = comparison.Compared,
        ["réponses modifiées"] = comparison.Changed,
        ["taux de dérive"] = comparison.DriftRate,
        ["changements de statut"] = comparison.Count(DifferenceKind.StatusChanged),
        ["changements de sources"] = comparison.Count(DifferenceKind.SourcesChanged),
        ["reformulations"] = comparison.Count(DifferenceKind.TextChanged),
    };

    public void Log(string line) => _lines.Add(line);

    /// <summary>Tableau métriques × colonnes (une colonne par configuration comparée).</summary>
    public void Table(IReadOnlyList<(string Column, IReadOnlyDictionary<string, object?> Values)> columns)
    {
        var metrics = columns.SelectMany(c => c.Values.Keys).Distinct().ToList();
        Log("| Mesure | " + string.Join(" | ", columns.Select(c => c.Column)) + " |");
        Log("|---|" + string.Concat(Enumerable.Repeat("---|", columns.Count)));
        foreach (var metric in metrics)
        {
            Log($"| {metric} | " + string.Join(" | ", columns.Select(c => Fmt(c.Values.GetValueOrDefault(metric)))) + " |");
        }
        Log("");
    }

    public void Comparison(SnapshotComparison comparison, string title)
    {
        Log($"## {title}");
        Log("");
        Log("```");
        Log(Presenter.ComparisonToText(comparison));
        Log("```");
        Log("");
    }

    public string Write()
    {
        var path = Path.Combine(OutDir, "rapport.md");
        File.WriteAllText(path, string.Join("\n", _lines) + "\n", new UTF8Encoding(false));
        return path;
    }

    public static string Fmt(object? value) => value switch
    {
        null => "—",
        double d => d.ToString("0.00", CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "—",
    };

    public static IReadOnlyDictionary<string, object?> Merge(IReadOnlyDictionary<string, object?> first, IReadOnlyDictionary<string, object?> second)
    {
        var merged = new Dictionary<string, object?>(first);
        foreach (var (key, value) in second)
        {
            merged[key] = value;
        }
        return merged;
    }
}

public static class Experiments
{
    public static readonly string[] Names = { "cace-decoupage", "changement-embeddings", "changement-generateur", "prompt-v2", "stabilite" };

    public static int Run(string name, Args args, AppConfig config, string configPath, Action<string> log)
    {
        var questionsPath = args.Value("--questions") ?? "eval/questions.json";
        var questions = EvalQuestions.Load(EvalQuestions.Resolve(questionsPath));
        if (args.Value("--limit") is { } limit && int.Parse(limit, CultureInfo.InvariantCulture) is var n && n > 0)
        {
            questions = questions.Take(n).ToList();
        }
        var exp = new Experiment(name, config, questions, args.Value("--out"), questionsPath, configPath, log);
        switch (name)
        {
            case "cace-decoupage": CaceDecoupage(exp, args); break;
            case "changement-embeddings": ChangementEmbeddings(exp, args); break;
            case "changement-generateur": ChangementGenerateur(exp, args); break;
            case "prompt-v2": PromptV2(exp, args); break;
            case "stabilite": Stabilite(exp, args); break;
            default: throw new ArgumentException($"expérience inconnue : {name} (connues : {string.Join(", ", Names)})");
        }
        Console.WriteLine($"Rapport : {exp.Write()}");
        return 0;
    }

    private static void CaceDecoupage(Experiment exp, Args args)
    {
        var maxChars = int.Parse(args.Value("--max-chars") ?? "300", CultureInfo.InvariantCulture);
        var overlap = int.Parse(args.Value("--overlap-chars") ?? "50", CultureInfo.InvariantCulture);
        var before = (exp.Config.SplitterMaxChars, exp.Config.SplitterOverlapChars);

        Console.WriteLine($"Avant : {before.SplitterMaxChars} / {before.SplitterOverlapChars}");
        var (containerA, manifestA, secondsA) = exp.Index("avant");
        var (snapshotA, _) = exp.Record("avant", containerA);
        Console.WriteLine($"Après : {maxChars} / {overlap}");
        var (containerB, manifestB, secondsB) = exp.Index("apres", new Overrides(SplitterMaxChars: maxChars, SplitterOverlapChars: overlap));
        var (snapshotB, _) = exp.Record("apres", containerB);

        var comparison = SnapshotComparer.Compare(snapshotA, snapshotB);
        exp.Log("Une seule chose change : la taille des morceaux. Même corpus, même modèle d'embeddings, même seuil, même prompt, même générateur.");
        exp.Log("");
        exp.Log("## Les deux index");
        exp.Log("");
        exp.Table(new[]
        {
            ($"avant ({before.SplitterMaxChars} / {before.SplitterOverlapChars})", Experiment.Merge(new Dictionary<string, object?>
            {
                ["morceaux"] = manifestA.ChunkCount, ["indexation (s)"] = Math.Round(secondsA, 1), ["identifiant de l'index"] = manifestA.IndexId,
            }, exp.Stats(snapshotA))),
            ($"après ({maxChars} / {overlap})", Experiment.Merge(new Dictionary<string, object?>
            {
                ["morceaux"] = manifestB.ChunkCount, ["indexation (s)"] = Math.Round(secondsB, 1), ["identifiant de l'index"] = manifestB.IndexId,
            }, exp.Stats(snapshotB))),
        });
        Drift(exp, comparison);
        exp.Log("## Lecture");
        exp.Log("");
        exp.Log("- L'identifiant de l'index change : le découpage fait partie de ce qui définit un index (manifeste), au même titre que le corpus et le modèle.");
        exp.Log("- Le seuil de pertinence n'a pas été recalibré : des morceaux plus courts donnent des scores différents, donc des refus et des réponses qui bougent sans qu'aucune règle métier n'ait changé. C'est le principe CACE : *changing anything changes everything*.");
        exp.Log("- Ce que le taux de dérive ne dit pas : laquelle des deux versions répond le mieux. Pour cela, regarder « bonne source » et « refus justes » ci-dessus, question par question.");
    }

    private static void ChangementEmbeddings(Experiment exp, Args args)
    {
        var other = args.Value("--other") ?? throw new ArgumentException("changement-embeddings attend --other <alias du second modèle d'embeddings>");
        var first = exp.Config.EmbeddingModel;

        Console.WriteLine($"Avant : {first}");
        var (containerA, manifestA, secondsA) = exp.Index("avant");
        var (snapshotA, _) = exp.Record("avant", containerA);

        // 1. Le même index interrogé avec l'autre modèle : refus explicite.
        string? mismatch = null;
        try
        {
            var q = exp.Questions[0];
            exp.Build("avant", new Overrides(EmbeddingModel: other)).AskQuestion.Execute(exp.Config.User(q.UserName), q.Question);
        }
        catch (IndexModelMismatchException error)
        {
            mismatch = error.Message;
        }
        catch (AiServiceException error)
        {
            throw new ArgumentException($"le service IA ne sert pas « {other} » : {error.Message}");
        }
        Console.WriteLine("  sans réindexer : " + (mismatch ?? "AUCUNE ERREUR (inattendu)"));

        // 2. Réindexation, puis mêmes questions.
        Console.WriteLine($"Après : {other}");
        var (containerB, manifestB, secondsB) = exp.Index("apres", new Overrides(EmbeddingModel: other));
        var (snapshotB, _) = exp.Record("apres", containerB);

        var comparison = SnapshotComparer.Compare(snapshotA, snapshotB);
        exp.Log($"Une seule chose change : le modèle d'embeddings, `{first}` → `{other}`. Même corpus, même découpage, même prompt, même générateur ; le seuil est celui configuré pour chaque modèle.");
        exp.Log("");
        exp.Log("## 1. Sans réindexer : l'application refuse");
        exp.Log("");
        exp.Log("```");
        exp.Log(mismatch ?? "aucune erreur levée");
        exp.Log("```");
        exp.Log("");
        exp.Log("## 2. Après réindexation");
        exp.Log("");
        exp.Table(new[]
        {
            ($"avant (`{first}`)", Experiment.Merge(new Dictionary<string, object?>
            {
                ["modèle servi"] = manifestA.EmbeddingModel, ["dimension"] = manifestA.Dimension, ["morceaux"] = manifestA.ChunkCount,
                ["indexation (s)"] = Math.Round(secondsA, 1), ["seuil"] = containerA.Settings.MinScore,
            }, exp.Stats(snapshotA))),
            ($"après (`{other}`)", Experiment.Merge(new Dictionary<string, object?>
            {
                ["modèle servi"] = manifestB.EmbeddingModel, ["dimension"] = manifestB.Dimension, ["morceaux"] = manifestB.ChunkCount,
                ["indexation (s)"] = Math.Round(secondsB, 1), ["seuil"] = containerB.Settings.MinScore,
            }, exp.Stats(snapshotB))),
        });
        Drift(exp, comparison);
        exp.Log("## Lecture");
        exp.Log("");
        exp.Log("- Changer de modèle d'embeddings coûte une réindexation complète (voir la durée) : les vecteurs stockés vivent dans l'espace du modèle qui les a produits.");
        exp.Log("- Le refus est explicite parce que le service IA renvoie l'identifiant concret du modèle et que l'application le compare au manifeste à chaque question (ADR 0003). Sans cela, à dimension égale, l'index aurait répondu à côté sans rien signaler.");
        exp.Log("- Après réindexation, le générateur n'a pas changé et pourtant les réponses bougent : il ne répond qu'à partir de ce que la recherche lui donne.");
    }

    private static void ChangementGenerateur(Experiment exp, Args args)
    {
        var other = args.Value("--other") ?? throw new ArgumentException("changement-generateur attend --other <alias du second modèle de génération>");
        var first = exp.Config.GenerationModel;

        var (containerA, manifest, _) = exp.Index("partage");
        Console.WriteLine($"Avant : {first}");
        var (snapshotA, secondsA) = exp.Record("avant", containerA);
        Console.WriteLine($"Après : {other}");
        var containerB = exp.Build("partage", new Overrides(GenerationModel: other));
        var (snapshotB, secondsB) = exp.Record("apres", containerB);
        var sameIndex = Equals(snapshotA.Configuration.GetValueOrDefault("index_id"), snapshotB.Configuration.GetValueOrDefault("index_id"));

        static int Rejected(Snapshot snapshot) => snapshot.Entries.Count(e => e.Status == "unsourced");

        var comparison = SnapshotComparer.Compare(snapshotA, snapshotB);
        exp.Log($"Une seule chose change : le modèle de génération, `{first}` → `{other}`. L'index `{manifest.IndexId}` est construit une fois et partagé : "
                + (sameIndex ? "**il n'est reconstruit à aucun moment**." : "ATTENTION, index différent."));
        exp.Log("");
        exp.Log("## Les deux générateurs");
        exp.Log("");
        exp.Table(new[]
        {
            ($"avant (`{first}`)", Experiment.Merge(new Dictionary<string, object?>
            {
                ["modèle servi"] = snapshotA.Configuration.GetValueOrDefault("generation_model_id"), ["durée totale (s)"] = Math.Round(secondsA, 1),
                ["par question (s)"] = Math.Round(secondsA / Math.Max(1, exp.Questions.Count), 1), ["réponses non sourcées"] = Rejected(snapshotA),
            }, exp.Stats(snapshotA))),
            ($"après (`{other}`)", Experiment.Merge(new Dictionary<string, object?>
            {
                ["modèle servi"] = snapshotB.Configuration.GetValueOrDefault("generation_model_id"), ["durée totale (s)"] = Math.Round(secondsB, 1),
                ["par question (s)"] = Math.Round(secondsB / Math.Max(1, exp.Questions.Count), 1), ["réponses non sourcées"] = Rejected(snapshotB),
            }, exp.Stats(snapshotB))),
        });
        Drift(exp, comparison);
        exp.Log("## Lecture");
        exp.Log("");
        exp.Log("- Aucune donnée stockée n'a changé : même index, même manifeste. « Le générateur est un détail » est vrai pour les données.");
        exp.Log("- Mais les textes, la latence et le nombre de sorties rejetées par le garde-fou de forme changent : le prompt est réglé pour un modèle, chaque modèle a ses manies (longueur, langue, raisonnement). Une ligne de configuration, oui ; sans conséquence, non.");
    }

    private static void PromptV2(Experiment exp, Args args)
    {
        var other = args.Value("--other") ?? "answer-v2";
        var first = exp.Config.PromptName;

        var (containerA, manifest, _) = exp.Index("partage");
        Console.WriteLine($"Avant : prompt {first}");
        var (snapshotA, _) = exp.Record("avant", containerA);
        Console.WriteLine($"Après : prompt {other}");
        var containerB = exp.Build("partage", new Overrides(PromptName: other));
        var (snapshotB, _) = exp.Record("apres", containerB);

        static object? MeanLength(Snapshot snapshot)
        {
            var answered = snapshot.Entries.Where(e => e.Status == "answered").Select(e => e.Text.Length).ToList();
            return answered.Count == 0 ? null : (int)Math.Round(answered.Average());
        }

        var comparison = SnapshotComparer.Compare(snapshotA, snapshotB);
        exp.Log($"Une seule chose change : le prompt, `{first}` → `{other}` (versions `{snapshotA.Configuration.GetValueOrDefault("prompt_version")}` → "
                + $"`{snapshotB.Configuration.GetValueOrDefault("prompt_version")}`). Même index `{manifest.IndexId}`, même générateur, même seuil.");
        exp.Log("");
        exp.Log("## Les deux prompts");
        exp.Log("");
        exp.Table(new[]
        {
            ($"avant (`{first}`)", Experiment.Merge(new Dictionary<string, object?>
            {
                ["version tracée"] = snapshotA.Configuration.GetValueOrDefault("prompt_version"), ["longueur moyenne des réponses"] = MeanLength(snapshotA),
            }, exp.Stats(snapshotA))),
            ($"après (`{other}`)", Experiment.Merge(new Dictionary<string, object?>
            {
                ["version tracée"] = snapshotB.Configuration.GetValueOrDefault("prompt_version"), ["longueur moyenne des réponses"] = MeanLength(snapshotB),
            }, exp.Stats(snapshotB))),
        });
        Drift(exp, comparison);
        exp.Log("## Lecture");
        exp.Log("");
        exp.Log("- La version du prompt (déclarée + empreinte du contenu) est dans chaque trace : la dérive est attribuable à cette seule modification.");
        exp.Log("- Ce que le prompt change (forme, longueur, ton) n'est pas ce que le domaine garantit (citations vérifiées, forme validée, droits filtrés) : c'est ce qui permet de le traiter comme une configuration *surveillée comme du métier* (ADR 0005).");
        exp.Log("- Avec `extractive` (hors-ligne), 0 % de dérive : ce générateur ignore les consignes. Un modèle de langage, lui, les suit — et c'est précisément ce qui rend le prompt sensible.");
    }

    private static void Stabilite(Experiment exp, Args args)
    {
        var runs = int.Parse(args.Value("--runs") ?? "3", CultureInfo.InvariantCulture);
        if (runs < 2)
        {
            throw new ArgumentException("--runs doit valoir au moins 2 : il faut deux passages pour mesurer une dérive");
        }
        int? seed = args.Value("--seed") is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : null;

        var (container, manifest, _) = exp.Index("partage", new Overrides(Seed: seed));
        var snapshots = Enumerable.Range(1, runs).Select(i => exp.Record($"passage-{i}", container).Snapshot).ToList();

        exp.Log($"Rien ne change entre les passages : même index `{manifest.IndexId}`, même générateur `{snapshots[0].Configuration.GetValueOrDefault("generation_model_id")}`, "
                + $"même prompt, même seuil, température {exp.Config.Temperature}, graine {(seed?.ToString(CultureInfo.InvariantCulture) ?? "aucune")}.");
        exp.Log("");
        exp.Log("## Chaque passage comparé au premier");
        exp.Log("");
        var rows = new List<(string, IReadOnlyDictionary<string, object?>)>();
        for (var i = 2; i <= runs; i++)
        {
            rows.Add(($"passage 1 → {i}", Experiment.DriftSummary(SnapshotComparer.Compare(snapshots[0], snapshots[i - 1]))));
        }
        exp.Table(rows);
        var drifts = rows.Select(r => r.Item2["taux de dérive"] as double?).Where(d => d is not null).Select(d => d!.Value).ToList();
        var meanDrift = drifts.Count == 0 ? (double?)null : Math.Round(drifts.Average(), 3);
        var statuses = rows.Sum(r => (int)r.Item2["changements de statut"]!);
        exp.Log($"**Dérive moyenne à configuration constante : {(meanDrift is null ? "—" : meanDrift.Value.ToString("0.###", CultureInfo.InvariantCulture))}** "
                + $"({statuses} changement(s) de statut sur {rows.Count} comparaison(s)).");
        exp.Log("");
        exp.Log("## Statuts par question");
        exp.Log("");
        exp.Log("| Question | " + string.Join(" | ", Enumerable.Range(1, runs).Select(i => $"passage {i}")) + " |");
        exp.Log("|---|" + string.Concat(Enumerable.Repeat("---|", runs)));
        foreach (var q in exp.Questions)
        {
            var cells = snapshots.Select(snapshot => snapshot.Entries.FirstOrDefault(e => e.QuestionId == q.Id)?.Status ?? "—").ToList();
            var marker = cells.Distinct().Count() > 1 ? " ⚠" : "";
            exp.Log($"| {q.Id}{marker} | " + string.Join(" | ", cells) + " |");
        }
        exp.Log("");
        exp.Log("## Lecture");
        exp.Log("");
        exp.Log("- C'est la mesure de base de la séquence 3.1 : un test par assertion exacte sur ces réponses échouerait au hasard. On teste donc une *proportion* (taux de réponses sourcées, de refus justes) avec une tolérance, et on documente la probabilité de faux échec.");
        exp.Log("- Les reformulations sont attendues ; les changements de statut (⚠) sont ce qu'un test statistique doit borner.");
        exp.Log("- Une graine réduit la variabilité pour un même modèle et un même moteur ; elle ne garantit rien d'un modèle à l'autre, ni d'une version d'Ollama à l'autre.");
    }

    private static void Drift(Experiment exp, SnapshotComparison comparison)
    {
        exp.Log("## Dérive");
        exp.Log("");
        exp.Table(new[] { ("avant → après", (IReadOnlyDictionary<string, object?>)Experiment.DriftSummary(comparison)) });
        exp.Comparison(comparison, "Détail");
    }
}
