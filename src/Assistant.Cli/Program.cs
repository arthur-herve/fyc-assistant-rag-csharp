// La ligne de commande de l'application. C'est un adaptateur entrant.
//
//     dotnet run --project src/Assistant.Cli -- index
//     dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail ?" --user alice -v
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
            // Configuration, corpus ou index mal formés : le message nomme déjà le fichier.
            Console.Error.WriteLine($"Erreur : {error.Message}");
            return 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"Erreur : fichier illisible (configuration, index) — {error.Message}");
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
            default:   // ask (Find ne connaît pas d'autre commande)
            {
                var user = config.User(args.Value("--user") ?? "alice");
                var answer = container.AskQuestion.Execute(user, args.Positional[1]);
                Console.WriteLine(args.Has("--json")
                    ? Presenter.ToJson(Presenter.AnswerToJson(answer, includeRaw: verbose))
                    : Presenter.AnswerToText(answer, verbose));
                return 0;
            }
        }
    }

    /// <summary>Les arguments qu'une commande attend après son nom, et le message s'il en manque.</summary>
    private sealed record Expected(int Count, string Missing);

    /// <summary>
    /// Ce que la ligne de commande accepte pour une commande : son nom, ses options et les arguments attendus après ce
    /// nom (aucun par défaut).
    /// </summary>
    private sealed record Command(string Name, string[] Options, Expected? Arguments = null, string? Extra = null)
    {
        /// <summary>Ni argument manquant, ni argument en trop (« index foo » n'est pas « index »).</summary>
        public void EnsureArguments(IReadOnlyList<string> positional)
        {
            var expected = 1 + (Arguments?.Count ?? 0);
            // Find a reconnu le nom : seul un argument attendu peut manquer.
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
    private const string OneQuestion = "ask attend une seule question, entre guillemets";

    /// <summary>Chaque commande et ses options.</summary>
    private static readonly Dictionary<string, Command> Commands = new Command[]
    {
        new("index", Common),
        new("ask", Common.Concat(new[] { "--user", "--json", "-v", "--verbose" }).ToArray(), new(1, OneQuestion), Extra: OneQuestion),
    }.ToDictionary(c => c.Name);

    /// <summary>La commande que nomme le premier argument, ou null s'il est inconnu.</summary>
    private static Command? Find(IReadOnlyList<string> positional) =>
        Commands.TryGetValue(positional[0], out var command) ? command : null;
}

/// <summary>Analyse minimale des arguments : positionnels, drapeaux, options à une valeur.</summary>
public sealed class Args
{
    private static readonly HashSet<string> Flags = new() { "-v", "--verbose", "--json", "--help", "-h" };
    // « -1 » ou « -0.5 » est une valeur (un nombre négatif), pas une option.
    private static readonly Regex NegativeNumber = new(@"^-([0-9]+|[0-9]*\.[0-9]+)\z", RegexOptions.Compiled);
    private readonly string[] _argv;
    private readonly Dictionary<string, List<string>> _values = new();
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
    /// La commande vient d'abord, puis ses options. Une option placée avant la commande est refusée, « -- » compris :
    /// « --json index », « -- index ». Ainsi la ligne n'a qu'une lecture. Placée avant, une option à valeur prendrait
    /// le nom de la commande pour valeur (« --config index »).
    /// Il faut aussi connaître la commande pour vérifier ses options, dans <see cref="EnsureOnly"/>.
    /// </summary>
    public void EnsureCommandFirst()
    {
        if (_argv.Take(1).FirstOrDefault(IsOption) is { } misplaced)
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
    }

    public bool Has(string flag) => _flags.Contains(flag);

    public string? Value(string option) => _values.TryGetValue(option, out var list) ? list[^1] : null;

    /// <summary>
    /// Lit une option qui remplace un défaut, comme la configuration (--config, --embedding-model, --generation-model).
    /// Absente ou donnée vide, elle vaut null, et le défaut s'applique.
    /// </summary>
    public string? NonEmpty(string option) => Value(option) is { Length: > 0 } value ? value : null;
}
