// Ligne de commande de l'application (adaptateur entrant).
//
//     dotnet run --project src/Assistant.Cli -- index [--if-stale]
//     dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail ?" --user alice -v
//     dotnet run --project src/Assistant.Cli -- status
//     dotnet run --project src/Assistant.Cli -- snapshot record reference --questions eval/questions.json
//     dotnet run --project src/Assistant.Cli -- snapshot compare reference candidat
//     dotnet run --project src/Assistant.Cli -- serve            (API HTTP de l'application, port 8000)
//
// Le service IA (python -m ai_service) doit tourner : c'est un autre programme, dans un autre langage.

using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Assistant.Application;
using Assistant.Domain;

namespace Assistant.Cli;

public static class Program
{
    private const string Usage = """
        Usage : assistant <commande> [options]

        Commandes :
          index                          indexer le corpus   (--if-stale : seulement si status dit « à refaire »)
          ask "<question>"               poser une question   (--user <nom>, défaut : alice ; --json ; -v : trace complète)
          status                         l'index est-il cohérent avec le corpus, le découpage et le modèle servi ?   (--json)
          snapshot record <nom>          enregistrer un instantané   (--questions eval/questions.json, --limit N, au moins 1)
          snapshot compare <a> <b>       comparer deux instantanés
          snapshot list                  lister les instantanés
          serve                          API HTTP de l'application   (--host 127.0.0.1, --port 8000, --quiet) : /health, /v1/status, /v1/index, /v1/ask
          benchmark                      mesurer recherche et génération   (--embedding a b, --generation x y — défaut : la configuration ; --questions, --validate-with, --runs, --limit, --min-score config|auto|<n de [-1, 1]>, --max-chars, --overlap-chars, --seed, --out)
          experience <nom>               une expérience reproductible   (--questions, --limit, --out, et selon l'expérience :)
                                           cace-decoupage (--max-chars, --overlap-chars) · changement-embeddings --other <alias>
                                           changement-generateur --other <alias> · prompt-v2 (--other <prompt>) · stabilite (--runs, --seed)

        Options communes : --config <fichier>   --embedding-model <alias>   --generation-model <alias>   --prompt <nom>
                           -v, --verbose : journal des décorateurs (toutes les commandes, sauf benchmark)
        La commande vient d'abord, puis ses options. Une option inconnue, abrégée ou d'une autre commande, ou un argument en trop, est une erreur.
        « -- » termine les options : ce qui suit est un argument, même un mot qui commence par un tiret (ask -- -télétravail) ; sans « -- », un tel mot n'est un argument que s'il est « - », un nombre négatif, ou si son nom (ce qui précède un éventuel « = ») contient une espace (ask "-vingt degrés ?").
        Codes de retour : 0 ok · 1 erreur, saisie comprise · status : 2 à refaire, 3 non vérifié · index --if-stale : 3 non vérifié
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
                                           or ConfigException or ArgumentException or FormatException)
        {
            // Configuration, corpus, index ou questions mal formés : des messages qui nomment le fichier.
            Console.Error.WriteLine($"Erreur : {error.Message}");
            return 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"Erreur : fichier illisible (configuration, questions, index) — {error.Message}");
            return 1;
        }
        catch (System.Net.Http.HttpRequestException error)
        {
            Console.Error.WriteLine($"Erreur : service IA — {error.Message}");
            return 1;
        }
        catch (Exception error) when (error is System.Net.HttpListenerException or System.Net.Sockets.SocketException)
        {
            Console.Error.WriteLine($"Erreur : impossible d'écouter (port déjà pris, adresse inconnue ou non autorisée) — {error.Message}");
            return 1;
        }
    }

    private static int Run(string[] argv)
    {
        var args = new Args(argv);
        if (args.Has("--help") || args.Has("-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }
        args.EnsureCommandFirst();
        if (args.Positional.Count == 0)
        {
            Console.Error.WriteLine(Usage);
            return 1;
        }
        var command = Find(args.Positional);
        if (command is null)
        {
            Console.Error.WriteLine($"Commande inconnue : {args.Positional[0]}");
            Console.WriteLine(Usage);
            return 1;
        }
        // Une option mal tapée (« --usr bruno », « -json »), celle d'une autre commande ou un argument en trop
        // est une erreur, pas une saisie ignorée en silence : tout est vérifié avant de lire la configuration.
        args.EnsureOnly(command.Name, command.Options);
        // Puis les entiers, chaque valeur dans l'ordre de la ligne : avant un argument manquant ou en trop, et avant
        // toute autre vérification de valeur.
        args.EnsureIntegers();
        command.EnsureArguments(args.Positional);
        if (args.Int("--port") is { } requested && requested is < 1 or > 65535)
        {
            throw new ArgumentException($"l'option --port attend un port entre 1 et 65535, pas « {requested} »");
        }
        var verbose = args.Has("-v") || args.Has("--verbose");
        Action<string> log = verbose || Environment.GetEnvironmentVariable("ASSISTANT_LOG") is "INFO" or "info"
            ? message => Console.Error.WriteLine($"[assistant] {message}")
            : _ => { };

        var config = AppConfig.Load(args.NonEmpty("--config"));
        var container = Composition.Build(config, args.NonEmpty("--embedding-model"), args.NonEmpty("--generation-model"),
                                          promptName: args.NonEmpty("--prompt"), log: log);
        if (command.Searches && !config.HasThresholdFor(container.EmbeddingModel))
        {
            Console.Error.WriteLine($"Attention : aucun seuil de pertinence configuré pour « {container.EmbeddingModel} » : valeur `default` "
                                    + $"{config.MinScoreFor(container.EmbeddingModel).ToString(System.Globalization.CultureInfo.InvariantCulture)} (ADR 0004 : lancer le banc d'essai)");
        }
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
                        Console.Error.WriteLine("Index non vérifié : le service IA n'a pas pu être interrogé, impossible de réindexer.");
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
                return RunBenchmark(args, config);
            case "serve":
            {
                using var api = new HttpApi(container, args.Value("--host") ?? "127.0.0.1", args.Int("--port") ?? 8000, quiet: args.Has("--quiet"));
                using var stop = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
                return Serve(api, config.AiBaseUrl, stop.WaitHandle);
            }
            default:   // experience <nom> (Find ne connaît pas d'autre commande)
            {
                var common = new Overrides(args.NonEmpty("--embedding-model"), args.NonEmpty("--generation-model"),
                                           PromptName: args.NonEmpty("--prompt"));
                // --config tel qu'il a été donné ; sans lui, depuis la racine (« config/app.json »).
                // Experiment y met des « / » : rapport identique sous Windows et Linux.
                var configPath = args.NonEmpty("--config") ?? Path.GetRelativePath(AppConfig.ProjectRoot, config.Source);
                return Experiments.Run(args.Positional[1], args, config, common, configPath, log);
            }
        }
    }

    /// <summary>Démarre l'API, annonce l'adresse où se connecter, attend l'arrêt.</summary>
    internal static int Serve(HttpApi api, string aiBaseUrl, WaitHandle stop)
    {
        api.Start();
        var listening = api.AllInterfaces ? ", à l'écoute sur toutes les interfaces" : "";
        Console.WriteLine($"Application sur {api.Url}{listening} (service IA : {aiBaseUrl}) — Ctrl+C pour arrêter");
        stop.WaitOne();
        api.Stop();
        Console.WriteLine("\nArrêt de l'application.");
        return 0;
    }

    /// <summary>Les arguments qu'une commande attend après son nom, et le message s'il en manque.</summary>
    private sealed record Expected(int Count, string Missing);

    /// <summary>
    /// Ce que la ligne de commande accepte pour une commande : son nom (« snapshot list »), ses options,
    /// les arguments attendus après ce nom (aucun par défaut), et si elle cherche des passages (seuil de pertinence).
    /// </summary>
    private sealed record Command(string Name, string[] Options, Expected? Arguments = null, bool Searches = false,
                                  string? Extra = null)
    {
        /// <summary>Ni argument manquant, ni argument en trop (« status foo » n'est pas « status »).</summary>
        public void EnsureArguments(IReadOnlyList<string> positional)
        {
            var expected = Name.Split(' ').Length + (Arguments?.Count ?? 0);
            // Find a reconnu les mots du nom : seul un argument attendu peut manquer.
            if (Arguments is not null && positional.Count < expected)
            {
                throw new ArgumentException(Arguments.Missing);
            }
            if (positional.Count > expected)
            {
                throw new ArgumentException(Extra ?? $"argument(s) en trop pour {Name} : {string.Join(", ", positional.Skip(expected))}");
            }
        }
    }

    private static readonly string[] Common = { "--config", "--embedding-model", "--generation-model", "--prompt", "-v", "--verbose" };
    private static readonly string[] ExperimentCommon = Common.Concat(new[] { "--questions", "--limit", "--out" }).ToArray();
    private const string OneQuestion = "ask attend une seule question, entre guillemets";

    /// <summary>Chaque commande et ses options.</summary>
    private static readonly Dictionary<string, Command> Commands = new Command[]
    {
        new("index", Common.Append("--if-stale").ToArray()),
        new("ask", Common.Concat(new[] { "--user", "--json" }).ToArray(), new(1, OneQuestion), Searches: true, Extra: OneQuestion),
        new("status", Common.Append("--json").ToArray()),
        new("snapshot record", Common.Concat(new[] { "--questions", "--limit" }).ToArray(), new(1, "snapshot record attend un nom"),
            Searches: true),
        new("snapshot compare", Common, new(2, "snapshot compare attend deux noms")),
        new("snapshot list", Common),
        new("serve", Common.Concat(new[] { "--host", "--port", "--quiet" }).ToArray(), Searches: true),
        // Le banc construit ses propres compositions, sans journal : -v n'y changerait rien.
        new("benchmark", new[] { "--config", "--embedding-model", "--generation-model", "--prompt", "--embedding", "--generation",
                                 "--questions", "--validate-with", "--runs", "--limit", "--min-score", "--max-chars",
                                 "--overlap-chars", "--seed", "--out" }),
        // Les options propres à chaque expérience.
        new("experience cace-decoupage", ExperimentCommon.Concat(new[] { "--max-chars", "--overlap-chars" }).ToArray()),
        new("experience changement-embeddings", ExperimentCommon.Append("--other").ToArray()),
        new("experience changement-generateur", ExperimentCommon.Append("--other").ToArray()),
        new("experience prompt-v2", ExperimentCommon.Append("--other").ToArray()),
        new("experience stabilite", ExperimentCommon.Concat(new[] { "--runs", "--seed" }).ToArray()),
    }.ToDictionary(c => c.Name);

    /// <summary>La commande que nomment les premiers arguments ; null si le premier mot est inconnu.</summary>
    private static Command? Find(IReadOnlyList<string> positional)
    {
        switch (positional[0])
        {
            case "snapshot":
                return positional.Count > 1 && Commands.TryGetValue($"snapshot {positional[1]}", out var snapshot)
                    ? snapshot
                    : throw new ArgumentException("snapshot attend record, compare ou list");
            case "experience":
                if (positional.Count < 2)
                {
                    throw new ArgumentException($"experience attend un nom : {string.Join(", ", Experiments.Names)}");
                }
                return Commands.TryGetValue($"experience {positional[1]}", out var experience)
                    ? experience
                    : throw new ArgumentException($"expérience inconnue : {positional[1]} (connues : {string.Join(", ", Experiments.Names)})");
            default:   // une commande d'un seul mot (« snapshot list » en un seul argument n'en est pas une)
                return Commands.TryGetValue(positional[0], out var single) && !single.Name.Contains(' ') ? single : null;
        }
    }

    private static int RunBenchmark(Args args, AppConfig config)
    {
        // Sans --embedding / --generation : les modèles de la configuration.
        var embeddings = args.Values("--embedding") is { Count: > 0 } e ? e : new[] { args.NonEmpty("--embedding-model") ?? config.EmbeddingModel };
        var generations = args.Values("--generation") is { Count: > 0 } g ? g : new[] { args.NonEmpty("--generation-model") ?? config.GenerationModel };
        // Les options sont validées avant tout travail : pas d'indexation pour découvrir une faute de frappe. Benchmark.Run
        // vérifie le reste (--min-score compris), puis le service IA.
        var runs = args.Int("--runs") ?? 3;
        var limit = args.Int("--limit");
        var maxChars = args.Int("--max-chars");
        var overlap = args.Int("--overlap-chars");
        var seed = args.Int("--seed");
        var questions = EvalQuestions.Limit(EvalQuestions.Load(EvalQuestions.Resolve(args.Value("--questions") ?? "eval/questions.json")), limit);
        var validation = args.Value("--validate-with") is { } path ? EvalQuestions.Load(EvalQuestions.Resolve(path)) : null;
        var options = new BenchmarkOptions(
            embeddings, generations, questions, validation,
            Runs: runs,
            OutDir: args.NonEmpty("--out") ?? AppConfig.ResultsDir("", DateTime.Now),
            MinScoreMode: args.Value("--min-score") ?? "config",
            SplitterMaxChars: maxChars,
            SplitterOverlapChars: overlap,
            Seed: seed,
            PromptName: args.NonEmpty("--prompt"),
            NewOutDir: args.NonEmpty("--out") is null);   // sans --out : jamais un dossier qui existe déjà
        Benchmark.Run(config, options, Console.WriteLine);
        return 0;
    }

    private static int Snapshot(Args args, AppConfig config, Container container)
    {
        switch (args.Positional[1])
        {
            case "record":
            {
                // Les N premières questions, puis leurs utilisateurs : une question écartée par --limit
                // n'a pas à nommer un utilisateur connu.
                var questions = EvalQuestions.Limit(EvalQuestions.Load(EvalQuestions.Resolve(args.Value("--questions") ?? "eval/questions.json")),
                                                    args.Int("--limit"));
                var snapshot = container.RecordSnapshot.Execute(args.Positional[2], questions.Select(q => q.ToSnapshotQuestion(config)).ToList());
                Console.WriteLine($"Instantané « {snapshot.Name} » : {snapshot.Entries.Count} réponses, enregistré dans {Display(config.SnapshotsDir)}");
                foreach (var (key, value) in snapshot.Configuration.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    Console.WriteLine($"  {key} = {SnapshotComparer.Canonical(value)}");
                }
                return 0;
            }
            case "compare":
            {
                var store = container.Snapshots;
                Console.WriteLine(Presenter.ComparisonToText(SnapshotComparer.Compare(store.Load(args.Positional[2]), store.Load(args.Positional[3]))));
                return 0;
            }
            default:   // list
            {
                var names = container.Snapshots.Names();
                Console.WriteLine(names.Count > 0 ? string.Join("\n", names) : $"Aucun instantané dans {Display(config.SnapshotsDir)}");
                return 0;
            }
        }
    }

    /// <summary>Un chemin affiché avec des « / » : la même sortie sous Windows et Linux.</summary>
    private static string Display(string path) => path.Replace('\\', '/');
}

/// <summary>Le service IA doit répondre avant un banc ou une expérience : on le vérifie d'abord, en une requête.</summary>
public static class AiService
{
    /// <summary>Les deux types de modèles de GET /v1/models, nommés comme dans les messages.</summary>
    private static readonly Dictionary<string, string> Kinds = new()
    {
        ["embedding"] = "modèle d'embeddings", ["generation"] = "modèle de génération",
    };

    /// <summary>
    /// Deux messages : injoignable, ou joignable mais en erreur (l'adresse interrogée).
    /// GetAsync lit tout le corps, sans l'interpréter : coupé ou trop lent, c'est « injoignable ».
    /// </summary>
    public static void Require(string baseUrl)
    {
        var url = baseUrl.TrimEnd('/') + "/health";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = http.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                throw new AiServiceException($"le service IA répond HTTP {(int)response.StatusCode} sur {url}", transient: false);
            }
        }
        // Refus, délai, adresse invalide (UriFormatException, InvalidOperationException) ou autre protocole que HTTP
        // (NotSupportedException, une trace auparavant) : le même message.
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException
                                               or UriFormatException or NotSupportedException)
        {
            throw new AiServiceException($"service IA injoignable ({baseUrl}). Lancez-le d'abord : python -m ai_service", transient: false);
        }
    }

    /// <summary>
    /// L'alias --other d'une expérience est-il servi, et du bon type (GET /v1/models) ? Vérifié avant tout index : sinon
    /// l'erreur ne se verrait qu'après l'index et l'instantané « avant ».
    /// </summary>
    public static void RequireModel(string baseUrl, string kind, string alias)
    {
        var url = baseUrl.TrimEnd('/') + "/v1/models";
        var served = ServedAliases(url, kind)
                     ?? throw new AiServiceException($"le service IA ne donne pas la liste de ses modèles ({url})", transient: false);
        if (!served.Contains(alias))
        {
            throw new ArgumentException($"le service IA ne sert pas « {alias} » comme {Kinds[kind]} "
                                        + $"(servis : {(served.Count > 0 ? string.Join(", ", served) : "aucun")})");
        }
    }

    /// <summary>Les alias d'un type, dans l'ordre du service ; null s'il est injoignable ou hors contrat.</summary>
    private static List<string>? ServedAliases(string url, string kind)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            // UTF-8 strict, marque d'ordre des octets acceptée, comme toute réponse du service IA
            // (GetStringAsync remplaçait un octet qui n'est pas de l'UTF-8). Statut d'erreur : HttpRequestException.
            var body = TextFiles.DecodeUtf8(http.GetByteArrayAsync(url).GetAwaiter().GetResult());
            // JSON strict : une clé en double, même là où rien n'est lu, NaN, un entier de plus de 4300 chiffres, plus
            // de 64 niveaux ou une chaîne qui n'est pas du texte sont hors contrat.
            if (JsonText.Parse(body, strings: true, maxDigits: JsonText.MaxDigits) is not JsonObject models
                || models[kind] is not JsonArray list)
            {
                return null;
            }
            var aliases = new List<string>();
            foreach (var item in list)
            {
                if (item is not JsonObject model || model["alias"] is not JsonValue alias || !alias.TryGetValue<string>(out var text))
                {
                    return null;   // un alias qui n'est pas un texte : hors contrat
                }
                aliases.Add(text);
            }
            return aliases;
        }
        // FormatException : octets qui ne sont pas de l'UTF-8 (TextFiles), ou JSON qui n'est pas strict (JsonText).
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException
                                               or System.Text.Json.JsonException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>Analyse minimale des arguments : positionnels, drapeaux, options à une ou plusieurs valeurs.</summary>
public sealed class Args
{
    private static readonly HashSet<string> Flags = new() { "-v", "--verbose", "--json", "--help", "-h", "--if-stale", "--quiet" };
    private static readonly HashSet<string> MultiValued = new() { "--embedding", "--generation" };
    // Les options entières de toutes les commandes : voir EnsureIntegers.
    private static readonly HashSet<string> Integers = new() { "--port", "--limit", "--runs", "--max-chars", "--overlap-chars", "--seed" };
    // « -1 » ou « -0.5 » est une valeur (un nombre négatif), pas une option.
    private static readonly Regex NegativeNumber = new(@"^-([0-9]+|[0-9]*\.[0-9]+)\z", RegexOptions.Compiled);
    private readonly string[] _argv;
    private readonly Dictionary<string, List<string>> _values = new();
    private readonly List<(string Option, string Value)> _integers = new();   // chaque valeur entière, dans l'ordre de la ligne
    private readonly HashSet<string> _flags = new();
    private readonly List<string> _seen = new();
    private readonly List<string> _withoutValue = new();
    private readonly List<string> _flagsWithValue = new();

    public List<string> Positional { get; } = new();

    public Args(string[] argv)
    {
        _argv = argv;
        var options = true;
        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (options && arg == "--")
            {
                // « -- » termine les options (convention POSIX) : tout ce qui suit est positionnel, même « -x »
                // ou « -h » (une question qui commence par un tiret : ask -- "-x").
                options = false;
                continue;
            }
            if (!options || !IsOption(arg))
            {
                Positional.Add(arg);
                continue;
            }
            var equals = arg.IndexOf('=');
            var option = equals > 0 ? arg[..equals] : arg;
            _seen.Add(option);
            if (Flags.Contains(option))
            {
                if (equals > 0)
                {
                    _flagsWithValue.Add(option);
                }
                else
                {
                    _flags.Add(option);
                }
            }
            else if (equals > 0)
            {
                Add(option, arg[(equals + 1)..]);
            }
            else if (i + 1 < argv.Length && !IsOption(argv[i + 1]))
            {
                Add(option, argv[++i]);
                // Option à plusieurs valeurs (--embedding a b) : on avale jusqu'à la prochaine option.
                while (MultiValued.Contains(option) && i + 1 < argv.Length && !IsOption(argv[i + 1]))
                {
                    Add(option, argv[++i]);
                }
            }
            else
            {
                _withoutValue.Add(option);   // erreur si l'option est connue (EnsureOnly), après les options inconnues
            }
        }
    }

    /// <summary>
    /// Un mot qui commence par un tiret est une option (« -json » compris), sauf « - » seul, un nombre négatif (« -1 ») et
    /// un texte dont le nom contient une espace : une question (« -vingt degrés ? », « -5 jours ? »).
    /// </summary>
    private static bool IsOption(string arg) =>
        arg.Length > 1 && arg[0] == '-' && !NegativeNumber.IsMatch(arg) && !Name(arg).Contains(' ');

    /// <summary>Le nom d'une option, sans sa valeur (« --user=bruno » : « --user »).</summary>
    private static string Name(string arg) => arg.IndexOf('=') is > 0 and var equals ? arg[..equals] : arg;

    /// <summary>
    /// La commande vient d'abord, puis ses options : une option placée avant ou entre les mots de la commande
    /// (« --json status », « snapshot -v list », « -- status » compris) est refusée. La ligne n'a ainsi qu'une
    /// lecture : placée avant, une option à valeur prendrait le nom de la commande pour valeur (« --config status »),
    /// et les options se vérifient d'après la commande (<see cref="EnsureOnly"/>).
    /// </summary>
    public void EnsureCommandFirst()
    {
        var words = _argv is ["snapshot" or "experience", ..] ? 2 : 1;
        if (_argv.Take(words).FirstOrDefault(IsOption) is { } misplaced)
        {
            throw new ArgumentException($"option placée avant la commande : {Name(misplaced)} "
                                        + "(la commande vient d'abord : assistant <commande> [options])");
        }
    }

    /// <summary>
    /// Refuse toute option que cette commande ne lit pas (faute de frappe, abréviation, tiret simple, option
    /// d'une autre commande ou d'une autre sous-commande), puis une option privée de sa valeur, ou un drapeau
    /// qui en reçoit une (« --json=1 »).
    /// </summary>
    public void EnsureOnly(string command, IReadOnlyCollection<string> allowed)
    {
        var unknown = _seen.Where(o => !allowed.Contains(o) && o is not ("--help" or "-h")).Distinct().ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"option(s) inconnue(s) pour {command} : {string.Join(", ", unknown)} "
                                        + $"(acceptées : {string.Join(", ", allowed.Order(StringComparer.Ordinal))})");
        }
        if (_withoutValue.Count > 0)
        {
            throw new ArgumentException($"l'option {_withoutValue[0]} attend une valeur");
        }
        if (_flagsWithValue.Count > 0)
        {
            throw new ArgumentException($"l'option {_flagsWithValue[0]} ne prend pas de valeur");
        }
    }

    private void Add(string option, string value)
    {
        if (!_values.TryGetValue(option, out var list))
        {
            _values[option] = list = new List<string>();
        }
        list.Add(value);
        if (Integers.Contains(option))
        {
            _integers.Add((option, value));
        }
    }

    public bool Has(string flag) => _flags.Contains(flag);

    public string? Value(string option) => _values.TryGetValue(option, out var list) ? list[^1] : null;

    /// <summary>
    /// Une option qui remplace un défaut, la configuration (--config, --embedding-model, --generation-model, --prompt) ou le
    /// dossier de sortie daté (--out) : null si elle est absente ou donnée vide. Une chaîne vide vaut ce défaut.
    /// </summary>
    public string? NonEmpty(string option) => Value(option) is { Length: > 0 } value ? value : null;

    public IReadOnlyList<string> Values(string option) => _values.GetValueOrDefault(option) ?? new List<string>();

    /// <summary>
    /// Chaque valeur des options entières, dans l'ordre de la ligne de commande : avec « --seed abc --runs deux », c'est
    /// --seed qui est signalé, et « --runs abc --runs 3 » est refusé (la dernière valeur, celle que lit <see cref="Int"/>,
    /// ne couvre pas la première).
    /// </summary>
    public void EnsureIntegers()
    {
        foreach (var (option, value) in _integers)
        {
            ParseInt(option, value);
        }
    }

    /// <summary>Valeur entière d'une option, avec un message en français si elle n'en est pas une.</summary>
    public int? Int(string option) => Value(option) is { } value ? ParseInt(option, value) : null;

    private static int ParseInt(string option, string value) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"l'option {option} attend un entier, pas « {value} »");
}
