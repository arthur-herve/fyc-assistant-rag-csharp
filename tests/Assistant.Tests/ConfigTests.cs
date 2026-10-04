// Configuration et ligne de commande : une erreur de saisie se voit tout de suite, avec le fichier et la clé.

using System.Text;
using Assistant.Application;
using Assistant.Cli;
using Assistant.Infrastructure;
using Xunit;

namespace Assistant.Tests;

/// <summary>AI_SERVICE_URL vaut pour tout le processus : les tests qui la posent ne tournent pas en même temps que les autres.</summary>
[CollectionDefinition(nameof(EnvironmentCollection), DisableParallelization = true)]
public sealed class EnvironmentCollection { }

[Collection(nameof(EnvironmentCollection))]
public sealed class ConfigTests : IDisposable
{
    private const string Valid = """
        {
          "ai_service": { "base_url": "http://127.0.0.1:8100", "embedding_model": "hashing", "generation_model": "extractive" },
          "corpus": { "directory": "corpus/solveo" },
          "index": { "path": "data/index.json" },
          "retrieval": { "min_score": { "default": 0.4, "hashing": 0.15 } },
          "decorators": { "validate_output": true }
        }
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("fyc-config-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AppConfig Load(string json)
    {
        var path = Path.Combine(_dir, "app.json");
        File.WriteAllText(path, json);
        return AppConfig.Load(path);
    }

    [Theory]
    [InlineData("app.json")]
    [InlineData("app-ollama.json")]
    [InlineData("app-ollama-complet.json")]
    public void The_shipped_configurations_load(string name) =>
        Assert.NotEmpty(AppConfig.Load(Path.Combine(AppConfig.ProjectRoot, "config", name)).Users);

    [Theory]
    [InlineData("embedding_model", "\"embedding_model\": \"hashing\", ", "")]
    [InlineData("validate_ouput", "\"validate_output\": true", "\"validate_ouput\": true")]
    [InlineData("validate_output", "\"validate_output\": true", "\"validate_output\": \"oui\"")]
    [InlineData("max_attempts", "\"decorators\"", "\"generation\": { \"max_attempts\": 0 }, \"decorators\"")]
    [InlineData("top_k", "\"min_score\"", "\"top_k\": 0, \"min_score\"")]
    [InlineData("topk", "\"min_score\"", "\"topk\": 8, \"min_score\"")]
    [InlineData("max_char", "\"decorators\"", "\"splitter\": { \"max_char\": 300 }, \"decorators\"")]
    [InlineData("overlap_chars", "\"decorators\"", "\"splitter\": { \"overlap_chars\": \"120\" }, \"decorators\"")]
    [InlineData("timeout_seconds", "\"generation_model\": \"extractive\"", "\"generation_model\": \"extractive\", \"timeout_seconds\": 0")]
    [InlineData("retreival", "\"decorators\"", "\"retreival\": { \"top_k\": 8 }, \"decorators\"")]
    public void Errors_name_the_key(string key, string from, string to)
    {
        var error = Assert.Throws<ConfigException>(() => Load(Valid.Replace(from, to)));
        Assert.Contains(key, error.Message);
        Assert.Contains("app.json", error.Message);
    }

    // Le message suit le nom du fichier.
    [Theory]
    [InlineData("\"embedding_model\": \"hashing\", ", "", " [ai_service] : clé « embedding_model » obligatoire")]
    [InlineData("\"corpus\": { \"directory\": \"corpus/solveo\" },", "", " : section [corpus] absente")]
    [InlineData("\"generation_model\": \"extractive\"", "\"generation_model\": \"extractive\", \"timeout_seconds\": 0",
                " [ai_service] : « timeout_seconds » = 0, doit être compris entre 1 et 86400")]
    [InlineData("\"generation_model\": \"extractive\"", "\"generation_model\": \"extractive\", \"timeout_seconds\": 86401",
                " [ai_service] : « timeout_seconds » = 86401, doit être compris entre 1 et 86400")]
    [InlineData("\"min_score\"", "\"top_k\": 0, \"min_score\"", " [retrieval] : « top_k » = 0, doit valoir au moins 1")]
    [InlineData("\"min_score\"", "\"top_k\": true, \"min_score\"", " [retrieval] : « top_k » doit être un entier, pas true")]
    [InlineData("\"min_score\"", "\"topk\": 8, \"min_score\"", " [retrieval] : clé(s) inconnue(s) [topk] (connues : min_score, top_k)")]
    [InlineData("\"hashing\": 0.15", "\"hashing\": 1.5", " [retrieval.min_score] : « hashing » = 1.5, doit être compris entre -1 et 1")]
    [InlineData("\"hashing\": 0.15", "\"hashing\": -1.5", " [retrieval.min_score] : « hashing » = -1.5, doit être compris entre -1 et 1")]
    [InlineData("{ \"default\": 0.4, \"hashing\": 0.15 }", "0.4", " : section [retrieval.min_score] mal formée")]
    [InlineData("\"decorators\"", "\"generation\": { \"temperature\": 2.5 }, \"decorators\"",
                " [generation] : « temperature » = 2.5, doit être compris entre 0 et 2")]
    [InlineData("\"decorators\"", "\"generation\": { \"temperature\": \"chaud\" }, \"decorators\"",
                " [generation] : « temperature » doit être un nombre, pas \"chaud\"")]
    [InlineData("\"decorators\"", "\"generation\": { \"max_tokens\": 0 }, \"decorators\"", " [generation] : « max_tokens » = 0, doit valoir au moins 1")]
    [InlineData("\"decorators\"", "\"generation\": { \"max_attempts\": 0 }, \"decorators\"", " [generation] : « max_attempts » = 0, doit valoir au moins 1")]
    [InlineData("\"decorators\"", "\"generation\": { \"seed\": \"42\" }, \"decorators\"", " [generation] : « seed » doit être un entier, pas \"42\"")]
    [InlineData("\"decorators\"", "\"generation\": { \"prompt\": 3 }, \"decorators\"", " [generation] : « prompt » doit être une chaîne, pas 3")]
    [InlineData("\"validate_output\": true", "\"validate_output\": \"oui\"", " [decorators] : « validate_output » doit être un booléen, pas \"oui\"")]
    [InlineData("\"validate_output\": true", "\"validate_output\": true, \"max_output_chars\": 0",
                " [decorators] : « max_output_chars » = 0, doit valoir au moins 1")]
    [InlineData("\"validate_output\": true", "\"validate_output\": true, \"retries\": -1", " [decorators] : « retries » = -1, doit valoir au moins 0")]
    [InlineData("\"decorators\"", "\"splitter\": { \"max_chars\": 99 }, \"decorators\"", " [splitter] : « max_chars » = 99, doit valoir au moins 100")]
    [InlineData("\"decorators\"", "\"splitter\": { \"overlap_chars\": 400 }, \"decorators\"",
                " [splitter] : « overlap_chars » = 400, doit être compris entre 0 et 399")]
    [InlineData("\"decorators\"", "\"splitter\": { \"max_chars\": 200 }, \"decorators\"",
                " [splitter] : « overlap_chars » = 120 (valeur par défaut), doit être compris entre 0 et 99")]
    [InlineData("\"decorators\"", "\"splitter\": { \"include_title\": \"désactivé\" }, \"decorators\"",
                " [splitter] : « include_title » doit être un booléen, pas \"désactivé\"")]
    [InlineData("\"decorators\"", "\"users\": { \"alice\": { \"group\": [\"rh\"] } }, \"decorators\"",
                " [users.alice] : clé(s) inconnue(s) [group] (connues : groups)")]
    [InlineData("\"decorators\"", "\"users\": { \"alice\": { \"groups\": [1, \"rh\"] } }, \"decorators\"",
                " [users.alice] : « groups » doit être une liste de chaînes, pas [1,\"rh\"]")]
    [InlineData("\"decorators\"", "\"users\": { \"alice\": { \"groups\": \"rh\" } }, \"decorators\"",
                " [users.alice] : « groups » doit être une liste de chaînes, pas \"rh\"")]
    [InlineData("\"decorators\"", "\"users\": { \"alice\": { \"groups\": [[\"rh\"]] } }, \"decorators\"",
                " [users.alice] : « groups » doit être une liste de chaînes, pas [[\"rh\"]]")]
    [InlineData("\"decorators\"", "\"users\": { \"alice\": \"tous\" }, \"decorators\"", " : section [users.alice] mal formée")]
    public void Errors_name_the_file_the_section_the_key_and_what_is_expected(string from, string to, string expected)
    {
        var json = Valid.Replace(from, to);
        Assert.NotEqual(Valid, json);
        Assert.Contains($"app.json{expected}", Assert.Throws<ConfigException>(() => Load(json)).Message);
    }

    private static readonly string Huge = "1" + new string('0', 5000);

    private const string TooLong = " : JSON invalide — nombre entier de plus de 4300 chiffres";

    /// <summary>
    /// Des entiers démesurés. Plus de 4300 chiffres : refusé à la lecture (maxDigits) ; moins, à une clé réelle, l'infini,
    /// hors bornes, cité tel qu'écrit dans le fichier.
    /// </summary>
    public static TheoryData<string, string, string> HugeIntegers => new()
    {
        { "\"min_score\"", $"\"top_k\": {Huge}, \"min_score\"", TooLong },
        { "\"decorators\"", $"\"generation\": {{ \"seed\": -{Huge} }}, \"decorators\"", TooLong },
        { "\"decorators\"", $"\"users\": {{ \"alice\": {{ \"groups\": [{Huge}, \"rh\"] }} }}, \"decorators\"", TooLong },
        { "\"generation_model\": \"extractive\"", $"\"generation_model\": \"extractive\", \"timeout_seconds\": 1{new string('0', 400)}",
          $" [ai_service] : « timeout_seconds » = 1{new string('0', 400)}, doit être compris entre 1 et 86400" },
        { "\"generation_model\": \"extractive\"", $"\"generation_model\": \"extractive\", \"timeout_seconds\": {Huge}.5",
          $" [ai_service] : « timeout_seconds » = {Huge}.5, doit être compris entre 1 et 86400" },
    };

    [Theory]
    [MemberData(nameof(HugeIntegers))]
    public void Huge_integers_are_refused_at_reading_or_quoted_as_written(string from, string to, string expected)
    {
        var json = Valid.Replace(from, to);
        Assert.NotEqual(Valid, json);
        Assert.EndsWith($"app.json{expected}", Assert.Throws<ConfigException>(() => Load(json)).Message);
    }

    // Au-delà de 24 jours, HttpClient refuse le délai (message anglais, sans fichier ni clé) ; au-delà d'environ
    // 9,2e11 s, TimeSpan.FromSeconds lève une OverflowException que rien n'attrape.
    [Theory]
    [InlineData("3000000", "3000000")]
    [InlineData("1e300", "1e300")]   // citée telle qu'écrite dans le fichier
    public void An_absurd_timeout_is_refused_at_load(string written, string shown)
    {
        var json = Valid.Replace("\"generation_model\": \"extractive\"", $"\"generation_model\": \"extractive\", \"timeout_seconds\": {written}");
        var error = Assert.Throws<ConfigException>(() => Load(json));
        Assert.Contains($"app.json [ai_service] : « timeout_seconds » = {shown}, doit être compris entre 1 et 86400", error.Message);
    }

    [Fact]
    public void A_file_that_is_not_utf8_names_the_file_and_the_byte()
    {
        // File.ReadAllText aurait lu « corpus/solv�o » sans rien dire. La position se compte après la marque d'ordre des
        // octets ; avec elle, un fichier en UTF-8 se lit.
        var json = Valid.Replace("corpus/solveo", "corpus/solvéo");
        var path = Path.Combine(_dir, "app.json");
        foreach (var bytes in new[] { Encoding.Latin1.GetBytes(json), Bytes.WithBom(Encoding.Latin1.GetBytes(json)) })
        {
            File.WriteAllBytes(path, bytes);
            Assert.Equal($"{path} : pas en UTF-8 (octet 0xe9 à la position {json.IndexOf('é')})",
                         Assert.Throws<ConfigException>(() => AppConfig.Load(path)).Message);
        }
        File.WriteAllBytes(path, Bytes.WithBom(Encoding.UTF8.GetBytes(json)));
        Assert.EndsWith("solvéo", AppConfig.Load(path).CorpusDir);
    }

    [Fact]
    public void Sixty_five_levels_are_a_config_error() =>
        Assert.Contains("app.json : JSON invalide — JSON trop imbriqué : plus de 64 niveaux",
                        Assert.Throws<ConfigException>(() => Load(Valid.Replace("\"decorators\"", "\"_x\": " + Bytes.Nested(64) + ", \"decorators\""))).Message);

    // (max_chars, overlap_chars, accepté) ; null : clé absente, donc valeur par défaut.
    [Theory]
    [InlineData(100, 49, true)]
    [InlineData(100, 50, false)]
    [InlineData(99, 0, false)]
    [InlineData(801, 399, true)]
    [InlineData(801, 400, false)]
    [InlineData(null, 399, true)]
    [InlineData(null, 400, false)]
    [InlineData(242, null, true)]
    [InlineData(241, null, false)]
    public void The_splitter_bounds_are_those_of_the_splitter(int? maxChars, int? overlapChars, bool accepted)
    {
        var settings = new[] { ("max_chars", maxChars), ("overlap_chars", overlapChars) }
            .Where(s => s.Item2 is not null).Select(s => $"\"{s.Item1}\": {s.Item2}");
        var json = Valid.Replace("\"decorators\"", $"\"splitter\": {{ {string.Join(", ", settings)} }}, \"decorators\"");
        var defaults = new ParagraphSplitter();
        var direct = Record.Exception(() => new ParagraphSplitter(maxChars ?? defaults.MaxChars, overlapChars ?? defaults.OverlapChars));
        var loaded = Record.Exception(() => Load(json));
        Assert.Equal(accepted, direct is null);   // refusé par le découpeur…
        Assert.Equal(accepted, loaded is null);   // … donc dès le chargement, avec le fichier et la clé
        if (accepted)
        {
            var config = Load(json);
            Assert.Equal(new ParagraphSplitter(maxChars ?? defaults.MaxChars, overlapChars ?? defaults.OverlapChars).Describe(),
                         new ParagraphSplitter(config.SplitterMaxChars, config.SplitterOverlapChars, config.SplitterIncludeTitle).Describe());
        }
        else
        {
            Assert.IsType<ConfigException>(loaded);
        }
    }

    [Theory]
    [InlineData("{ \"ai_service\": ", "JSON invalide")]
    [InlineData("[]", "un objet JSON est attendu")]
    public void A_malformed_file_is_a_config_error_that_names_it(string json, string expected) =>
        Assert.Contains($"app.json : {expected}", Assert.Throws<ConfigException>(() => Load(json)).Message);

    // JSON strict. Sans ce contrôle, JsonNode ne lisait l'objet ou la chaîne qu'au premier
    // accès : ArgumentException (clé en double) ou InvalidOperationException (« \ud800 » isolé, que rien n'attrape).
    [Theory]
    [InlineData("\"index\"", "\"corpus\": { \"directory\": \"autre\" }, \"index\"", "clé « corpus » en double")]
    [InlineData("\"hashing\": 0.15", "\"hashing\": 0.15, \"hashing\": 0.2", "clé « hashing » en double")]
    [InlineData("\"corpus/solveo\"", "\"corpus/solveo\\ud800\"", "chaîne qui n'est pas du texte")]
    [InlineData("\"base_url\"", "\"_n\\ud800\": \"x\", \"base_url\"", "chaîne qui n'est pas du texte")]
    public void A_file_that_is_not_strict_json_is_a_config_error_that_names_it(string from, string to, string expected)
    {
        var json = Valid.Replace(from, to);
        Assert.NotEqual(Valid, json);
        Assert.Contains($"app.json : JSON invalide — {expected}", Assert.Throws<ConfigException>(() => Load(json)).Message);
    }

    [Fact]
    public void Keys_that_differ_only_by_case_are_two_keys()
    {
        // JSON distingue les clés caractère par caractère (RFC 8259) : « Alice » et « alice » sont deux utilisateurs,
        // pas une clé en double.
        var config = Load(Valid.Replace("\"decorators\"", "\"users\": { \"alice\": {}, \"Alice\": {} }, \"decorators\""));
        Assert.Equal(new[] { "Alice", "alice" }, config.Users.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_user_without_groups_is_in_the_public_group_only() =>
        Assert.Equal(new[] { "tous" }, Load(Valid.Replace("\"decorators\"", "\"users\": { \"alice\": {} }, \"decorators\"")).User("alice").Groups);

    [Fact]
    public void A_leading_underscore_is_a_comment_at_every_level()
    {
        var config = Load(Valid
            .Replace("\"base_url\"", "\"_note\": \"x\", \"base_url\"")
            .Replace("\"min_score\"", "\"_note\": \"x\", \"min_score\"")
            .Replace("\"default\": 0.4", "\"_calibre\": \"le 11/09\", \"default\": 0.4")
            .Replace("\"validate_output\": true", "\"_note\": \"x\", \"validate_output\": true")
            .Replace("\"decorators\"", "\"users\": { \"_note\": \"x\", \"_modele\": { \"groupes\": [\"tous\"] }, "
                                       + "\"alice\": { \"_note\": \"x\", \"groups\": [\"tous\"] } }, \"decorators\""));
        Assert.Equal(new[] { "default", "hashing" }, config.MinScores.Keys.Order());
        Assert.Equal(new[] { "validate_output" }, config.Decorators.Keys);
        Assert.Equal(new[] { "alice" }, config.Users.Keys);   // « _modele » est un commentaire : ni vérifié, ni utilisateur
    }

    [Fact]
    public void The_ai_service_variable_does_not_skip_the_check_of_base_url()
    {
        var previous = Environment.GetEnvironmentVariable("AI_SERVICE_URL");
        Environment.SetEnvironmentVariable("AI_SERVICE_URL", "http://machine-gpu:8100");
        try
        {
            Assert.Equal("http://machine-gpu:8100", Load(Valid).AiBaseUrl);
            Assert.Contains("app.json [ai_service] : « base_url » doit être une chaîne, pas 8100",
                            Assert.Throws<ConfigException>(() => Load(Valid.Replace("\"http://127.0.0.1:8100\"", "8100"))).Message);
            Assert.Contains("app.json [ai_service] : clé « base_url » obligatoire",
                            Assert.Throws<ConfigException>(() => Load(Valid.Replace("\"base_url\": \"http://127.0.0.1:8100\", ", ""))).Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AI_SERVICE_URL", previous);
        }
    }

    [Fact]
    public void Threshold_per_model_and_uncalibrated_fallback()
    {
        var config = Load(Valid);
        Assert.Equal(0.15, config.MinScoreFor("hashing"));
        Assert.True(config.HasThresholdFor("hashing"));
        Assert.Equal(0.4, config.MinScoreFor("nomic"));        // repli sur « default »…
        Assert.False(config.HasThresholdFor("nomic"));         // … signalé (ligne de commande, banc)
    }

    [Fact]
    public void Help_is_a_success_and_a_mistyped_option_an_error()
    {
        Assert.Equal(0, Program.Main(new[] { "--help" }));
        Assert.Equal(1, Program.Main(Array.Empty<string>()));
        // « --usr bruno » ne répond pas au nom d'alice : l'option inconnue est refusée avant tout travail
        // (le code 1 seul ne le prouve pas : sans index, « ask » échouerait de toute façon).
        var stderr = new StringWriter();
        var previous = Console.Error;
        Console.SetError(stderr);
        try
        {
            Assert.Equal(1, Program.Main(new[] { "ask", "Combien de jours ?", "--usr", "bruno" }));
        }
        finally
        {
            Console.SetError(previous);
        }
        Assert.Contains("option(s) inconnue(s) pour ask : --usr", stderr.ToString());
    }

    [Fact]
    public void The_snapshot_configuration_includes_the_settings_that_change_statuses()
    {
        var config = AppConfig.Load() with { AiBaseUrl = "http://127.0.0.1:1" };
        var container = Composition.Build(config, new Overrides(IndexPath: Path.Combine(_dir, "index.json"),
                                                                SnapshotsDir: Path.Combine(_dir, "instantanes")));
        var snapshot = container.RecordSnapshot.Execute("vide", Array.Empty<SnapshotQuestion>());
        Assert.Equal((object?)2, snapshot.Configuration["max_attempts"]);
        Assert.Equal((object?)true, snapshot.Configuration["validate_output"]);
        Assert.Equal((object?)1500, snapshot.Configuration["max_output_chars"]);
    }
}
