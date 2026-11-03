// Expériences reproductibles. Chacune enregistre des instantanés et écrit un rapport Markdown. Elles
// rejouent sur l'application propre les « ruptures » de la problématique : stabilite pose plusieurs fois
// les mêmes questions, sans rien changer, pour mesurer le non-déterminisme.
//
//     assistant experience stabilite             [--runs 3]

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Assistant.Application;
using Assistant.Domain;

namespace Assistant.Cli;

public sealed class Experiment
{
    public string Name { get; }
    public string OutDir { get; }
    public AppConfig Config { get; }
    public IReadOnlyList<EvalQuestion> Questions { get; }
    private readonly List<string> _lines = new();
    private readonly Overrides _common;

    /// <summary>
    /// Crée le dossier de sortie <paramref name="outDir"/>. <paramref name="newOutDir"/> est vrai pour le dossier par
    /// défaut, daté à la seconde, qui est alors toujours neuf : si le nom est pris, on ajoute « -2 », « -3 », et ainsi
    /// de suite (<see cref="AppConfig.CreateNewDir"/>). Un dossier donné par --out peut déjà exister.
    /// </summary>
    public Experiment(string name, AppConfig config, IReadOnlyList<EvalQuestion> questions, string outDir, string questionsPath, string configPath,
                      Overrides? common = null, bool newOutDir = false)
    {
        Name = name;
        Config = config;
        Questions = questions;
        _common = common ?? new Overrides();
        if (questions.Count == 0)
        {
            throw new ArgumentException($"aucune question dans {questionsPath}");
        }
        OutDir = newOutDir ? AppConfig.CreateNewDir(outDir) : outDir;
        Directory.CreateDirectory(OutDir);
        Log($"# Expérience « {name} » — {DateTime.Now:yyyy-MM-dd HH:mm}");
        Log("");
        // Les chemins tels qu'on les a donnés, avec des « / » : le rapport est le même sous Windows et Linux.
        Log($"Configuration `{configPath.Replace('\\', '/')}` · {questions.Count} questions de `{questionsPath.Replace('\\', '/')}` · "
            + $"corpus `{Path.GetFileName(config.CorpusDir.TrimEnd('/', '\\'))}`.");
        Log("");
    }

    /// <summary>
    /// L'index et les instantanés vivent dans le dossier de l'expérience, rien n'est donc écrasé ailleurs.
    /// Les options communes (--embedding-model, --generation-model) s'appliquent.
    /// </summary>
    public Container Build(string indexName) =>
        Composition.Build(Config with { IndexPath = Path.Combine(OutDir, $"index-{indexName}.json") },
                          _common with { SnapshotsDir = Path.Combine(OutDir, "instantanes") });

    /// <summary>Indexe le corpus et affiche une ligne de progression en console, comme <see cref="Record"/>.</summary>
    public (Container Container, IndexSummary Summary) Index(string indexName)
    {
        var container = Build(indexName);
        var clock = Stopwatch.StartNew();
        var summary = container.IndexCorpus.Execute();
        var seconds = clock.Elapsed.TotalSeconds;
        Console.WriteLine($"  index « {indexName} » : {summary.ChunkCount} morceaux, "
                          + $"{seconds.ToString("0.0", CultureInfo.InvariantCulture)} s");
        return (container, summary);
    }

    public Snapshot Record(string snapshotName, Container container)
    {
        var clock = Stopwatch.StartNew();
        var snapshot = container.RecordSnapshot.Execute(snapshotName, Questions.Select(q => q.ToSnapshotQuestion(Config)).ToList());
        var seconds = clock.Elapsed.TotalSeconds;
        Console.WriteLine($"  instantané « {snapshotName} » : {snapshot.Entries.Count} réponses en {seconds.ToString("0.0", CultureInfo.InvariantCulture)} s");
        return snapshot;
    }

    /// <summary>Les noms de statut des instantanés viennent du domaine, plutôt que de chaînes recopiées.</summary>
    public static readonly string Answered = StatusNames.Of(AnswerStatus.Answered),
        NoRelevantSource = StatusNames.Of(AnswerStatus.NoRelevantSource), Unsourced = StatusNames.Of(AnswerStatus.Unsourced);

    /// <summary>Six mesures lues dans un instantané, à partir de ce que les questions attendent.</summary>
    public Dictionary<string, object?> Stats(Snapshot snapshot)
    {
        var byId = Questions.ToDictionary(q => q.Id);
        var entries = snapshot.Entries.Where(e => byId.ContainsKey(e.QuestionId)).ToList();
        var answerable = entries.Where(e => byId[e.QuestionId].Answerable).ToList();
        var unanswerable = entries.Where(e => !byId[e.QuestionId].Answerable).ToList();
        var sourced = answerable.Where(e => e.Status == Answered && byId[e.QuestionId].ExpectedDocuments.Count > 0).ToList();
        var coverages = answerable.Where(e => e.Status == Answered)
            .Select(e => Keywords.Coverage(e.Text, byId[e.QuestionId].ExpectedKeywords))
            .OfType<double>().ToList();
        return new Dictionary<string, object?>
        {
            ["répond (répondables)"] = Rate(answerable, e => e.Status == Answered),
            ["bonne source"] = Rate(sourced, e => e.CitedDocuments.Intersect(byId[e.QuestionId].ExpectedDocuments).Any()),
            // Une mesure grossière du contenu, utile parce que le taux de dérive seul ne distingue pas une
            // reformulation d'une réponse inversée.
            ["mots-clés (réponses données)"] = coverages.Count == 0 ? null : Math.Round(coverages.Average(), 2),
            ["refus justes (sans réponse accessible)"] = Rate(unanswerable, e => e.Status == NoRelevantSource),
            ["non sourcé"] = Rate(entries, e => e.Status == Unsourced),
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
        ["textes modifiés (à relire)"] = comparison.Count(DifferenceKind.TextChanged),
    };

    public void Log(string line) => _lines.Add(line);

    /// <summary>Un tableau avec une ligne par métrique et une colonne par configuration comparée.</summary>
    public void Table(IReadOnlyList<(string Column, IReadOnlyDictionary<string, object?> Values)> columns)
    {
        var metrics = columns.SelectMany(c => c.Values.Keys).Distinct().ToList();
        Log("| | " + string.Join(" | ", columns.Select(c => c.Column)) + " |");
        Log("|---|" + string.Concat(Enumerable.Repeat("---|", columns.Count)));
        foreach (var metric in metrics)
        {
            Log($"| {metric} | " + string.Join(" | ", columns.Select(c => Fmt(c.Values.GetValueOrDefault(metric)))) + " |");
        }
        Log("");
    }

    public string Write()
    {
        var path = Path.Combine(OutDir, "rapport.md");
        File.WriteAllText(path, string.Join("\n", _lines) + "\n", new UTF8Encoding(false));
        return path;
    }

    /// <summary>Une valeur du rapport : « — » pour null, deux décimales pour un réel, le reste tel quel.</summary>
    public static string Fmt(object? value) => value switch
    {
        null => "—",
        double d => d.ToString("0.00", CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "—",
    };
}

public static class Experiments
{
    public static readonly string[] Names = { "stabilite" };

    public static int Run(string name, Args args, AppConfig config, Overrides common, string configPath)
    {
        if (!Names.Contains(name))
        {
            throw new ArgumentException($"expérience inconnue : {name} (connues : {string.Join(", ", Names)})");
        }
        // Toutes les options sont vérifiées ici, alors que seule la configuration a été lue : une faute de frappe ne
        // coûte ni dossier, ni index, ni appel au service IA. On monte aussi, sans rien calculer, ce que l'expérience
        // utilisera, pour qu'un prompt introuvable se voie avant l'indexation et le premier instantané. Le service IA
        // n'est interrogé qu'ensuite : il doit répondre.
        if (name == "stabilite" && (args.Int("--runs") ?? 3) < 2)
        {
            throw new ArgumentException("--runs doit valoir au moins 2 : il faut deux passages pour mesurer une dérive");
        }
        var questionsPath = args.NonEmpty("--questions") ?? "eval/questions.json";
        var questions = EvalQuestions.Limit(EvalQuestions.Load(EvalQuestions.Resolve(questionsPath)), args.Int("--limit"));
        foreach (var q in questions)
        {
            config.User(q.UserName);   // un utilisateur inconnu lève UnknownUserException avant tout travail
        }
        Check(config, common);
        AiService.Require(config.AiBaseUrl);
        // Sans --out, le dossier va sous la racine du projet et il est toujours neuf (newOutDir).
        var outDir = args.NonEmpty("--out");
        var exp = new Experiment(name, config, questions, outDir ?? AppConfig.ResultsDir($"exp-{name}-", DateTime.Now), questionsPath, configPath,
                                 common, newOutDir: outDir is null);
        switch (name)
        {
            case "stabilite": Stabilite(exp, args); break;
            default: throw new ArgumentException($"expérience inconnue : {name} (connues : {string.Join(", ", Names)})");
        }
        // Une fois le rapport écrit, on affiche une ligne vide puis son chemin, avec des « / » pour qu'il soit le
        // même sous Windows et Linux. La ligne vide passe par WriteLine plutôt que par un « \n » dans la chaîne,
        // pour garder la fin de ligne de la plateforme.
        var report = exp.Write().Replace('\\', '/');
        Console.WriteLine();
        Console.WriteLine($"Rapport : {report}");
        return 0;
    }

    /// <summary>
    /// Monte la configuration que l'expérience utilisera et lit son prompt, sans rien calculer. Un prompt introuvable
    /// se voit ainsi avant l'indexation et le premier instantané.
    /// </summary>
    private static void Check(AppConfig config, Overrides overrides)
    {
        var container = Composition.Build(config, overrides);
        container.Prompts.Get(container.Settings.PromptName);
    }

    private static void Stabilite(Experiment exp, Args args)
    {
        var runs = args.Int("--runs") ?? 3;

        var (container, summary) = exp.Index("partage");
        var snapshots = Enumerable.Range(1, runs).Select(i => exp.Record($"passage-{i}", container)).ToList();

        exp.Log($"Rien ne change entre les passages : même index ({summary.ChunkCount} morceaux, `{snapshots[0].Configuration.GetValueOrDefault("embedding_model")}`), "
                + $"même générateur `{snapshots[0].Configuration.GetValueOrDefault("generation_model")}`, "
                + $"même prompt, même seuil, température {exp.Config.Temperature}.");
        exp.Log("");
        exp.Log("## Chaque passage comparé au premier");
        exp.Log("");
        var comparisons = Enumerable.Range(2, Math.Max(0, runs - 1))
            .Select(i => ($"passage 1 → {i}", SnapshotComparer.Compare(snapshots[0], snapshots[i - 1]))).ToList();
        exp.Table(comparisons.Select(c => (c.Item1, (IReadOnlyDictionary<string, object?>)Experiment.DriftSummary(c.Item2))).ToList());
        var drifts = comparisons.Select(c => c.Item2.DriftRate).OfType<double>().ToList();
        var meanDrift = drifts.Count == 0 ? "—" : Math.Round(drifts.Average(), 3).ToString("0.###", CultureInfo.InvariantCulture);
        var statuses = comparisons.Sum(c => c.Item2.Count(DifferenceKind.StatusChanged));
        exp.Log($"**Dérive moyenne à configuration constante : {meanDrift}** "
                + $"({statuses} changement(s) de statut sur {comparisons.Count} comparaison(s)).");
        exp.Log("");
        exp.Log("## Indicateurs par passage");
        exp.Log("");
        exp.Table(snapshots.Select((snapshot, i) => ($"passage {i + 1}", (IReadOnlyDictionary<string, object?>)exp.Stats(snapshot))).ToList());
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
        foreach (var line in StabilityReading(comparisons.Select(c => c.Item2)))
        {
            exp.Log(line);
        }
    }

    /// <summary>
    /// La lecture du rapport de stabilité décrit la mesure, et rien de plus. Si aucune réponse n'a bougé, elle dit que
    /// ce générateur s'est montré déterministe et explique comment obtenir des réponses qui bougent. Ce qu'une dérive
    /// mesurée change pour les tests, elle ne le dit pas : c'est la question de l'exercice S3.1.
    /// </summary>
    public static IReadOnlyList<string> StabilityReading(IEnumerable<SnapshotComparison> comparisons)
    {
        return comparisons.Any(c => c.Changed > 0)
            ? new List<string>
            {
                "- Des réponses ont bougé alors que rien n'a changé entre les passages : ni l'index, ni le générateur, ni le prompt, ni le seuil, ni la température. Le taux de dérive compte les réponses modifiées, et les questions marquées ⚠ n'ont pas gardé le même statut.",
                "- Le texte change souvent d'un passage à l'autre, et un « texte modifié » peut inverser la réponse (Oui devenu Non) : il se relit, la couverture des mots-clés le mesure grossièrement.",
            }
            : new List<string>
            {
                "- Aucune réponse n'a bougé d'un passage à l'autre : avec ces réglages, ce générateur s'est montré déterministe. C'est le cas d'`extractive`, le générateur hors-ligne par défaut. Cette mesure ne montre donc pas le non-déterminisme de la séquence 3.1.",
                "- Un vrai modèle ne donne pas toujours la même réponse quand la configuration ne change pas. Hors-ligne, `--generation-model extractive-bruite` simule ce défaut.",
            };
    }
}
