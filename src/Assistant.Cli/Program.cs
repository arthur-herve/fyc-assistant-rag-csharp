// La ligne de commande de l'application. C'est un adaptateur entrant.
//
//     dotnet run --project src/Assistant.Cli -- index
//     dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail ?" --user alice -v
//     dotnet run --project src/Assistant.Cli -- snapshot record reference --questions eval/questions.json
//     dotnet run --project src/Assistant.Cli -- snapshot compare reference candidat
//
// Le service IA (python -m ai_service) doit tourner : c'est un autre programme, dans un autre langage.

using System.Text;
using System.Text.RegularExpressions;
using Assistant.Application;
using Assistant.Domain;

namespace Assistant.Cli;

public static class Program
{
    private const string Usage = """
        Usage : assistant <commande> [options]

        Commandes :
          index                          indexer le corpus
          ask "<question>"               poser une question   (--user <nom>, défaut : alice ; --json ; -v : trace complète)
          snapshot record <nom>          enregistrer un instantané   (--questions eval/questions.json, --limit N, au moins 1)
          snapshot compare <a> <b>       comparer deux instantanés
          snapshot list                  lister les instantanés
          experience <nom>               une expérience reproductible   (--questions, --limit, --out, et selon l'expérience :)
                                           stabilite (--runs)

        Options communes : --config <fichier>   --embedding-model <alias>   --generation-model <alias>
        La commande vient d'abord, puis ses options. Une option inconnue, abrégée ou d'une autre commande, ou un argument en trop, est une erreur.
        « -- » termine les options : ce qui suit est un argument, même un mot qui commence par un tiret (ask -- -télétravail) ; sans « -- », un tel mot n'est un argument que s'il est « - », un nombre négatif, ou si son nom (ce qui précède un éventuel « = ») contient une espace (ask "-vingt degrés ?").
        Codes de retour : 0 ok · 1 erreur, saisie comprise
        """;

    public static int Main(string[] args)
    {
        // Sortie en UTF-8, même redirigée vers un fichier : sans cela, Windows l'encoderait en cp1252.
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            return Run(args);
        }
        catch (Exception error) when (error is AssistantApplicationException or DomainException or UnknownUserException
                                           or ConfigException or ArgumentException or FormatException)
        {
            // Configuration, corpus, index ou questions mal formés : le message nomme déjà le fichier.
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
        // Viennent ensuite les entiers, vérifiés dans l'ordre de la ligne. Ils passent avant un argument manquant ou en
        // trop, et avant toute autre vérification de valeur.
        args.EnsureIntegers();
        command.EnsureArguments(args.Positional);
        var verbose = args.Has("-v") || args.Has("--verbose");

        var config = AppConfig.Load(args.NonEmpty("--config"));
        var container = Composition.Build(config, args.NonEmpty("--embedding-model"), args.NonEmpty("--generation-model"));
        switch (args.Positional[0])
        {
            case "index":
            {
                var summary = container.IndexCorpus.Execute();
                Console.WriteLine(Presenter.SummaryToText(summary));
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
            case "snapshot":
                return Snapshot(args, config, container);
            default:   // experience <nom> (Find ne connaît pas d'autre commande)
            {
                var common = new Overrides(args.NonEmpty("--embedding-model"), args.NonEmpty("--generation-model"));
                // Le rapport cite --config tel qu'il a été donné, sinon le chemin depuis la racine,
                // « config/app.json ». Experiment y met des « / » : le rapport est identique sous Windows et Linux.
                var configPath = args.NonEmpty("--config") ?? Path.GetRelativePath(AppConfig.ProjectRoot, config.Source);
                return Experiments.Run(args.Positional[1], args, config, common, configPath);
            }
        }
    }

    /// <summary>Les arguments qu'une commande attend après son nom, et le message s'il en manque.</summary>
    private sealed record Expected(int Count, string Missing);

    /// <summary>
    /// Ce que la ligne de commande accepte pour une commande : son nom, parfois en deux mots (« snapshot list »), ses
    /// options et les arguments attendus après ce nom (aucun par défaut).
    /// </summary>
    private sealed record Command(string Name, string[] Options, Expected? Arguments = null, string? Extra = null)
    {
        /// <summary>Ni argument manquant, ni argument en trop (« index foo » n'est pas « index »).</summary>
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

    private static readonly string[] Common = { "--config", "--embedding-model", "--generation-model" };
    private static readonly string[] ExperimentCommon = Common.Concat(new[] { "--questions", "--limit", "--out" }).ToArray();
    private const string OneQuestion = "ask attend une seule question, entre guillemets";

    /// <summary>Chaque commande et ses options.</summary>
    private static readonly Dictionary<string, Command> Commands = new Command[]
    {
        new("index", Common),
        new("ask", Common.Concat(new[] { "--user", "--json", "-v", "--verbose" }).ToArray(), new(1, OneQuestion), Extra: OneQuestion),
        new("snapshot record", Common.Concat(new[] { "--questions", "--limit" }).ToArray(), new(1, "snapshot record attend un nom")),
        new("snapshot compare", Common, new(2, "snapshot compare attend deux noms")),
        new("snapshot list", Common),
        // Les options propres à chaque expérience.
        new("experience stabilite", ExperimentCommon.Concat(new[] { "--runs" }).ToArray()),
    }.ToDictionary(c => c.Name);

    /// <summary>La commande que nomment les premiers arguments, ou null si le premier mot est inconnu.</summary>
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
            default:   // une commande d'un seul mot : « snapshot list » passé en un seul argument n'en est pas une
                return Commands.TryGetValue(positional[0], out var single) && !single.Name.Contains(' ') ? single : null;
        }
    }

    private static int Snapshot(Args args, AppConfig config, Container container)
    {
        switch (args.Positional[1])
        {
            case "record":
            {
                // On prend les N premières questions avant de chercher leurs utilisateurs : une question écartée par
                // --limit n'a pas à nommer un utilisateur connu.
                var questions = EvalQuestions.Limit(EvalQuestions.Load(EvalQuestions.Resolve(args.NonEmpty("--questions") ?? "eval/questions.json")),
                                                    args.Int("--limit"));
                var snapshot = container.RecordSnapshot.Execute(args.Positional[2], questions.Select(q => q.ToSnapshotQuestion(config)).ToList());
                Console.WriteLine($"Instantané « {snapshot.Name} » : {snapshot.Entries.Count} réponses, enregistré dans {Display(config.SnapshotsDir)}");
                foreach (var (key, value) in snapshot.Configuration.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    Console.WriteLine($"  {key} = {ConfigurationValues.Canonical(value)}");
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

    /// <summary>Un chemin affiché avec des « / », pour que la sortie soit la même sous Windows et Linux.</summary>
    private static string Display(string path) => path.Replace('\\', '/');
}

/// <summary>Analyse minimale des arguments : positionnels, drapeaux, options à une valeur.</summary>
public sealed class Args
{
    private static readonly HashSet<string> Flags = new() { "-v", "--verbose", "--json", "--help", "-h" };
    // Les options entières de toutes les commandes : voir EnsureIntegers.
    private static readonly HashSet<string> Integers = new() { "--limit", "--runs" };
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
                // « -- » termine les options, selon la convention POSIX : tout ce qui suit est positionnel, même « -x »
                // ou « -h ». C'est ainsi qu'on pose une question qui commence par un tiret : ask -- "-x".
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
            }
            else
            {
                _withoutValue.Add(option);   // EnsureOnly la refuse si elle est connue, après les options inconnues
            }
        }
    }

    /// <summary>
    /// Un mot qui commence par un tiret est une option, « -json » compris, sauf dans trois cas : « - » seul, un nombre
    /// négatif (« -1 ») et un texte dont le nom contient une espace, qui est une question (« -vingt degrés ? »,
    /// « -5 jours ? »).
    /// </summary>
    private static bool IsOption(string arg) =>
        arg.Length > 1 && arg[0] == '-' && !NegativeNumber.IsMatch(arg) && !Name(arg).Contains(' ');

    /// <summary>Le nom d'une option, sans sa valeur (« --user=bruno » : « --user »).</summary>
    private static string Name(string arg) => arg.IndexOf('=') is > 0 and var equals ? arg[..equals] : arg;

    /// <summary>
    /// La commande vient d'abord, puis ses options. Une option placée avant ou entre les mots de la commande est
    /// refusée, « -- » compris : « --json index », « snapshot -v list », « -- index ». Ainsi la ligne n'a qu'une
    /// lecture. Placée avant, une option à valeur prendrait le nom de la commande pour valeur (« --config index »).
    /// Il faut aussi connaître la commande pour vérifier ses options, dans <see cref="EnsureOnly"/>.
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
    /// Refuse d'abord toute option que cette commande ne lit pas : une faute de frappe, une abréviation, ou l'option
    /// d'une autre commande. Refuse ensuite une option privée de sa valeur, et enfin un drapeau qui en reçoit une
    /// (« --json=1 »).
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
    /// Lit une option qui remplace un défaut, comme la configuration (--config, --embedding-model, --generation-model)
    /// ou le dossier de sortie daté (--out). Absente ou donnée vide, elle vaut null, et le défaut s'applique.
    /// </summary>
    public string? NonEmpty(string option) => Value(option) is { Length: > 0 } value ? value : null;

    /// <summary>
    /// Vérifie chaque valeur des options entières, dans l'ordre de la ligne : avec « --limit abc --runs deux », c'est
    /// --limit qui est signalé. « --runs abc --runs 3 » est refusé aussi, même si <see cref="Int"/> ne lit que la
    /// dernière valeur.
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
