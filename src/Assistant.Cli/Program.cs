// Ligne de commande de l'application (adaptateur entrant).
//
//     dotnet run --project src/Assistant.Cli -- index
//     dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail ?" --user alice -v
//     dotnet run --project src/Assistant.Cli -- status
//     dotnet run --project src/Assistant.Cli -- snapshot record reference --questions eval/questions.json
//     dotnet run --project src/Assistant.Cli -- snapshot compare reference candidat
//
// Le service IA (python -m ai_service) doit tourner : c'est un autre programme, dans un autre langage.

using System.Text;
using System.Text.Json.Nodes;
using Assistant.Application;
using Assistant.Domain;
using Assistant.Infrastructure;

namespace Assistant.Cli;

public static class Program
{
    private const string Usage = """
        Usage : assistant <commande> [options]

        Commandes :
          index                          indexer le corpus
          ask "<question>"               poser une question   (--user alice, --json, -v)
          status                         l'index est-il cohérent avec le corpus, le découpage et le modèle servi ?   (--json)
          snapshot record <nom>          enregistrer un instantané   (--questions eval/questions.json, --limit N)
          snapshot compare <a> <b>       comparer deux instantanés
          snapshot list                  lister les instantanés

        Options communes : --config <fichier>   --embedding-model <alias>   --generation-model <alias>   --prompt <nom>
        Codes de retour : 0 ok · 1 erreur · status : 2 à refaire, 3 non vérifié
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
                                           or CorpusFormatException or ArgumentException or InvalidOperationException
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
            default:
                Console.Error.WriteLine($"Commande inconnue : {args.Positional[0]}");
                Console.WriteLine(Usage);
                return 1;
        }
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
                var questions = LoadQuestions(ProjectPath(args.Value("--questions") ?? "eval/questions.json"), config);
                if (args.Value("--limit") is { } limit && int.Parse(limit, System.Globalization.CultureInfo.InvariantCulture) is var n && n > 0)
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

    /// <summary>Jeu de questions au format de eval/questions.json (partagé avec la version Python).</summary>
    private static List<SnapshotQuestion> LoadQuestions(string path, AppConfig config)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        return root["questions"]!.AsArray().Select(q => new SnapshotQuestion(
            q!["id"]!.GetValue<string>(),
            config.User(q["user"]?.GetValue<string>() ?? "alice"),
            q["question"]!.GetValue<string>())).ToList();
    }

    /// <summary>Un chemin relatif qui n'existe pas depuis le dossier courant est cherché depuis la racine du projet.</summary>
    private static string ProjectPath(string path) =>
        !Path.IsPathRooted(path) && !File.Exists(path) && File.Exists(Path.Combine(AppConfig.ProjectRoot, path))
            ? Path.Combine(AppConfig.ProjectRoot, path)
            : path;
}

/// <summary>Analyse minimale des arguments : positionnels, drapeaux, options à valeur.</summary>
public sealed class Args
{
    private static readonly HashSet<string> Flags = new() { "-v", "--verbose", "--json", "--help", "-h" };
    private readonly Dictionary<string, string> _values = new();
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
                    _values[arg[..equals]] = arg[(equals + 1)..];
                }
                else if (i + 1 < argv.Length)
                {
                    _values[arg] = argv[++i];
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

    public bool Has(string flag) => _flags.Contains(flag);

    public string? Value(string option) => _values.GetValueOrDefault(option);
}
