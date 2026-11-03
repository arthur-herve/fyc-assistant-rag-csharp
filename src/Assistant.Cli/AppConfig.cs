// La configuration de l'application, lue depuis config/app.json avec System.Text.Json et rien d'autre.
// Elle ne connaît les modèles que par leurs alias. Seuls les seuils de pertinence dépendent du modèle,
// et c'est voulu. Une erreur de saisie se voit dès le chargement, avec le fichier et la clé.
// JSON n'a pas de commentaires : une clé qui commence par « _ » en tient lieu, à tous les niveaux du
// fichier, y compris dans retrieval.min_score, users et chaque users.<nom>.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Domain;

namespace Assistant.Cli;

/// <summary>Utilisateur absent de la configuration. Le message rappelle la liste des utilisateurs connus.</summary>
public sealed class UnknownUserException : Exception
{
    public UnknownUserException(string name, IEnumerable<string> known)
        : base($"utilisateur inconnu : {name} (connus : {string.Join(", ", known)})") { }
}

/// <summary>Configuration absente, incomplète ou hors bornes : le message nomme le fichier et la clé.</summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string message) : base(message) { }
}

public sealed record AppConfig(
    string AiBaseUrl,
    string EmbeddingModel,
    string GenerationModel,
    TimeSpan Timeout,
    string CorpusDir,
    string IndexPath,
    string SnapshotsDir,
    int SplitterMaxChars,
    int SplitterOverlapChars,
    bool SplitterIncludeTitle,
    int TopK,
    IReadOnlyDictionary<string, double> MinScores,
    string PromptName,
    double Temperature,
    int MaxTokens,
    int MaxAttempts,
    int? Seed,
    IReadOnlyDictionary<string, IReadOnlySet<string>> Users)
{
    /// <summary>
    /// Les clés attendues, section par section, la clé "" désignant le premier niveau. Une faute comme « topk » ou
    /// « max_char » ne doit pas laisser la valeur par défaut s'appliquer sans rien dire. Les clés de commentaire, qui
    /// commencent par « _ », ne sont pas concernées.
    /// </summary>
    private static readonly Dictionary<string, string[]> KnownKeys = new(StringComparer.Ordinal)
    {
        [""] = new[] { "ai_service", "corpus", "index", "snapshots", "splitter", "retrieval", "generation", "users" },
        ["ai_service"] = new[] { "base_url", "embedding_model", "generation_model", "timeout_seconds" },
        ["corpus"] = new[] { "directory" },
        ["index"] = new[] { "path" },
        ["snapshots"] = new[] { "directory" },
        ["splitter"] = new[] { "max_chars", "overlap_chars", "include_title" },
        ["retrieval"] = new[] { "top_k", "min_score" },
        ["generation"] = new[] { "prompt", "temperature", "max_tokens", "max_attempts", "seed" },
    };

    /// <summary>
    /// Le délai maximal d'un appel au service IA : un jour, bien au-delà d'une génération lente. Sans cette borne,
    /// une valeur absurde ferait échouer HttpClient, qui refuse plus de 24 jours, ou TimeSpan, et le message ne
    /// nommerait ni le fichier ni la clé.
    /// </summary>
    private const double MaxTimeoutSeconds = 86_400;

    /// <summary>
    /// Les noms de types qu'emploient les messages (« doit être un entier, pas … »). La valeur fautive y est écrite
    /// en JSON par System.Text.Json : une espace insécable y apparaît échappée, et un nombre garde son écriture du
    /// fichier.
    /// </summary>
    private static readonly Dictionary<Type, string> KindNames = new()
    {
        [typeof(bool)] = "un booléen",
        [typeof(int)] = "un entier",
        [typeof(double)] = "un nombre",
        [typeof(string)] = "une chaîne",
    };

    /// <summary>Pour <see cref="Show"/> : une valeur citée tient sur une ligne et garde ses accents.</summary>
    private static readonly JsonSerializerOptions OneLine = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Racine du projet : le dossier qui contient config/, corpus/, prompts/.</summary>
    public static string ProjectRoot { get; } = FindProjectRoot();

    /// <summary>
    /// Le dossier de sortie par défaut, daté à la seconde, par exemple « 20261001-164152 ». Le banc passe un
    /// <paramref name="prefix"/> vide, une expérience passe « exp-&lt;nom&gt;- ». Le dossier est toujours sous la
    /// racine du projet, d'où qu'on lance la commande, mais son chemin est relatif à <paramref name="from"/>, le
    /// dossier courant par défaut. Depuis la racine, il s'affiche donc « eval/resultats/… ».
    /// </summary>
    public static string ResultsDir(string prefix, DateTime now, string? from = null) =>
        Path.GetRelativePath(from ?? Directory.GetCurrentDirectory(),
                             Path.Combine(ProjectRoot, "eval", "resultats", prefix + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)));

    /// <summary>
    /// Crée un dossier neuf et renvoie le nom retenu : <paramref name="path"/> s'il est libre, sinon « path-2 »,
    /// « path-3 », et ainsi de suite. Cela sert au dossier de sortie par défaut, daté à la seconde
    /// (<see cref="ResultsDir"/>) : deux bancs lancés dans la même seconde, par un script ou depuis deux terminaux,
    /// n'écrivent pas dans le même dossier.
    /// Directory.CreateDirectory réussit aussi sur un dossier qui existe, et vérifier avant de créer laisserait deux
    /// processus prendre le même nom. Le choix du nom et la création se font donc sous un Mutex nommé, un verrou
    /// commun à tous les processus de la machine.
    /// </summary>
    public static string CreateNewDir(string path)
    {
        using var naming = new Mutex(false, NewDirLock);
        try
        {
            naming.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // Un processus s'est arrêté en tenant le verrou : il nous revient quand même.
        }
        try
        {
            for (var number = 1; ; number++)
            {
                var candidate = number == 1 ? path : $"{path}-{number}";
                if (!Path.Exists(candidate))   // ni dossier ni fichier de ce nom
                {
                    Directory.CreateDirectory(candidate);
                    return candidate;
                }
            }
        }
        finally
        {
            naming.ReleaseMutex();
        }
    }

    /// <summary>
    /// Le nom du verrou de <see cref="CreateNewDir"/>. Le préfixe « Global\ » le rend commun à tous les processus,
    /// même lancés depuis deux terminaux. Sans lui, sous Linux, chaque session, donc chaque terminal, aurait le sien.
    /// </summary>
    private const string NewDirLock = @"Global\fyc-assistant-rag-dossiers-de-sortie";

    /// <summary>Le fichier d'où vient cette configuration, cité dans les rapports.</summary>
    public string Source { get; init; } = "";

    public static AppConfig Load(string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("ASSISTANT_CONFIG") ?? Path.Combine(ProjectRoot, "config", "app.json");
        string text;
        try
        {
            text = TextFiles.ReadUtf8(path);   // UTF-8 strict, BOM accepté
        }
        catch (FormatException error)
        {
            throw new ConfigException($"{path} : {error.Message}");   // pas en UTF-8 : l'octet fautif et sa position
        }
        JsonObject raw;
        try
        {
            // Lecture stricte. Une clé en double ou une chaîne qui n'est pas du texte valide est refusée ici, alors
            // que JsonNode ne la verrait qu'au premier accès, en anglais et sans nommer le fichier. La profondeur est
            // bornée à 64 niveaux et les entiers à 4300 chiffres.
            raw = JsonText.Parse(text, strings: true, maxDigits: JsonText.MaxDigits) as JsonObject ?? throw new ConfigException($"{path} : un objet JSON est attendu");
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            throw new ConfigException($"{path} : JSON invalide — {error.Message}");
        }
        CheckKnownKeys(raw, path);
        var ai = Section(raw, "ai_service", path);
        var gen = Section(raw, "generation", path, required: false);
        var retrieval = Section(raw, "retrieval", path);
        var splitter = Section(raw, "splitter", path, required: false);
        var minScores = Section(retrieval, "min_score", path, parent: "retrieval");
        var users = Section(raw, "users", path, required: false);
        string Rel(string relative) => Path.IsPathRooted(relative) ? relative : Path.Combine(ProjectRoot, relative);
        string Where(string section) => $"{path} [{section}]";
        // AI_SERVICE_URL remplace l'adresse, mais base_url reste vérifiée : le fichier reçoit le même verdict quel
        // que soit l'environnement.
        var baseUrl = Value<string>(ai, "base_url", Where("ai_service"));
        // Les bornes de ParagraphSplitter sont vérifiées ici pour que le message nomme le fichier et la clé. Le
        // recouvrement, même celui par défaut, doit rester sous la moitié de max_chars.
        var maxChars = Value(splitter, "max_chars", Where("splitter"), 800, min: 100);
        // Un alias qui n'a pas son seuil prend celui de « default » (MinScoreFor) : cette entrée est obligatoire.
        _ = Value<double>(minScores, "default", Where("retrieval.min_score"));
        return new AppConfig(
            AiBaseUrl: Environment.GetEnvironmentVariable("AI_SERVICE_URL") ?? baseUrl,
            EmbeddingModel: Value<string>(ai, "embedding_model", Where("ai_service")),
            GenerationModel: Value<string>(ai, "generation_model", Where("ai_service")),
            Timeout: TimeSpan.FromSeconds(Value(ai, "timeout_seconds", Where("ai_service"), 300.0, min: 1, max: MaxTimeoutSeconds)),
            CorpusDir: Rel(Value<string>(Section(raw, "corpus", path), "directory", Where("corpus"))),
            IndexPath: Rel(Value<string>(Section(raw, "index", path), "path", Where("index"))),
            SnapshotsDir: Rel(Value(Section(raw, "snapshots", path, required: false), "directory", Where("snapshots"), "eval/instantanes")),
            SplitterMaxChars: maxChars,
            SplitterOverlapChars: Value(splitter, "overlap_chars", Where("splitter"), 120, min: 0, max: maxChars / 2 - 1),
            SplitterIncludeTitle: Value(splitter, "include_title", Where("splitter"), true),
            TopK: Value(retrieval, "top_k", Where("retrieval"), 4, min: 1),
            MinScores: Entries(minScores).ToDictionary(
                kv => kv.Key,
                kv => Value<double>(minScores, kv.Key, Where("retrieval.min_score"), min: -1, max: 1),
                StringComparer.Ordinal),
            PromptName: Value(gen, "prompt", Where("generation"), "answer"),
            Temperature: Value(gen, "temperature", Where("generation"), 0.2, min: 0, max: 2),
            MaxTokens: Value(gen, "max_tokens", Where("generation"), 400, min: 1),
            MaxAttempts: Value(gen, "max_attempts", Where("generation"), 2, min: 1),
            Seed: gen["seed"] is null ? null : Value<int>(gen, "seed", Where("generation")),
            Users: Entries(users).ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlySet<string>)Groups(Section(users, kv.Key, path, parent: "users"), Where($"users.{kv.Key}")),
                StringComparer.Ordinal))
        {
            Source = path,
        };
    }

    public double MinScoreFor(string embeddingModel) =>
        MinScores.TryGetValue(embeddingModel, out var score) ? score : MinScores["default"];

    /// <summary>
    /// Un seuil est-il configuré pour cet alias ? Configuré ne veut pas dire mesuré : le
    /// <c>_commentaire</c> de chaque configuration dit lesquels le sont.
    /// </summary>
    public bool HasThresholdFor(string embeddingModel) => MinScores.ContainsKey(embeddingModel);

    public User User(string name) =>
        Users.TryGetValue(name, out var groups) ? new User(name, groups) : throw new UnknownUserException(name, Users.Keys.Order());

    private static void CheckKnownKeys(JsonObject raw, string path)
    {
        var sections = KnownKeys.Select(kv => (Name: kv.Key, Values: kv.Key.Length == 0 ? raw : raw[kv.Key] as JsonObject, Known: kv.Value))
            .Concat(Entries(raw["users"] as JsonObject ?? new JsonObject())
                .Select(kv => (Name: $"users.{kv.Key}", Values: kv.Value as JsonObject, Known: new[] { "groups" })));
        foreach (var (name, values, known) in sections)
        {
            if (values is null)
            {
                continue;   // absente ou mal formée : Section le dira
            }
            var unknown = Entries(values).Select(kv => kv.Key).Where(k => !known.Contains(k))
                .Order(StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
            {
                var where = name.Length == 0 ? path : $"{path} [{name}]";
                throw new ConfigException($"{where} : clé(s) inconnue(s) [{string.Join(", ", unknown)}] "
                                          + $"(connues : {string.Join(", ", known.Order(StringComparer.Ordinal))})");
            }
        }
    }

    /// <summary>Les entrées d'un objet, sans les commentaires (clés qui commencent par « _ »).</summary>
    private static IEnumerable<KeyValuePair<string, JsonNode?>> Entries(JsonObject section) =>
        section.Where(kv => !kv.Key.StartsWith('_'));

    private static JsonObject Section(JsonObject raw, string name, string path, bool required = true, string? parent = null)
    {
        var label = parent is null ? name : $"{parent}.{name}";
        return raw[name] switch
        {
            null when required => throw new ConfigException($"{path} : section [{label}] absente"),
            null => new JsonObject(),
            JsonObject section => section,
            _ => throw new ConfigException($"{path} : section [{label}] mal formée"),
        };
    }

    /// <summary>Valeur obligatoire d'un type donné.</summary>
    private static T Value<T>(JsonObject section, string key, string where, double? min = null, double? max = null)
    {
        var node = section[key] ?? throw new ConfigException($"{where} : clé « {key} » obligatoire");
        return Typed<T>(node, key, where, min, max);
    }

    /// <summary>
    /// Valeur facultative d'un type donné, avec sa valeur par défaut. Celle-ci est bornée elle aussi : le
    /// recouvrement par défaut ne convient pas à tout max_chars.
    /// </summary>
    private static T Value<T>(JsonObject section, string key, string where, T fallback, double? min = null, double? max = null) =>
        section[key] is { } node
            ? Typed<T>(node, key, where, min, max)
            : Bounded(fallback, $"{Convert.ToString(fallback, CultureInfo.InvariantCulture)} (valeur par défaut)", key, where, min, max);

    private static T Typed<T>(JsonNode? node, string key, string where, double? min, double? max)
    {
        if (node is not JsonValue value || !value.TryGetValue<T>(out var parsed) || parsed is null)
        {
            throw new ConfigException($"{where} : « {key} » doit être {KindNames[typeof(T)]}, pas {Show(node)}");
        }
        return Bounded(parsed, Show(node), key, where, min, max);
    }

    private static T Bounded<T>(T value, string shown, string key, string where, double? min, double? max)
    {
        if (value is IConvertible number and not string and not bool)
        {
            var d = number.ToDouble(CultureInfo.InvariantCulture);
            if (d < min || d > max)   // JSON n'a pas de NaN, qui passerait ces deux comparaisons
            {
                throw new ConfigException($"{where} : « {key} » = {shown}, {Range(min, max)}");
            }
        }
        return value;
    }

    /// <summary>
    /// Le message d'une valeur hors bornes. Il n'a que deux formes, car une borne haute vient toujours avec une
    /// borne basse : c'est le cas du délai, du recouvrement, des seuils et de la température.
    /// </summary>
    private static string Range(double? min, double? max) =>
        max is null ? $"doit valoir au moins {Number(min)}" : $"doit être compris entre {Number(min)} et {Number(max)}";

    private static string? Number(double? bound) => bound?.ToString(CultureInfo.InvariantCulture);

    /// <summary>La valeur en JSON. Un nombre garde son écriture du fichier, par exemple « 1e300 ».</summary>
    private static string Show(JsonNode? node) => node?.ToJsonString(OneLine) ?? "null";

    private static HashSet<string> Groups(JsonObject user, string where) =>
        user["groups"] switch
        {
            null => new HashSet<string> { AccessPolicy.PublicGroup },
            JsonArray groups when groups.All(g => g is JsonValue v && v.TryGetValue<string>(out _))
                => groups.Select(g => g!.GetValue<string>()).ToHashSet(),
            var other => throw new ConfigException($"{where} : « groups » doit être une liste de chaînes, pas {Show(other)}"),
        };

    private static string FindProjectRoot()
    {
        // Le binaire vit dans src/Assistant.Cli/bin/…/net8.0 : on remonte jusqu'à config/app.json.
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "config", "app.json")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
        }
        return Directory.GetCurrentDirectory();
    }
}
