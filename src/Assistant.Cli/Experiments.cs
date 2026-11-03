// Expériences reproductibles. Chacune change une seule chose, enregistre des instantanés et écrit
// un rapport Markdown. Elles rejouent sur l'application propre les « ruptures » de la problématique :
// cace-decoupage et changement-embeddings pour la dépendance de données instable, stabilite pour le
// non-déterminisme, changement-generateur et prompt-v2 pour ce que le générateur et le prompt changent
// malgré tout.
//
//     assistant experience cace-decoupage        --config config/app-ollama.json --questions eval/questions-service-public.json
//     assistant experience changement-embeddings --other nomic
//     assistant experience changement-generateur --other qwen3-4b
//     assistant experience prompt-v2             [--other answer-v2]
//     assistant experience stabilite             [--runs 3] [--seed 42]

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
    private readonly Action<string> _log;
    private readonly Overrides _common;

    /// <summary>
    /// Crée le dossier de sortie <paramref name="outDir"/>. <paramref name="newOutDir"/> est vrai pour le dossier par
    /// défaut, daté à la seconde, qui est alors toujours neuf : si le nom est pris, on ajoute « -2 », « -3 », et ainsi
    /// de suite (<see cref="AppConfig.CreateNewDir"/>). Un dossier donné par --out peut déjà exister.
    /// </summary>
    public Experiment(string name, AppConfig config, IReadOnlyList<EvalQuestion> questions, string outDir, string questionsPath, string configPath,
                      Action<string> log, Overrides? common = null, bool newOutDir = false)
    {
        Name = name;
        Config = config;
        Questions = questions;
        _log = log;
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
        WarnIfDefaultThreshold(EmbeddingModel);
    }

    /// <summary>
    /// Quand aucun seuil n'est configuré pour cet alias, c'est la valeur `default` qui s'applique. On le signale
    /// dans la console et dans le rapport, comme le font la ligne de commande et le banc d'essai (ADR 0004).
    /// </summary>
    public void WarnIfDefaultThreshold(string embeddingModel)
    {
        if (Config.HasThresholdFor(embeddingModel))
        {
            return;
        }
        var message = $"Attention : aucun seuil de pertinence configuré pour « {embeddingModel} » : valeur `default` "
                      + $"{Config.MinScoreFor(embeddingModel).ToString(CultureInfo.InvariantCulture)} (ADR 0004 : lancer le banc d'essai)";
        Console.Error.WriteLine(message);
        Log($"> {message}");
        Log("");
    }

    /// <summary>Modèles et prompt effectifs : les options communes de la ligne de commande, sinon la configuration.</summary>
    public string EmbeddingModel => _common.EmbeddingModel ?? Config.EmbeddingModel;
    public string GenerationModel => _common.GenerationModel ?? Config.GenerationModel;
    public string PromptName => _common.PromptName ?? Config.PromptName;

    /// <summary>
    /// L'index et les instantanés vivent dans le dossier de l'expérience, rien n'est donc écrasé ailleurs.
    /// Les options communes (--embedding-model, --generation-model, --prompt) s'appliquent, sauf sur ce que
    /// l'expérience change elle-même.
    /// </summary>
    public Container Build(string indexName, Overrides? overrides = null)
    {
        var o = overrides ?? new Overrides();
        return Composition.Build(Config, o with
        {
            EmbeddingModel = o.EmbeddingModel ?? _common.EmbeddingModel,
            GenerationModel = o.GenerationModel ?? _common.GenerationModel,
            PromptName = o.PromptName ?? _common.PromptName,
            IndexPath = Path.Combine(OutDir, $"index-{indexName}.json"),
            SnapshotsDir = Path.Combine(OutDir, "instantanes"),
        }, _log);
    }

    /// <summary>Indexe le corpus et affiche une ligne de progression en console, comme <see cref="Record"/>.</summary>
    public (Container Container, IndexManifest Manifest, double Seconds) Index(string indexName, Overrides? overrides = null)
    {
        var container = Build(indexName, overrides);
        var clock = Stopwatch.StartNew();
        var manifest = container.IndexCorpus.Execute();
        var seconds = clock.Elapsed.TotalSeconds;
        Console.WriteLine($"  index « {indexName} » : {manifest.ChunkCount} morceaux, {manifest.EmbeddingModel}, {manifest.Dimension} dim., "
                          + $"{seconds.ToString("0.0", CultureInfo.InvariantCulture)} s");
        return (container, manifest, seconds);
    }

    public (Snapshot Snapshot, double Seconds) Record(string snapshotName, Container container)
    {
        var clock = Stopwatch.StartNew();
        var snapshot = container.RecordSnapshot.Execute(snapshotName, Questions.Select(q => q.ToSnapshotQuestion(Config)).ToList());
        var seconds = clock.Elapsed.TotalSeconds;
        Console.WriteLine($"  instantané « {snapshotName} » : {snapshot.Entries.Count} réponses en {seconds.ToString("0.0", CultureInfo.InvariantCulture)} s");
        return (snapshot, seconds);
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

    public void Comparison(SnapshotComparison comparison, string title)
    {
        Log($"## {title}");
        Log("");
        Log("```");
        // Ligne à ligne, parce que le rapport s'écrit avec des « \n » même sous Windows, alors que
        // ComparisonToText y mettrait des « \r\n ».
        foreach (var line in Presenter.ComparisonLines(comparison))
        {
            Log(line);
        }
        Log("```");
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

    public static int Run(string name, Args args, AppConfig config, Overrides common, string configPath, Action<string> log)
    {
        if (!Names.Contains(name))
        {
            throw new ArgumentException($"expérience inconnue : {name} (connues : {string.Join(", ", Names)})");
        }
        // Toutes les options sont vérifiées ici, alors que seule la configuration a été lue : une faute de frappe ne
        // coûte ni dossier, ni index, ni appel au service IA. On monte aussi, sans rien calculer, ce que l'expérience
        // utilisera, pour qu'un découpage hors bornes ou un prompt introuvable se voie avant l'indexation et
        // l'instantané « avant ». Le service IA n'est interrogé qu'ensuite : il doit répondre et, pour les deux
        // expériences de changement de modèle, servir l'alias --other avec le bon type. Le seuil `default` n'est
        // annoncé qu'après, par Experiment.
        if (name is "changement-embeddings" or "changement-generateur" && args.Value("--other") is null)
        {
            throw new ArgumentException($"{name} attend --other <alias du second modèle>");
        }
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
        if (name == "cace-decoupage")
        {
            var (maxChars, overlap) = CaceSplitter(args);
            Check(config, common with { SplitterMaxChars = maxChars, SplitterOverlapChars = overlap });
        }
        if (name == "prompt-v2")
        {
            Check(config, common with { PromptName = OtherPrompt(args) });
        }
        AiService.Require(config.AiBaseUrl);
        switch (name)
        {
            case "changement-embeddings":
                AiService.RequireModel(config.AiBaseUrl, "embedding", args.Value("--other")!);
                break;
            case "changement-generateur":
                AiService.RequireModel(config.AiBaseUrl, "generation", args.Value("--other")!);
                break;
        }
        // Sans --out, le dossier va sous la racine du projet et il est toujours neuf (newOutDir).
        var outDir = args.NonEmpty("--out");
        var exp = new Experiment(name, config, questions, outDir ?? AppConfig.ResultsDir($"exp-{name}-", DateTime.Now), questionsPath, configPath,
                                 log, common, newOutDir: outDir is null);
        switch (name)
        {
            case "cace-decoupage": CaceDecoupage(exp, args); break;
            case "changement-embeddings": ChangementEmbeddings(exp, args); break;
            case "changement-generateur": ChangementGenerateur(exp, args); break;
            case "prompt-v2": PromptV2(exp, args); break;
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
    /// Monte la configuration que l'expérience utilisera et lit son prompt, sans rien calculer. Un découpage hors
    /// bornes ou un prompt introuvable se voit ainsi avant l'indexation et l'instantané « avant ». Run l'appelle
    /// aussi pour le découpage de cace-decoupage et pour le second prompt de prompt-v2.
    /// </summary>
    private static void Check(AppConfig config, Overrides overrides)
    {
        var container = Composition.Build(config, overrides);
        container.Prompts.Get(container.Settings.PromptName);
    }

    /// <summary>Le découpage « après » de cace-decoupage : --max-chars et --overlap-chars, sinon 300 et 50.</summary>
    private static (int MaxChars, int Overlap) CaceSplitter(Args args) => (args.Int("--max-chars") ?? 300, args.Int("--overlap-chars") ?? 50);

    /// <summary>Le second prompt de prompt-v2 : --other, sinon answer-v2.</summary>
    private static string OtherPrompt(Args args) => args.Value("--other") ?? "answer-v2";

    private static void CaceDecoupage(Experiment exp, Args args)
    {
        var (maxChars, overlap) = CaceSplitter(args);
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
        var other = args.Value("--other")!;
        exp.WarnIfDefaultThreshold(other);
        var first = exp.EmbeddingModel;

        Console.WriteLine($"Avant : {first}");
        var (containerA, manifestA, secondsA) = exp.Index("avant");
        var (snapshotA, _) = exp.Record("avant", containerA);

        // 1. On interroge le même index avec l'autre modèle : l'application doit refuser explicitement.
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

        // 2. On réindexe avec l'autre modèle, puis on repose les mêmes questions.
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
        var other = args.Value("--other")!;
        var first = exp.GenerationModel;

        var (containerA, manifest, _) = exp.Index("partage");
        Console.WriteLine($"Avant : {first}");
        var (snapshotA, secondsA) = exp.Record("avant", containerA);
        Console.WriteLine($"Après : {other}");
        var containerB = exp.Build("partage", new Overrides(GenerationModel: other));
        var (snapshotB, secondsB) = exp.Record("apres", containerB);
        var sameIndex = Equals(snapshotA.Configuration.GetValueOrDefault("index_id"), snapshotB.Configuration.GetValueOrDefault("index_id"));

        static int Rejected(Snapshot snapshot) => snapshot.Entries.Count(e => e.Status == Experiment.Unsourced);

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
        exp.Log("- Mais les textes, la latence et le nombre de réponses non sourcées, rejets du garde-fou de forme compris, changent : le prompt est réglé pour un modèle, chaque modèle a ses manies (longueur, langue, raisonnement). Une ligne de configuration, oui ; sans conséquence, non.");
    }

    private static void PromptV2(Experiment exp, Args args)
    {
        var other = OtherPrompt(args);
        var first = exp.PromptName;

        var (containerA, manifest, _) = exp.Index("partage");
        Console.WriteLine($"Avant : prompt {first}");
        var (snapshotA, _) = exp.Record("avant", containerA);
        Console.WriteLine($"Après : prompt {other}");
        var containerB = exp.Build("partage", new Overrides(PromptName: other));
        var (snapshotB, _) = exp.Record("apres", containerB);

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
        exp.Log("- Ce que le prompt change (forme, longueur, ton) n'est pas ce que le domaine garantit (citations vérifiées, forme validée, droits filtrés). "
                + "L'ADR 0005 fait la même distinction. Ce dépôt y retient la position suivante : le prompt est construit et versionné dans l'application. "
                + "La séquence 3.3 pose la question que l'ADR laisse ouverte : le prompt est-il une configuration ou de la logique métier ?");
        exp.Log("- Avec `extractive` (hors-ligne), 0 % de dérive : ce générateur ignore les consignes. Un modèle qui ignore le prompt produit exactement cette signature.");
    }

    /// <summary>
    /// La longueur moyenne des réponses données, comptée en points de code Unicode et non en unités UTF-16
    /// (string.Length) : un emoji compte pour un. La moyenne est arrondie à l'entier par Math.Round, qui départage
    /// les égalités vers le pair. Sans réponse donnée, on renvoie null.
    /// </summary>
    public static int? MeanLength(Snapshot snapshot)
    {
        var answered = snapshot.Entries.Where(e => e.Status == Experiment.Answered).Select(e => e.Text.EnumerateRunes().Count()).ToList();
        return answered.Count == 0 ? null : (int)Math.Round(answered.Average());
    }

    private static void Stabilite(Experiment exp, Args args)
    {
        var runs = args.Int("--runs") ?? 3;
        var seed = args.Int("--seed");

        var (container, manifest, _) = exp.Index("partage", new Overrides(Seed: seed));
        var effectiveSeed = container.Settings.Seed;   // --seed, sinon la graine de la configuration
        var snapshots = Enumerable.Range(1, runs).Select(i => exp.Record($"passage-{i}", container).Snapshot).ToList();

        exp.Log($"Rien ne change entre les passages : même index `{manifest.IndexId}`, même générateur `{snapshots[0].Configuration.GetValueOrDefault("generation_model_id")}`, "
                + $"même prompt, même seuil, température {exp.Config.Temperature}, graine {(effectiveSeed?.ToString(CultureInfo.InvariantCulture) ?? "aucune")}.");
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
    /// La lecture du rapport de stabilité suit la mesure. Si aucune réponse n'a bougé, elle n'affirme pas qu'un test par
    /// assertion exacte échouerait au hasard sur ces réponses. Elle dit que ce générateur s'est montré déterministe et
    /// explique comment obtenir des réponses qui bougent.
    /// </summary>
    public static IReadOnlyList<string> StabilityReading(IEnumerable<SnapshotComparison> comparisons)
    {
        var lines = comparisons.Any(c => c.Changed > 0)
            ? new List<string>
            {
                "- C'est la mesure de base de la séquence 3.1 : un test par assertion exacte sur ces réponses échouerait au hasard. On teste donc une *proportion* (taux de réponses sourcées, de refus justes) avec une tolérance, et on documente la probabilité de faux échec.",
                "- Le texte change souvent d'un passage à l'autre, et un « texte modifié » peut inverser la réponse (Oui devenu Non) : il se relit, la couverture des mots-clés le mesure grossièrement. Les changements de statut (⚠) existent aussi à configuration constante : un test statistique doit les borner avec une tolérance, pas exiger zéro.",
            }
            : new List<string>
            {
                "- Aucune réponse n'a bougé d'un passage à l'autre : avec ces réglages, ce générateur s'est montré déterministe. C'est le cas d'`extractive`, le générateur hors-ligne par défaut, et d'`extractive-bruite` quand une graine est fixée. Cette mesure ne montre donc pas le non-déterminisme de la séquence 3.1.",
                "- Sans graine, un vrai modèle ne donne pas toujours la même réponse quand la configuration ne change pas. Hors-ligne, `--generation-model extractive-bruite` sans graine simule ce défaut. "
                + "Sur des réponses qui bougent, un test par assertion exacte échouerait au hasard : on teste alors une *proportion* (taux de réponses sourcées, de refus justes) avec une tolérance, et on documente la probabilité de faux échec.",
            };
        lines.Add("- Une graine réduit la variabilité pour un même modèle et un même moteur ; elle ne garantit rien d'un modèle à l'autre, ni d'une version d'Ollama à l'autre.");
        return lines;
    }

    private static void Drift(Experiment exp, SnapshotComparison comparison)
    {
        exp.Log("## Dérive");
        exp.Log("");
        exp.Table(new[] { ("avant → après", (IReadOnlyDictionary<string, object?>)Experiment.DriftSummary(comparison)) });
        exp.Comparison(comparison, "Détail");
    }
}
