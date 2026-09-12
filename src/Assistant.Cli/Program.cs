// Ligne de commande de l'application (adaptateur entrant).
//
//     dotnet run --project src/Assistant.Cli -- index
//     dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail ?" --user alice -v
//     dotnet run --project src/Assistant.Cli -- status
//     dotnet run --project src/Assistant.Cli -- snapshot record reference --questions eval/questions.json
//     dotnet run --project src/Assistant.Cli -- snapshot compare reference candidat
//     dotnet run --project src/Assistant.Cli -- serve            (API HTTP de l'application, port 8000)
//
// Le service IA (python -m ai_service) doit tourner : c'est un autre programme, dans un autre langage.

using System.Text;
using System.Text.Json.Nodes;
using Assistant.Application;
using Assistant.Domain;

namespace Assistant.Cli;

public static class Program
{
    private const string Usage = """
        Usage : assistant <commande> [options]

        Commandes :
          index                          indexer le corpus   (--if-stale : seulement si status dit « à refaire »)
          ask "<question>"               poser une question   (--user alice, --json, -v)
          status                         l'index est-il cohérent avec le corpus, le découpage et le modèle servi ?   (--json)
          snapshot record <nom>          enregistrer un instantané   (--questions eval/questions.json, --limit N)
          snapshot compare <a> <b>       comparer deux instantanés
          snapshot list                  lister les instantanés
          serve                          API HTTP de l'application   (--host 127.0.0.1, --port 8000, --quiet) : /health, /v1/status, /v1/index, /v1/ask
          benchmark                      mesurer recherche et génération   (--embedding a b, --generation x y — défaut : la configuration ; --questions, --validate-with, --runs, --limit, --min-score config|auto|<n>, --max-chars, --overlap-chars, --seed, --out)
          experience <nom>               une expérience reproductible : cace-decoupage, changement-embeddings, changement-generateur, prompt-v2, stabilite   (--questions, --limit, --out, --other, --max-chars, --overlap-chars, --runs, --seed)

        Options communes : --config <fichier>   --embedding-model <alias>   --generation-model <alias>   --prompt <nom>
        Codes de retour : 0 ok · 1 erreur · status : 2 à refaire, 3 non vérifié · index --if-stale : 3 non vérifié
        """;

    public static int Main(string[] args)
    {
        // Sortie en UTF-8 même redirigée vers un fichier (Windows encoderait en cp1252).
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            return Run(args);
        }
        catch (Exception error) when (error is AssistantApplicationException or DomainException or UnknownUserException
                                           or ArgumentException or InvalidOperationException
                                           or FormatException or OverflowException or NotSupportedException)
        {
            Console.Error.WriteLine($"Erreur : {error.Message}");
            return 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                           or NullReferenceException or KeyNotFoundException)
        {
            Console.Error.WriteLine($"Erreur : fichier illisible ou incomplet (configuration, questions, index) — {error.Message}");
            return 1;
        }
        catch (System.Net.Http.HttpRequestException error)
        {
            Console.Error.WriteLine($"Erreur : service IA — {error.Message}");
            return 1;
        }
        catch (Exception error) when (error is System.Net.HttpListenerException or System.Net.Sockets.SocketException)
        {
            Console.Error.WriteLine($"Erreur : impossible d'écouter (port déjà pris, ou adresse non autorisée) — {error.Message}");
            return 1;
        }
    }

    private static int Run(string[] argv)
    {
        var args = new Args(argv);
        if (args.Positional.Count == 0 || args.Has("--help") || args.Has("-h"))
        {
            Console.WriteLine(Usage);
            return args.Positional.Count == 0 ? 1 : 0;
        }
        var verbose = args.Has("-v") || args.Has("--verbose");
        Action<string> log = verbose || Environment.GetEnvironmentVariable("ASSISTANT_LOG") is "INFO" or "info"
            ? message => Console.Error.WriteLine($"[assistant] {message}")
            : _ => { };

        var config = AppConfig.Load(args.Value("--config"));
        var container = Composition.Build(config, args.Value("--embedding-model"), args.Value("--generation-model"),
                                          promptName: args.Value("--prompt"), log: log);
        switch (args.Positional[0])
        {
            case "index":
            {
                if (args.Has("--if-stale"))
                {
                    // Le cycle « réentraînement » d'un RAG : détecter que l'index ne
                    // correspond plus au corpus, au découpage ou au modèle servi (status),
                    // puis le reconstruire. Rien n'est réappris : on recalcule un dérivé.
                    var report = container.CheckStatus.Execute();
                    if (report.UpToDate)
                    {
                        Console.WriteLine("Index à jour : rien à refaire.");
                        return 0;
                    }
                    if (report.Unverified)
                    {
                        Console.Error.WriteLine("Index non vérifié : le service IA est injoignable, impossible de réindexer.");
                        return 3;
                    }
                    Console.WriteLine("Index à refaire :");
                    foreach (var issue in report.Issues)
                    {
                        Console.WriteLine($"  - {issue}");
                    }
                }
                var manifest = container.IndexCorpus.Execute();
                Console.WriteLine(Presenter.ToJson(Presenter.ManifestToJson(manifest)));
                return 0;
            }
            case "ask":
            {
                if (args.Positional.Count != 2)
                {
                    throw new ArgumentException("ask attend une seule question, entre guillemets");
                }
                var user = config.User(args.Value("--user") ?? "alice");
                var answer = container.AskQuestion.Execute(user, args.Positional[1]);
                Console.WriteLine(args.Has("--json")
                    ? Presenter.ToJson(Presenter.AnswerToJson(answer, includeRaw: verbose))
                    : Presenter.AnswerToText(answer, verbose));
                return 0;
            }
            case "status":
            {
                var report = container.CheckStatus.Execute();
                Console.WriteLine(args.Has("--json") ? Presenter.ToJson(Presenter.StatusToJson(report)) : Presenter.StatusToText(report));
                return report.UpToDate ? 0 : report.Unverified ? 3 : 2;
            }
            case "snapshot":
                return Snapshot(args, config, container);
            case "benchmark":
                return RunBenchmark(args, config, log);
            case "serve":
            {
                var host = args.Value("--host") ?? "127.0.0.1";
                var port = args.Int("--port") ?? 8000;
                using var api = new HttpApi(container, host, port, quiet: args.Has("--quiet"));
                using var stop = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
                api.Start();
                Console.Error.WriteLine($"[application] à l'écoute sur {api.Url} (service IA : {config.AiBaseUrl}) — Ctrl+C pour arrêter");
                stop.Wait();
                api.Stop();
                return 0;
            }
            case "experience":
            {
                if (args.Positional.Count < 2)
                {
                    throw new ArgumentException($"experience attend un nom : {string.Join(", ", Experiments.Names)}");
                }
                var common = new Overrides(args.Value("--embedding-model"), args.Value("--generation-model"), PromptName: args.Value("--prompt"));
                return Experiments.Run(args.Positional[1], args, config, common, args.Value("--config") ?? "config/app.json", log);
            }
            default:
                Console.Error.WriteLine($"Commande inconnue : {args.Positional[0]}");
                Console.WriteLine(Usage);
                return 1;
        }
    }

    private static int RunBenchmark(Args args, AppConfig config, Action<string> log)
    {
        // Sans --embedding / --generation : les modèles de la configuration, comme en Python.
        var embeddings = args.Values("--embedding") is { Count: > 0 } e ? e : new[] { args.Value("--embedding-model") ?? config.EmbeddingModel };
        var generations = args.Values("--generation") is { Count: > 0 } g ? g : new[] { args.Value("--generation-model") ?? config.GenerationModel };
        var minScore = args.Value("--min-score") ?? "config";
        if (minScore is not ("config" or "auto") && !double.TryParse(minScore, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            throw new ArgumentException("--min-score attend config, auto ou un nombre (ex. 0.6)");
        }
        // Les options sont validées avant tout travail : pas d'indexation pour découvrir une faute de frappe.
        var runs = args.Int("--runs") ?? 3;
        var limit = args.Int("--limit");
        var maxChars = args.Int("--max-chars");
        var overlap = args.Int("--overlap-chars");
        var seed = args.Int("--seed");
        var questions = EvalQuestions.Load(EvalQuestions.Resolve(args.Value("--questions") ?? "eval/questions.json"));
        if (limit is > 0)
        {
            questions = questions.Take(limit.Value).ToList();
        }
        var validation = args.Value("--validate-with") is { } path ? EvalQuestions.Load(EvalQuestions.Resolve(path)) : null;
        AiService.Require(config.AiBaseUrl);
        var options = new BenchmarkOptions(
            embeddings, generations, questions, validation,
            Runs: runs,
            OutDir: args.Value("--out") ?? Path.Combine(AppConfig.ProjectRoot, "eval", "resultats", DateTime.Now.ToString("yyyy-MM-dd-HHmmss")),
            MinScoreMode: minScore,
            SplitterMaxChars: maxChars,
            SplitterOverlapChars: overlap,
            Seed: seed,
            PromptName: args.Value("--prompt"));
        Benchmark.Run(config, options, Console.WriteLine);
        return 0;
    }

    private static int Snapshot(Args args, AppConfig config, Container container)
    {
        var sub = args.Positional.Count > 1 ? args.Positional[1] : "";
        switch (sub)
        {
            case "record":
            {
                if (args.Positional.Count < 3)
                {
                    throw new ArgumentException("snapshot record attend un nom");
                }
                var questions = EvalQuestions.Load(EvalQuestions.Resolve(args.Value("--questions") ?? "eval/questions.json"))
                                             .Select(q => q.ToSnapshotQuestion(config)).ToList();
                if (args.Int("--limit") is > 0 and var n)
                {
                    questions = questions.Take(n).ToList();
                }
                var snapshot = container.RecordSnapshot.Execute(args.Positional[2], questions);
                Console.WriteLine($"Instantané « {snapshot.Name} » : {snapshot.Entries.Count} réponses, enregistré dans {config.SnapshotsDir}");
                foreach (var (key, value) in snapshot.Configuration)
                {
                    Console.WriteLine($"  {key} = {value}");
                }
                return 0;
            }
            case "compare":
            {
                if (args.Positional.Count < 4)
                {
                    throw new ArgumentException("snapshot compare attend deux noms");
                }
                var store = container.Snapshots;
                Console.WriteLine(Presenter.ComparisonToText(SnapshotComparer.Compare(store.Load(args.Positional[2]), store.Load(args.Positional[3]))));
                return 0;
            }
            case "list":
            {
                var names = container.Snapshots.Names();
                Console.WriteLine(names.Count > 0 ? string.Join("\n", names) : $"Aucun instantané dans {config.SnapshotsDir}");
                return 0;
            }
            default:
                throw new ArgumentException("snapshot attend record, compare ou list");
        }
    }

}

/// <summary>Le service IA doit répondre avant un banc ou une expérience : on le vérifie d'abord, en une requête.</summary>
public static class AiService
{
    public static void Require(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = http.GetAsync(baseUrl.TrimEnd('/') + "/health").Result;
            if (!response.IsSuccessStatusCode)
            {
                throw new AiServiceException($"le service IA répond HTTP {(int)response.StatusCode} sur {baseUrl}/health", transient: false);
            }
        }
        catch (Exception error) when (error is HttpRequestException or AggregateException or TaskCanceledException)
        {
            throw new AiServiceException($"service IA injoignable ({baseUrl}). Lancez-le d'abord : python -m ai_service", transient: false);
        }
    }
}

/// <summary>Analyse minimale des arguments : positionnels, drapeaux, options à une ou plusieurs valeurs.</summary>
public sealed class Args
{
    private static readonly HashSet<string> Flags = new() { "-v", "--verbose", "--json", "--help", "-h", "--if-stale", "--quiet" };
    private static readonly HashSet<string> MultiValued = new() { "--embedding", "--generation" };
    private readonly Dictionary<string, List<string>> _values = new();
    private readonly HashSet<string> _flags = new();

    public List<string> Positional { get; } = new();

    public Args(string[] argv)
    {
        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (Flags.Contains(arg))
            {
                _flags.Add(arg);
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var equals = arg.IndexOf('=');
                if (equals > 0)
                {
                    Add(arg[..equals], arg[(equals + 1)..]);
                }
                else if (i + 1 < argv.Length && !argv[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    Add(arg, argv[++i]);
                    // Option à plusieurs valeurs (--embedding a b) : on avale jusqu'à la prochaine option.
                    while (MultiValued.Contains(arg) && i + 1 < argv.Length && !argv[i + 1].StartsWith("-", StringComparison.Ordinal))
                    {
                        Add(arg, argv[++i]);
                    }
                }
                else
                {
                    throw new ArgumentException($"l'option {arg} attend une valeur");
                }
            }
            else
            {
                Positional.Add(arg);
            }
        }
    }

    private void Add(string option, string value)
    {
        if (!_values.TryGetValue(option, out var list))
        {
            _values[option] = list = new List<string>();
        }
        list.Add(value);
    }

    public bool Has(string flag) => _flags.Contains(flag);

    public string? Value(string option) => _values.TryGetValue(option, out var list) ? list[^1] : null;

    public IReadOnlyList<string> Values(string option) => _values.GetValueOrDefault(option) ?? new List<string>();

    /// <summary>Valeur entière d'une option, avec un message en français si elle n'en est pas une.</summary>
    public int? Int(string option) =>
        Value(option) is { } value
            ? int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new ArgumentException($"l'option {option} attend un entier, pas « {value} »")
            : null;
}
