// La ligne de commande, appelée comme on la tape : une saisie fausse est refusée avant tout travail, et les
// affichages se lisent (null, true, [a, b], « 33 % »).

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Assistant.Application;
using Assistant.Cli;
using Assistant.Domain;
using Xunit;

namespace Assistant.Tests;

/// <summary>
/// Console.Out et Console.Error sont partagés par tout le processus : les tests qui les lisent ou y écrivent ne
/// tournent pas en même temps. CliTests et BenchmarkRunTests sont dans cette collection, le test de l'aide de
/// ConfigTests dans celle de l'environnement : deux collections non parallèles, que xUnit fait passer l'une après
/// l'autre, après les autres tests.
/// </summary>
[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleCollection
{
}

[Collection("Console")]
public sealed class CliTests : IDisposable
{
    private const string Question = "Combien de jours de télétravail par semaine ?";
    private const string Warning = "Attention : aucun seuil de pertinence configuré pour « hashing » : valeur `default` 0.15";
    private readonly string _dir = Directory.CreateTempSubdirectory("fyc-cli-").FullName;
    private readonly string _missing;   // lue seulement si la saisie est juste

    public CliTests() => _missing = Path.Combine(_dir, "absente.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Code de retour, sortie et erreurs de Program.Main, comme dans un terminal.</summary>
    private static (int Code, string Out, string Err) Run(params string[] argv)
    {
        var (previousOut, previousError) = (Console.Out, Console.Error);
        var (output, errors) = (new StringWriter(), new StringWriter());
        Console.SetOut(output);
        Console.SetError(errors);
        try
        {
            return (Program.Main(argv), output.ToString().Replace("\r\n", "\n"), errors.ToString().Replace("\r\n", "\n"));
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    /// <summary>Pas de seuil pour « hashing » (repli sur `default`), un seuil pour « hashing-512 ».</summary>
    private string WriteConfig(string aiUrl, string corpus = "corpus/solveo")
    {
        var path = Path.Combine(_dir, "app.json");
        File.WriteAllText(path, new JsonObject
        {
            ["ai_service"] = new JsonObject
            {
                ["base_url"] = aiUrl, ["embedding_model"] = "hashing", ["generation_model"] = "extractive", ["timeout_seconds"] = 5,
            },
            ["corpus"] = new JsonObject { ["directory"] = corpus },
            ["index"] = new JsonObject { ["path"] = Path.Combine(_dir, "index.json") },
            ["snapshots"] = new JsonObject { ["directory"] = Path.Combine(_dir, "instantanes") },
            ["retrieval"] = new JsonObject { ["min_score"] = new JsonObject { ["default"] = 0.15, ["hashing-512"] = 0.15 } },
            ["users"] = new JsonObject { ["alice"] = new JsonObject { ["groups"] = new JsonArray("tous") } },
        }.ToJsonString());
        return path;
    }

    /// <summary>Un jeu d'une question, posée par alice (connue de toutes les configurations des tests).</summary>
    private string WriteQuestions()
    {
        var path = Path.Combine(_dir, "questions.json");
        File.WriteAllText(path, """{"questions": [{"id": "tt", "user": "alice", "question": "Combien de jours de télétravail ?"}]}""");
        return path;
    }

    /// <summary>
    /// Un projet en miniature dans le dossier du test : une copie du binaire (bin/), la configuration (config/app.json :
    /// le faux service IA, le corpus Solvéo de ce dépôt) et les prompts. Lancé de là (Launch), assistant y voit la racine
    /// du projet (AppConfig.ProjectRoot : le premier dossier, au-dessus du binaire, qui contient config/app.json) : sans
    /// --out, ses dossiers de résultats y sont écrits, et non dans ce dépôt.
    /// </summary>
    private string CopyProject(string aiUrl)
    {
        var root = Directory.CreateDirectory(Path.Combine(_dir, "projet")).FullName;
        var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        foreach (var file in new[] { "assistant.dll", "assistant.deps.json", "assistant.runtimeconfig.json",
                                     "Assistant.Application.dll", "Assistant.Domain.dll", "Assistant.Infrastructure.dll" })
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(bin, file));
        }
        var prompts = Directory.CreateDirectory(Path.Combine(root, "prompts")).FullName;
        foreach (var file in Directory.GetFiles(Composition.PromptsDir))
        {
            File.Copy(file, Path.Combine(prompts, Path.GetFileName(file)));
        }
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.Copy(WriteConfig(aiUrl, corpus: Path.Combine(AppConfig.ProjectRoot, "corpus", "solveo")), Path.Combine(root, "config", "app.json"));
        return root;
    }

    /// <summary>
    /// Code de retour, sortie et erreurs de la commande lancée pour de vrai, dans un autre processus
    /// (dotnet bin/assistant.dll), depuis la copie <paramref name="root"/> (CopyProject), qui est aussi son dossier courant.
    /// </summary>
    private static (int Code, string Out, string Err) Launch(string root, params string[] argv)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,   // Program écrit en UTF-8, même redirigé
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in argv.Prepend(Path.Combine(root, "bin", "assistant.dll")))
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var (output, errors) = (process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
        if (!process.WaitForExit(TimeSpan.FromMinutes(1)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"assistant {string.Join(' ', argv)} : pas fini en une minute");
        }
        return (process.ExitCode, output.Result.Replace("\r\n", "\n"), errors.Result.Replace("\r\n", "\n"));
    }

    private void AssertRefused(string message, string[] argv)
    {
        var (code, output, errors) = Run(argv.Append("--config").Append(_missing).ToArray());
        Assert.Equal(1, code);
        Assert.Contains($"Erreur : {message}", errors);
        Assert.Equal("", output);
        Assert.DoesNotContain("Attention", errors);   // refusé avant de lire la configuration
    }

    [Theory]
    [InlineData("option(s) inconnue(s) pour status : -json (acceptées : --config, --embedding-model, --generation-model, --json, --prompt, --verbose, -v)",
                "status", "-json")]
    [InlineData("option(s) inconnue(s) pour index : -if-stale", "index", "-if-stale")]
    [InlineData("option(s) inconnue(s) pour status : --jsn", "status", "--jsn")]   // et non « --jsn attend une valeur »
    [InlineData("option(s) inconnue(s) pour status : --if-stale, --port", "status", "--if-stale", "--port", "8000")]
    [InlineData("option(s) inconnue(s) pour ask : --us", "ask", Question, "--us", "bruno")]
    [InlineData("option(s) inconnue(s) pour ask : --usr", "ask", Question, "--usr=bruno")]
    [InlineData("option(s) inconnue(s) pour snapshot list : --limit", "snapshot", "list", "--limit", "3")]
    [InlineData("option(s) inconnue(s) pour snapshot compare : --questions", "snapshot", "compare", "a", "b", "--questions", "q.json")]
    [InlineData("option(s) inconnue(s) pour serve : --json", "serve", "--json")]
    [InlineData("option(s) inconnue(s) pour benchmark : -v", "benchmark", "-v")]
    [InlineData("option(s) inconnue(s) pour experience prompt-v2 : --runs", "experience", "prompt-v2", "--runs", "5")]
    [InlineData("option(s) inconnue(s) pour experience stabilite : --max-chars", "experience", "stabilite", "--max-chars", "300")]
    [InlineData("option(s) inconnue(s) pour experience cace-decoupage : --other", "experience", "cace-decoupage", "--other", "nomic")]
    [InlineData("option(s) inconnue(s) pour status : --jsn (acceptées", "status", "--jsn", "--jsn")]   // nommée une fois
    public void A_mistyped_abbreviated_or_foreign_option_is_refused_before_any_work(string message, params string[] argv) =>
        AssertRefused(message, argv);

    [Theory]
    [InlineData("--json", "--json", "status")]
    [InlineData("-v", "-v", "snapshot", "list")]
    [InlineData("-v", "snapshot", "-v", "list")]
    [InlineData("--other", "experience", "--other", "nomic", "prompt-v2")]
    [InlineData("--config", "--config=app.json", "status")]
    [InlineData("--json", "--json")]
    public void The_command_comes_first(string option, params string[] argv) =>
        AssertRefused($"option placée avant la commande : {option} (la commande vient d'abord : assistant <commande> [options])", argv);

    /// <summary>« CONFIG » devient « --config absente.json » : à placer avant « -- », après lequel ce serait un argument.</summary>
    private string[] WithConfig(string[] argv) => argv.SelectMany(a => a == "CONFIG" ? new[] { "--config", _missing } : new[] { a }).ToArray();

    // Après « -- », tout est un argument, même ce qui ressemble à une option (une question qui commence par un tiret,
    // « -h ») ; les options se placent donc avant.
    [Theory]
    [InlineData("ask", "CONFIG", "--", "-x")]
    [InlineData("ask", "--user", "bruno", "CONFIG", "--", "-v jours")]
    [InlineData("ask", "CONFIG", "--", "-télétravail")]   // l'exemple de l'aide et du README
    [InlineData("ask", "CONFIG", "--", "-h")]   // pas une demande d'aide
    [InlineData("snapshot", "compare", "CONFIG", "--", "-a", "-b")]
    [InlineData("status", "CONFIG", "--")]
    public void A_double_dash_ends_the_options(params string[] argv)
    {
        var (code, output, errors) = Run(WithConfig(argv));
        Assert.Equal((1, ""), (code, output));
        Assert.Contains("absente.json", errors);   // la saisie est acceptée : c'est le fichier qui manque
    }

    [Theory]
    [InlineData("argument(s) en trop pour status : foo", "status", "CONFIG", "--", "foo")]
    [InlineData("argument(s) en trop pour status : --json", "status", "CONFIG", "--", "--json")]
    [InlineData("argument(s) en trop pour status : --", "status", "CONFIG", "--", "--")]   // le second est un argument
    [InlineData("argument(s) en trop pour benchmark : b", "benchmark", "--embedding", "a", "CONFIG", "--", "b")]
    [InlineData("ask attend une seule question, entre guillemets", "ask", "Q", "CONFIG", "--", "--json")]
    [InlineData("option(s) inconnue(s) pour ask : --usr", "ask", "--usr", "x", "CONFIG", "--", "-h")]   // -h : un argument
    [InlineData("option placée avant la commande : --", "--", "status", "CONFIG")]
    [InlineData("option placée avant la commande : --", "snapshot", "--", "list", "CONFIG")]
    public void After_a_double_dash_everything_is_an_argument(string message, params string[] argv)
    {
        var (code, output, errors) = Run(WithConfig(argv));
        Assert.Equal((1, ""), (code, output));
        Assert.Contains($"Erreur : {message}", errors);
        Assert.DoesNotContain("Attention", errors);   // refusé avant de lire la configuration
    }

    [Fact]
    public void A_question_that_starts_with_a_dash_follows_a_double_dash()
    {
        using var ai = new FakeAiService();
        var config = WriteConfig(ai.Url);
        Assert.Equal(0, Run("index", "--config", config).Code);
        var (code, output, errors) = Run("ask", "--json", "--config", config, "--", "-télétravail");
        Assert.True(code == 0, errors);
        Assert.Equal("-télétravail", JsonNode.Parse(output)!["question"]!.GetValue<string>());
    }

    [Fact]
    public void A_separator_alone_is_a_question_blanks_alone_are_not()
    {
        // U+001C n'est pas un blanc pour Trim() : la question est posée (code 0), là où des blancs sont refusés.
        using var ai = new FakeAiService();
        var config = WriteConfig(ai.Url);
        Assert.Equal(0, Run("index", "--config", config).Code);
        var (code, output, errors) = Run("ask", "--json", "--config", config, "--", "\u001c");
        Assert.True(code == 0, errors);
        Assert.Equal("\u001c", JsonNode.Parse(output)!["question"]!.GetValue<string>());
        (code, output, errors) = Run("ask", "--config", config, "--", " \t");
        Assert.Equal((1, ""), (code, output));
        Assert.Contains("Erreur : La question est vide.", errors);
    }

    [Theory]
    [InlineData("a", "--", "--")]
    [InlineData("--", "a", "--")]
    public void A_double_dash_typed_after_the_one_that_ends_the_options_is_a_name(params string[] names)
    {
        // Le second nom est « -- », un nom invalide : un message, pas une trace.
        using var ai = new FakeAiService();
        var config = WriteConfig(ai.Url);
        Assert.Equal(0, Run("index", "--config", config).Code);
        Assert.Equal(0, Run("snapshot", "record", "a", "--limit", "1", "--config", config).Code);
        var (code, output, errors) = Run(new[] { "snapshot", "compare", "--config", config }.Concat(names).ToArray());
        Assert.Equal((1, ""), (code, output));
        Assert.Contains("Erreur : nom d'instantané invalide : « -- » (", errors);
    }

    [Theory]
    [InlineData("argument(s) en trop pour status : foo", "status", "foo")]
    // « - » seul, un nombre négatif ou un texte avec une espace est un argument, pas une option.
    [InlineData("argument(s) en trop pour status : -", "status", "-")]
    [InlineData("argument(s) en trop pour status : -1", "status", "-1")]
    [InlineData("argument(s) en trop pour status : -5 jours ?", "status", "-5 jours ?")]
    [InlineData("argument(s) en trop pour index : maintenant", "index", "maintenant")]
    [InlineData("argument(s) en trop pour snapshot list : tout", "snapshot", "list", "tout")]
    [InlineData("argument(s) en trop pour snapshot compare : c", "snapshot", "compare", "a", "b", "c")]
    [InlineData("argument(s) en trop pour serve : 8000", "serve", "8000")]
    [InlineData("argument(s) en trop pour benchmark : hashing", "benchmark", "hashing")]
    [InlineData("argument(s) en trop pour experience stabilite : encore", "experience", "stabilite", "encore")]
    [InlineData("ask attend une seule question, entre guillemets", "ask", "Combien", "de", "jours", "?")]
    public void An_extra_argument_is_refused(string message, params string[] argv) => AssertRefused(message, argv);

    [Theory]
    [InlineData("l'option --json ne prend pas de valeur", "status", "--json=1")]
    [InlineData("snapshot attend record, compare ou list", "snapshot", "bidule")]
    [InlineData("expérience inconnue : nope (connues : cace-decoupage, changement-embeddings, changement-generateur, prompt-v2, stabilite)",
                "experience", "nope")]
    [InlineData("l'option --port attend un port entre 1 et 65535, pas « 99999 »", "serve", "--port", "99999")]
    [InlineData("l'option --port attend un port entre 1 et 65535, pas « -1 »", "serve", "--port", "-1")]
    [InlineData("l'option --port attend un port entre 1 et 65535, pas « 0 »", "serve", "--port", "0")]
    [InlineData("l'option --port attend un port entre 1 et 65535, pas « 65536 »", "serve", "--port=65536")]
    [InlineData("ask attend une seule question, entre guillemets", "ask")]
    [InlineData("snapshot record attend un nom", "snapshot", "record")]
    [InlineData("snapshot compare attend deux noms", "snapshot", "compare", "a")]
    public void A_wrong_value_or_an_unknown_subcommand_is_refused_before_any_work(string message, params string[] argv) =>
        AssertRefused(message, argv);

    // Une option d'expérience fausse : la configuration est lue d'abord (absente, c'est elle qui est dite), puis
    // l'option est refusée par Experiments.Run, avant tout travail : ni dossier de sortie, ni appel au service IA.
    [Theory]
    [InlineData("--runs doit valoir au moins 2 : il faut deux passages pour mesurer une dérive", "stabilite", "--runs", "1")]
    [InlineData("--limit doit valoir au moins 1, pas 0", "stabilite", "--limit", "0")]
    [InlineData("max_chars doit valoir au moins 100", "cace-decoupage", "--max-chars", "50")]
    // 150 : la moitié des 300 caractères par défaut, la première valeur refusée.
    [InlineData("overlap_chars doit être compris entre 0 et max_chars / 2", "cace-decoupage", "--overlap-chars", "150")]
    [InlineData("prompt introuvable : nexiste dans ", "prompt-v2", "--other", "nexiste")]
    [InlineData("changement-embeddings attend --other <alias du second modèle>", "changement-embeddings")]
    public void An_experiment_option_is_checked_after_the_configuration_and_before_any_work(string message, params string[] options)
    {
        using var ai = new FakeAiService();
        var config = WriteConfig(ai.Url);
        var outDir = Path.Combine(_dir, "exp");
        var argv = options.Prepend("experience").Concat(new[] { "--questions", WriteQuestions(), "--out", outDir }).ToArray();
        var (code, output, errors) = Run(argv.Concat(new[] { "--config", _missing }).ToArray());
        Assert.Equal((1, ""), (code, output));
        Assert.Contains("absente.json", errors);
        (code, output, errors) = Run(argv.Concat(new[] { "--config", config }).ToArray());
        Assert.Equal((1, ""), (code, output));
        Assert.StartsWith($"Erreur : {message}", errors);
        Assert.False(Directory.Exists(outDir));
        Assert.Equal(0, ai.Requests);
    }

    // Une valeur qui n'est pas un entier est refusée au démarrage, pour toutes les commandes : avant la configuration,
    // même quand l'option est répétée (chaque valeur est lue, pas seulement la dernière).
    [Theory]
    [InlineData("l'option --seed attend un entier, pas « abc »\n", "benchmark", "--seed", "abc")]
    [InlineData("l'option --limit attend un entier, pas « abc »\n", "snapshot", "record", "x", "--limit", "abc")]
    [InlineData("l'option --limit attend un entier, pas « abc »\n", "snapshot", "record", "x", "--limit", "abc", "--limit", "2")]
    [InlineData("l'option --port attend un entier, pas « abc »\n", "serve", "--port", "abc")]
    [InlineData("l'option --runs attend un entier, pas « deux »\n", "experience", "stabilite", "--runs", "deux")]
    public void A_non_integer_value_is_refused_before_the_configuration_even_if_repeated(string message, params string[] argv) =>
        AssertRefused(message, argv);

    [Fact]
    public void Snapshot_record_refuses_a_limit_below_one_before_any_question()
    {
        // --limit : au moins 1, lu avec le jeu de questions, une fois la configuration lue (le banc :
        // Benchmark_refuses_a_limit_of_zero_like_the_experiments). Un nombre négatif est une valeur, pas une option.
        using var ai = new FakeAiService();
        var (code, output, errors) = Run("snapshot", "record", "x", "--limit", "-1", "--questions", WriteQuestions(), "--config", WriteConfig(ai.Url));
        Assert.Equal((1, ""), (code, output));
        Assert.EndsWith("Erreur : --limit doit valoir au moins 1, pas -1\n", errors);   // après l'avertissement du seuil `default`
        Assert.False(Directory.Exists(Path.Combine(_dir, "instantanes")));
        Assert.Equal(0, ai.Requests);
    }

    [Fact]
    public void An_option_without_its_value_is_refused() =>
        Assert.Contains("Erreur : l'option --user attend une valeur", Run("ask", Question, "--config", _missing, "--user").Err);

    [Theory]
    [InlineData("Erreur : snapshot attend record, compare ou list", "snapshot")]
    [InlineData("Erreur : experience attend un nom : cace-decoupage, changement-embeddings, changement-generateur, prompt-v2, stabilite",
                "experience")]
    [InlineData("Commande inconnue : snapshot list", "snapshot list")]   // un seul argument
    public void A_command_is_one_word_or_two_separate_words(string message, params string[] argv)
    {
        var (code, _, errors) = Run(argv);
        Assert.Equal(1, code);
        Assert.Contains(message, errors);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("ask", "-h")]
    [InlineData("--json", "status", "--help")]   // l'aide passe avant tout, où qu'elle soit
    [InlineData("status", "--jsn", "-h")]
    [InlineData("ask", "--us", "-h")]
    public void Help_comes_first_wherever_it_is(params string[] argv) => Assert.Equal(0, Run(argv).Code);

    [Theory]
    [InlineData("index", "--if-stale", "-v")]
    [InlineData("ask", Question, "--user", "alice", "--json", "--verbose")]
    [InlineData("status", "--json")]
    [InlineData("snapshot", "record", "x", "--questions", "q.json", "--limit", "2")]
    [InlineData("snapshot", "compare", "a", "b", "--prompt", "answer-v2")]
    [InlineData("snapshot", "list", "-v")]
    [InlineData("serve", "--host", "127.0.0.1", "--port", "1", "--quiet")]
    [InlineData("benchmark", "--embedding-model", "hashing-512", "--generation-model", "extractive", "--prompt", "answer-v2")]
    [InlineData("experience", "cace-decoupage", "--max-chars", "300", "--overlap-chars", "50", "--questions", "q.json")]
    [InlineData("experience", "changement-embeddings", "--other", "nomic", "--limit", "2")]
    [InlineData("experience", "prompt-v2", "--other", "answer-v2", "-v")]
    [InlineData("experience", "stabilite", "--runs", "3", "--seed", "42", "--out", "ici")]
    // Une question qui commence par un tiret (elle contient une espace), sans « -- » ; une valeur avec espace.
    [InlineData("ask", "-5 jours ?")]
    [InlineData("ask", "-vingt degrés ?")]
    [InlineData("ask", "-v jours")]
    [InlineData("ask", "- Combien de jours ?", "--user=bruno martin")]
    // Les bornes : le port 65535, et --limit 1.
    [InlineData("serve", "--port", "65535")]
    [InlineData("snapshot", "record", "x", "--limit", "1")]
    [InlineData("benchmark", "--limit", "1")]
    [InlineData("experience", "stabilite", "--limit", "1")]
    public void The_options_of_each_command_reach_the_configuration(params string[] argv)
    {
        var (code, _, errors) = Run(argv.Append("--config").Append(_missing).ToArray());
        Assert.Equal(1, code);
        Assert.Contains("absente.json", errors);   // la saisie est acceptée : c'est le fichier qui manque
        Assert.DoesNotContain("inconnue", errors);
    }

    [Fact]
    public void Every_experiment_has_its_own_options()
    {
        foreach (var name in Experiments.Names)
        {
            Assert.Contains($"option(s) inconnue(s) pour experience {name} : --nope", Run("experience", name, "--nope", "--config", _missing).Err);
        }
    }

    [Fact]
    public void The_threshold_warning_is_for_the_commands_that_search_with_the_model_they_use()
    {
        using var ai = new FakeAiService();
        var config = WriteConfig(ai.Url);
        foreach (var argv in new[] { new[] { "index" }, new[] { "status" }, new[] { "snapshot", "list" } })
        {
            var run = Run(argv.Append("--config").Append(config).ToArray());
            Assert.Equal((0, ""), (run.Code, run.Err));
        }
        var (code, _, errors) = Run("ask", Question, "--user", "mallory", "--config", config);
        Assert.Equal(1, code);
        Assert.Contains(Warning, errors);
        // Le modèle réellement utilisé a son seuil : pas d'avertissement.
        Assert.DoesNotContain("Attention", Run("ask", Question, "--user", "mallory", "--embedding-model", "hashing-512", "--config", config).Err);
        Assert.Contains(Warning, Run("snapshot", "record", "x", "--questions", Path.Combine(_dir, "absentes.json"), "--config", config).Err);
        Assert.DoesNotContain("Attention", Run("snapshot", "compare", "a", "b", "--config", config).Err);
        // Le banc et les expériences mesurent leurs propres modèles (le banc nomme le seuil de chacun).
        Assert.DoesNotContain("Attention", Run("benchmark", "--embedding", "hashing-512", "--min-score", "abc", "--config", config).Err);
        Assert.DoesNotContain("Attention", Run("experience", "stabilite", "--runs", "1", "--config", config).Err);
    }

    [Fact]
    public void The_snapshot_commands_print_the_folder_with_slashes_and_the_configuration_sorted()
    {
        // Un seul document, réservé aux RH : alice n'y a pas accès, d'où une réponse sans génération.
        var corpus = Directory.CreateDirectory(Path.Combine(_dir, "corpus")).FullName;
        File.WriteAllText(Path.Combine(corpus, "paie.md"), "---\nid: paie\ntitre: Paie\ngroupes: rh\n---\n# Paie\n\nLe salaire est versé le 28.\n");
        using var ai = new FakeAiService();
        var config = WriteConfig(ai.Url, corpus);
        var directory = Path.Combine(_dir, "instantanes").Replace('\\', '/');   // des « / » : la même sortie sous Windows et Linux
        Assert.Equal($"Aucun instantané dans {directory}\n", Run("snapshot", "list", "--config", config).Out);
        Assert.Equal(0, Run("index", "--config", config).Code);
        var questions = Path.Combine(_dir, "questions.json");
        File.WriteAllText(questions, """{"questions": [{"id": "q1", "user": "alice", "question": "Quand le salaire est-il versé ?"}]}""");
        var (code, output, errors) = Run("snapshot", "record", "ref", "--questions", questions, "--config", config);
        Assert.True(code == 0, errors);
        var lines = output.TrimEnd('\n').Split('\n');
        Assert.Equal($"Instantané « ref » : 1 réponses, enregistré dans {directory}", lines[0]);
        var keys = lines.Skip(1).Select(line => line.Split(" = ")[0].Trim()).ToList();
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);   // clés triées
        Assert.Contains("  seed = null", lines);
        Assert.Contains("  validate_output = true", lines);
        Assert.Contains("  splitter = {include_title=true, max_chars=800, overlap_chars=120, type=paragraph}", lines);
    }

    [Fact]
    public void An_experiment_names_its_files_as_given()
    {
        // L'en-tête du rapport : --config et --questions tels qu'ils ont été donnés, avec des « / » ; la configuration
        // s'écrivait depuis la racine du projet. Puis la console : progression, « Rapport : » et son chemin. 15 morceaux :
        // le corpus Solvéo en 800 / 120 ; la dimension du faux service, un axe par mot du vocabulaire plus l'axe constant
        // (FakeAiService).
        using var ai = new FakeAiService();
        var config = WriteConfig(ai.Url);
        var questions = WriteQuestions();
        var outDir = Path.Combine(_dir, "exp");
        var (code, output, errors) = Run("experience", "stabilite", "--runs", "2", "--questions", questions, "--out", outDir, "--config", config);
        Assert.True(code == 0, errors);
        Assert.Contains($"\nConfiguration `{config.Replace('\\', '/')}` · 1 questions de `{questions.Replace('\\', '/')}` · corpus `solveo`.\n",
                        File.ReadAllText(Path.Combine(outDir, "rapport.md")));
        Assert.Contains($"  index « partage » : 15 morceaux, faux:hashing, {Fakes.Vocabulary.Length + 1} dim., ", output);
        Assert.Contains("  instantané « passage-1 » : 1 réponses en ", output);
        Assert.Contains("  instantané « passage-2 » : 1 réponses en ", output);
        Assert.EndsWith($"\nRapport : {Path.Combine(outDir, "rapport.md").Replace('\\', '/')}\n", output);
    }

    [Fact]
    public void Without_config_an_experiment_names_the_configuration_from_the_project_root()
    {
        // Sans --config : la configuration du projet, nommée depuis la racine (« config/app.json »), et non par son
        // chemin absolu. Le faux service IA remplace l'adresse (AI_SERVICE_URL), et ASSISTANT_CONFIG est retirée le
        // temps de la commande. Ces variables valent pour tout le processus : cette collection ne tourne en même temps
        // qu'aucune autre.
        using var ai = new FakeAiService();
        var questions = WriteQuestions();
        var outDir = Path.Combine(_dir, "exp");
        var (previousUrl, previousConfig) = (Environment.GetEnvironmentVariable("AI_SERVICE_URL"),
                                             Environment.GetEnvironmentVariable("ASSISTANT_CONFIG"));
        Environment.SetEnvironmentVariable("AI_SERVICE_URL", ai.Url);
        Environment.SetEnvironmentVariable("ASSISTANT_CONFIG", null);
        try
        {
            var (code, _, errors) = Run("experience", "stabilite", "--runs", "2", "--questions", questions, "--out", outDir);
            Assert.True(code == 0, errors);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AI_SERVICE_URL", previousUrl);
            Environment.SetEnvironmentVariable("ASSISTANT_CONFIG", previousConfig);
        }
        Assert.Contains($"\nConfiguration `config/app.json` · 1 questions de `{questions.Replace('\\', '/')}` · corpus `solveo`.\n",
                        File.ReadAllText(Path.Combine(outDir, "rapport.md")));
    }

    [Fact]
    public void Without_config_an_experiment_reads_assistant_config()
    {
        // Sans --config, ASSISTANT_CONFIG désigne la configuration, comme pour les autres commandes ; l'en-tête la nomme
        // depuis la racine du projet. AI_SERVICE_URL est retirée le temps de la commande : l'adresse est celle du
        // fichier. Variables du processus entier : voir le test précédent.
        using var ai = new FakeAiService();
        var config = WriteConfig(ai.Url);
        var questions = WriteQuestions();
        var outDir = Path.Combine(_dir, "exp");
        var (previousUrl, previousConfig) = (Environment.GetEnvironmentVariable("AI_SERVICE_URL"),
                                             Environment.GetEnvironmentVariable("ASSISTANT_CONFIG"));
        Environment.SetEnvironmentVariable("AI_SERVICE_URL", null);
        Environment.SetEnvironmentVariable("ASSISTANT_CONFIG", config);
        try
        {
            var (code, _, errors) = Run("experience", "stabilite", "--runs", "2", "--questions", questions, "--out", outDir);
            Assert.True(code == 0, errors);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AI_SERVICE_URL", previousUrl);
            Environment.SetEnvironmentVariable("ASSISTANT_CONFIG", previousConfig);
        }
        var shown = Path.GetRelativePath(AppConfig.ProjectRoot, config).Replace('\\', '/');
        Assert.Contains($"\nConfiguration `{shown}` · 1 questions de `{questions.Replace('\\', '/')}` · corpus `solveo`.\n",
                        File.ReadAllText(Path.Combine(outDir, "rapport.md")));
    }

    [Fact]
    public void An_unknown_prompt_names_its_folder_and_the_known_ones()
    {
        // « prompt introuvable : … », et non plus « fichier illisible … — Could not find file ».
        using var ai = new FakeAiService();
        var (code, output, errors) = Run("status", "--prompt", "nexiste", "--config", WriteConfig(ai.Url));
        Assert.Equal((1, ""), (code, output));
        Assert.Equal($"Erreur : prompt introuvable : nexiste dans {Composition.PromptsDir} (connus : answer, answer-v2)\n", errors);
    }

    [Fact]
    public void An_empty_value_is_no_value()
    {
        // --config, --embedding-model, --generation-model ou --prompt donnés vides valent la configuration, comme sans
        // l'option. Ils étaient pris pour un nom : « prompt introuvable :  dans … », un modèle « » (le faux service sert
        // « faux: »), ou, pour --config, un message anglais (« The value cannot be an empty string »). ASSISTANT_CONFIG :
        // variable du processus entier, comme plus haut.
        using var ai = new FakeAiService();
        var config = WriteConfig(ai.Url);
        const string question = "Combien de jours de télétravail par semaine ?";
        Assert.Equal(0, Run("index", "--config", config).Code);
        var (status, ask) = (Run("status", "--config", config), Run("ask", question, "--config", config));
        Assert.Equal((0, 0), (status.Code, ask.Code));
        foreach (var option in new[] { "--embedding-model", "--generation-model", "--prompt" })
        {
            Assert.Equal(status, Run("status", option, "", "--config", config));
            Assert.Equal(ask, Run("ask", question, option, "", "--config", config));
        }
        var previousConfig = Environment.GetEnvironmentVariable("ASSISTANT_CONFIG");
        Environment.SetEnvironmentVariable("ASSISTANT_CONFIG", config);
        try
        {
            Assert.Equal(status, Run("status", "--config", ""));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASSISTANT_CONFIG", previousConfig);
        }
    }

    [Fact]
    public void An_empty_questions_or_validation_file_is_no_value()
    {
        // La même règle pour les jeux de questions : --questions "" vaut le jeu par défaut (eval/questions.json, depuis la
        // racine du projet), --validate-with "" l'absence de validation. Ils étaient pris pour un chemin, avec un message
        // anglais (« The value cannot be an empty string »).
        using var ai = new FakeAiService();
        var config = WriteConfig(ai.Url);
        Assert.Equal(0, Run("index", "--config", config).Code);
        var (code, output, errors) = Run("snapshot", "record", "vide", "--questions", "", "--limit", "1", "--config", config);
        Assert.True(code == 0, errors);
        Assert.StartsWith("Instantané « vide » : 1 réponses", output);
        var outDir = Path.Combine(_dir, "banc");
        (code, output, errors) = Run("benchmark", "--runs", "1", "--questions", WriteQuestions(), "--validate-with", "", "--out", outDir,
                                     "--config", config);
        Assert.True(code == 0, errors);
        Assert.EndsWith($"\nRapport : {Path.Combine(outDir, "rapport.md").Replace('\\', '/')}\n", output);
    }

    [Fact]
    public void An_unreachable_ai_service_stops_the_benchmark_and_the_experiments_before_any_folder()
    {
        // Banc et expériences : « Erreur : », le message « injoignable », code 1, avant tout dossier.
        var config = WriteConfig("http://127.0.0.1:1");
        var questions = Path.Combine(_dir, "questions.json");
        File.WriteAllText(questions, """{"questions": [{"id": "a", "user": "alice", "question": "Combien ?"}]}""");
        foreach (var argv in new[] { new[] { "benchmark" }, new[] { "experience", "stabilite" } })
        {
            var outDir = Path.Combine(_dir, "sortie");
            var (code, output, errors) = Run(argv.Concat(new[] { "--questions", questions, "--out", outDir, "--config", config }).ToArray());
            Assert.Equal((1, ""), (code, output));
            Assert.Equal("Erreur : service IA injoignable (http://127.0.0.1:1). Lancez-le d'abord : python -m ai_service\n", errors);
            Assert.False(Directory.Exists(outDir));
        }
    }

    [Fact]
    public void Snapshot_record_takes_the_limit_before_checking_the_users()
    {
        var config = WriteConfig("http://127.0.0.1:1");
        // La seconde question nomme un utilisateur inconnu, mais --limit 1 l'écarte :
        // l'erreur est l'index absent, pas « utilisateur inconnu : claire ».
        var questions = Path.Combine(_dir, "questions.json");
        File.WriteAllText(questions, """
            {"questions": [{"id": "a", "user": "alice", "question": "Combien de jours ?"},
                           {"id": "c", "user": "claire", "question": "Quel budget ?"}]}
            """);
        var (code, _, errors) = Run("snapshot", "record", "x", "--questions", questions, "--limit", "1", "--config", config);
        Assert.Equal(1, code);
        Assert.Contains("Aucun index", errors);
        Assert.DoesNotContain("claire", errors);
    }

    [Fact]
    public void Benchmark_refuses_a_limit_of_zero_like_the_experiments()
    {
        // Le banc, comme les expériences et snapshot record, refuse --limit 0 avant tout travail : ni service IA
        // interrogé, ni dossier de résultats.
        var config = WriteConfig("http://127.0.0.1:1");
        var outDir = Path.Combine(_dir, "banc");
        var (code, output, errors) = Run("benchmark", "--limit", "0", "--out", outDir, "--config", config);
        Assert.Equal((1, ""), (code, output));
        Assert.Contains("Erreur : --limit doit valoir au moins 1, pas 0", errors);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void With_out_the_benchmark_writes_in_the_folder_given_even_if_it_exists()
    {
        // Seul le dossier par défaut, daté à la seconde, n'est jamais un dossier qui existe déjà (« -2 », « -3 »…) :
        // --out reste le dossier donné, même s'il existe.
        using var ai = new FakeAiService();
        var config = WriteConfig(ai.Url);
        var outDir = Directory.CreateDirectory(Path.Combine(_dir, "banc")).FullName;
        var (code, output, errors) = Run("benchmark", "--runs", "1", "--questions", WriteQuestions(), "--out", outDir, "--config", config);
        Assert.True(code == 0, errors);
        Assert.EndsWith($"\nRapport : {Path.Combine(outDir, "rapport.md").Replace('\\', '/')}\n", output);
        Assert.Equal(new[] { "index-hashing.json", "rapport.md", "resultats.csv", "synthese.json" },
                     Directory.GetFileSystemEntries(outDir).Select(f => Path.GetFileName(f)).Order());
        Assert.False(Path.Exists(outDir + "-2"));
    }

    [Fact]
    public void Without_out_the_benchmark_and_the_experiments_never_write_in_a_folder_that_exists()
    {
        // Sans --out, le dossier de sortie est daté à la seconde, sous eval/resultats de la racine du projet : une commande
        // lancée dans la même seconde qu'une autre écrivait dans son dossier. Elle prend le suivant, « -2 » (NewOutDir pour
        // le banc, newOutDir pour les expériences), et laisse l'autre intact. Lancée depuis une copie du projet
        // (CopyProject, Launch), elle écrit sous le dossier du test, et non dans ce dépôt, dont BenchmarkTests lit les
        // résultats de référence. Les dossiers des 60 secondes à venir y existent déjà, comme ceux d'une commande lancée
        // juste avant.
        using var ai = new FakeAiService();
        var root = CopyProject(ai.Url);
        var questions = WriteQuestions();
        foreach (var (command, prefix) in new[] { (new[] { "benchmark", "--runs", "1" }, ""),
                                                  (new[] { "experience", "stabilite", "--runs", "2" }, "exp-stabilite-") })
        {
            var now = DateTime.Now;
            var taken = Enumerable.Range(0, 60)   // le dossier que la commande prendrait à chacune de ces secondes
                                  .Select(s => Path.Combine(root, AppConfig.ResultsDir(prefix, now.AddSeconds(s), from: AppConfig.ProjectRoot)))
                                  .ToList();
            taken.ForEach(folder => Directory.CreateDirectory(folder));
            var (code, output, errors) = Launch(root, command.Concat(new[] { "--questions", questions, "--config", Path.Combine(root, "config", "app.json") }).ToArray());
            Assert.True(code == 0, errors);
            Assert.All(taken, folder => Assert.Empty(Directory.GetFileSystemEntries(folder)));
            var second = Assert.Single(taken, folder => Directory.Exists(folder + "-2")) + "-2";
            Assert.EndsWith($"\nRapport : {Path.GetRelativePath(root, second).Replace('\\', '/')}/rapport.md\n", output);
        }
    }

    [Fact]
    public void An_empty_out_is_the_dated_folder()
    {
        // --out "" vaut l'option absente : le dossier daté, sous eval/resultats, et jamais un dossier qui existe déjà, comme
        // sans --out (test précédent). Pris pour un chemin, il arrêtait la commande sur un message .NET en anglais (« The
        // value cannot be an empty string »).
        using var ai = new FakeAiService();
        var root = CopyProject(ai.Url);
        var questions = WriteQuestions();
        foreach (var (command, prefix) in new[] { (new[] { "benchmark", "--runs", "1" }, ""),
                                                  (new[] { "experience", "stabilite", "--runs", "2" }, "exp-stabilite-") })
        {
            var now = DateTime.Now;
            var taken = Enumerable.Range(0, 60)   // le dossier que la commande prendrait à chacune de ces secondes
                                  .Select(s => Path.Combine(root, AppConfig.ResultsDir(prefix, now.AddSeconds(s), from: AppConfig.ProjectRoot)))
                                  .ToList();
            taken.ForEach(folder => Directory.CreateDirectory(folder));
            var (code, output, errors) = Launch(root, command.Concat(new[] { "--out", "", "--questions", questions, "--config", Path.Combine(root, "config", "app.json") }).ToArray());
            Assert.True(code == 0, errors);
            Assert.All(taken, folder => Assert.Empty(Directory.GetFileSystemEntries(folder)));
            var second = Assert.Single(taken, folder => Directory.Exists(folder + "-2")) + "-2";
            Assert.EndsWith($"\nRapport : {Path.GetRelativePath(root, second).Replace('\\', '/')}/rapport.md\n", output);
        }
    }

    // Service IA injoignable : une option fausse est dite quand même, avant de l'interroger, et sans dossier de
    // résultats. Le message entier, jusqu'à la fin de ligne : sans « (Parameter 'maxChars') », que .NET ajoutait.
    [Theory]
    [InlineData("Erreur : --runs doit valoir au moins 1, pas 0\n", "--runs", "0")]
    [InlineData("Erreur : --min-score attend config, auto ou un nombre de [-1, 1] (ex. 0.6), pas « abc »\n", "--min-score", "abc")]
    [InlineData("Erreur : max_chars doit valoir au moins 100\n", "--max-chars", "50")]
    [InlineData("Erreur : overlap_chars doit être compris entre 0 et max_chars / 2\n", "--overlap-chars", "900")]
    [InlineData("nexiste", "--prompt", "nexiste")]
    public void Benchmark_checks_every_option_before_the_ai_service(string message, params string[] options)
    {
        var config = WriteConfig("http://127.0.0.1:1");
        var questions = Path.Combine(_dir, "questions.json");
        File.WriteAllText(questions, """{"questions": [{"id": "a", "user": "alice", "question": "Combien ?"}]}""");
        var outDir = Path.Combine(_dir, "banc");
        var (code, output, errors) = Run(options.Prepend("benchmark").Concat(new[] { "--questions", questions, "--out", outDir, "--config", config }).ToArray());
        Assert.Equal((1, ""), (code, output));
        Assert.Contains(message, errors);
        Assert.DoesNotContain("injoignable", errors);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void The_help_of_min_score_names_its_bounds() =>
        Assert.Contains("--min-score config|auto|<n de [-1, 1]>", Run("--help").Out);

    [Fact]
    public void The_help_says_how_to_end_the_options_and_bounds_limit()
    {
        var help = Run("--help").Out;
        // Les exemples : A_double_dash_ends_the_options (« -télétravail ») et The_options_of_each_command_reach_the_configuration.
        Assert.Contains("« -- » termine les options : ce qui suit est un argument, même un mot qui commence par un tiret "
                        + "(ask -- -télétravail) ; sans « -- », un tel mot n'est un argument que s'il est « - », un nombre négatif, ou si son "
                        + "nom (ce qui précède un éventuel « = ») contient une espace (ask \"-vingt degrés ?\").", help);
        Assert.Contains("--limit N, au moins 1", help);
    }

    private Container BuildContainer() => Composition.Build(AppConfig.Load() with { AiBaseUrl = "http://127.0.0.1:1" },
                                                       new Overrides(IndexPath: Path.Combine(_dir, "index.json")));

    [Theory]
    [InlineData("localhost", "http://localhost:8123", false)]
    [InlineData("0.0.0.0", "http://127.0.0.1:8123", true)]
    [InlineData("*", "http://127.0.0.1:8123", true)]
    [InlineData("+", "http://127.0.0.1:8123", true)]
    public void The_api_announces_an_address_to_connect_to(string host, string url, bool allInterfaces)
    {
        // Une adresse où se connecter (Serve annonce Url) : l'hôte demandé, sauf pour toutes les interfaces (0.0.0.0,
        // « * », « + »), où l'on ne se connecte pas : 127.0.0.1, et Serve le dit. Sans Start : on n'écoute pas sur
        // toutes les interfaces.
        using var api = new HttpApi(BuildContainer(), host, 8123, quiet: true);
        Assert.Equal((url, allInterfaces), (api.Url, api.AllInterfaces));
    }

    [Fact]
    public async Task An_unknown_user_names_the_known_ones_without_brackets()
    {
        // Même texte en ligne de commande et dans le 403 de l'API HTTP.
        const string expected = "utilisateur inconnu : mallory (connus : alice, bruno, claire)";
        Assert.Equal(expected, Assert.Throws<UnknownUserException>(() => AppConfig.Load().User("mallory")).Message);
        using var api = TestPorts.StartOnAFreePort(
            port => new HttpApi(BuildContainer(), "127.0.0.1", port, quiet: true), server => server.Start());
        using var http = new HttpClient();
        using var response = await http.PostAsync(api.Url + "/v1/ask",
            new StringContent("""{"user": "mallory", "question": "?"}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!;
        Assert.Equal(("unknown_user", expected), (error["code"]!.GetValue<string>(), error["message"]!.GetValue<string>()));
    }

    [Fact]
    public void Serve_announces_where_it_listens_and_when_it_stops()
    {
        using var stopped = new ManualResetEvent(true);   // Ctrl+C déjà reçu : Serve rend la main aussitôt
        var (previous, output) = (Console.Out, new StringWriter());
        var port = 0;
        Console.SetOut(output);
        try
        {
            // Serve démarre l'API lui-même, sur un port libre (TestPorts : un autre essai si ce port est pris
            // entre-temps).
            using var api = TestPorts.StartOnAFreePort(
                free =>
                {
                    port = free;
                    return new HttpApi(BuildContainer(), "127.0.0.1", free, quiet: true);
                },
                server => Assert.Equal(0, Program.Serve(server, "http://ia:8100", stopped)));
        }
        finally
        {
            Console.SetOut(previous);
        }
        Assert.Equal($"Application sur http://127.0.0.1:{port} (service IA : http://ia:8100) — Ctrl+C pour arrêter\n\nArrêt de l'application.\n",
                     output.ToString().Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Serve_on_a_taken_port_says_so()
    {
        var taken = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        try
        {
            var port = ((IPEndPoint)taken.LocalEndpoint).Port;
            var config = WriteConfig("http://127.0.0.1:1");
            // Sur un port libre, serve attendrait Ctrl+C : le délai fait échouer le test au lieu de le bloquer.
            var run = Task.Run(() => Run("serve", "--quiet", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--config", config));
            Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30))));
            var (code, output, errors) = await run;
            Assert.Equal((1, ""), (code, output));
            Assert.Contains("Erreur : impossible d'écouter (port déjà pris, adresse inconnue ou non autorisée) — ", errors);
        }
        finally
        {
            taken.Stop();
        }
    }
}

/// <summary>Des affichages qui se lisent : null, true, [a, b], « 33 % », clés du découpage triées.</summary>
public sealed class ReadableDisplayTests
{
    private static readonly Dictionary<string, object> Splitter = new()
    {
        ["type"] = "paragraph", ["max_chars"] = 800, ["overlap_chars"] = 120, ["include_title"] = true,
    };

    private static SnapshotComparison Comparison(int changed, int compared,
                                                 Dictionary<string, object?>? before = null, Dictionary<string, object?>? after = null)
    {
        Snapshot Make(string name, Dictionary<string, object?>? configuration, string text) => new(name, "2026-09-11",
            configuration ?? new Dictionary<string, object?>(),
            Enumerable.Range(0, compared).Select(i => new SnapshotEntry($"q{i}", "alice", "?", "answered", new[] { "teletravail", "conges" },
                                                                         i < changed ? text : "même")).ToList());
        return SnapshotComparer.Compare(Make("a", before, "avant"), Make("b", after, "après"));
    }

    [Fact]
    public void The_drift_rate_lists_and_configuration_differences()
    {
        var text = Presenter.ComparisonToText(Comparison(1, 3,
            new Dictionary<string, object?> { ["seed"] = null, ["validate_output"] = true, ["splitter"] = Splitter },
            new Dictionary<string, object?> { ["seed"] = 42, ["validate_output"] = false, ["splitter"] = new Dictionary<string, object>(Splitter) { ["max_chars"] = 300 } }))
            .Replace("\r\n", "\n");
        Assert.Contains("  taux de dérive      : 33 %", text);
        Assert.Contains("  avant : answered [teletravail, conges] « avant »", text);
        Assert.Contains("  - seed : null → 42", text);
        Assert.Contains("  - validate_output : true → false", text);
        Assert.Contains("  - splitter : {include_title=true, max_chars=800, overlap_chars=120, type=paragraph} → "
                        + "{include_title=true, max_chars=300, overlap_chars=120, type=paragraph}", text);
    }

    [Fact]
    public void The_whole_comparison_line_by_line()
    {
        // Le texte entier : les lignes d'un rapport d'expérience ; la console les joint avec les fins de ligne du système.
        var comparison = Comparison(1, 2, new Dictionary<string, object?> { ["seed"] = null }, new Dictionary<string, object?> { ["seed"] = 42 });
        var expected = new[]
        {
            "Comparaison : a → b", "", "Différences de configuration", "  - seed : null → 42", "",
            "Dérive", "  questions comparées : 2", "  réponses modifiées  : 1", "  taux de dérive      : 50 %", "",
            "| Nature | Nombre | Lecture |", "|---|---|---|",
            "| statut modifié | 0 | changement de comportement : refus devenu réponse, ou l'inverse |",
            "| sources modifiées | 0 | même décision, autres documents cités |",
            "| texte modifié | 1 | mêmes sources, même statut, texte différent : à relire, le sens a pu changer (Oui devenu Non…) |",
            "| identique | 1 | rien n'a bougé |",
            "| absente d'un des deux | 0 | question présente d'un seul côté |",
            "", "q0 [texte modifié]", "  avant : answered [teletravail, conges] « avant »",
            "  après : answered [teletravail, conges] « après »",
        };
        Assert.Equal(expected, Presenter.ComparisonLines(comparison));
        Assert.Equal(string.Join(Environment.NewLine, expected), Presenter.ComparisonToText(comparison));
    }

    [Fact]
    public void Answers_are_cut_at_90_code_points()
    {
        // « avant » et « après » : les 90 premiers caractères (points de code), et non 90 unités UTF-16. Un emoji avant
        // la coupure compte pour un et reste entier (coupé en deux, sa moitié s'écrivait « � ») ; une moitié de paire
        // isolée compte pour un et reste telle quelle.
        const string emoji = "\U0001F600";
        var cases = new (string Text, string Head)[]
        {
            (new string('a', 89) + emoji + new string('b', 10), new string('a', 89) + emoji),
            (emoji + new string('a', 88) + "\ud800" + "b", emoji + new string('a', 88) + "\ud800"),
            ("court", "court"),
        };
        static Snapshot Make(string name, string text) => new(name, "2026-09-11", new Dictionary<string, object?>(),
            new[] { new SnapshotEntry("q0", "alice", "?", "answered", new[] { "d" }, text, 1) });
        foreach (var (text, head) in cases)
        {
            var lines = Presenter.ComparisonLines(SnapshotComparer.Compare(Make("a", text), Make("b", text + "c")));
            Assert.Contains($"  avant : answered [d] « {head} »", lines);
        }
    }

    [Fact]
    public void The_status_and_the_manifest_sort_the_splitter_keys()
    {
        var manifest = new IndexManifest("d5276d0355c9", "hashing-256-stem6", 256, new string('0', 64), Splitter, 9, 15, "2026-09-11");
        var report = new StatusReport(manifest, 9, new string('0', 64), Splitter, "hashing-256-stem6", 256, null, "v1", Array.Empty<string>());
        const string described = "{include_title: true, max_chars: 800, overlap_chars: 120, type: paragraph}";
        Assert.Contains($"· découpage {described}", Presenter.StatusToText(report));
        Assert.Contains($"Découpage  : {described}", Presenter.StatusToText(report));
        var sorted = new[] { "include_title", "max_chars", "overlap_chars", "type" };
        Assert.Equal(sorted, Presenter.ManifestToJson(manifest)["splitter"]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(sorted, Presenter.StatusToJson(report)["splitter"]!.AsObject().Select(kv => kv.Key));
    }

    [Fact]
    public void The_configuration_values()
    {
        // null, true et false (plutôt que rien, True et False), un réel avec un point, listes et dictionnaires sans guillemets.
        Assert.Equal(new[] { "null", "true", "false", "0.15", "4", "hashing" },
                     new object?[] { null, true, false, 0.15, 4, "hashing" }.Select(SnapshotComparer.Canonical));
        Assert.Equal("{include_title=true, max_chars=800, overlap_chars=120, type=paragraph}", SnapshotComparer.Canonical(Splitter));
        Assert.Equal("[a, null, true, 0.5]", SnapshotComparer.Canonical(new object?[] { "a", null, true, 0.5 }));
    }

    [Fact]
    public void The_verbose_answer()
    {
        // Chaque sortie brute en chaîne JSON : entre guillemets, sur une ligne (sauts de ligne échappés) ; relue, elle
        // redonne la sortie telle quelle, espaces insécables, emoji et caractères de contrôle compris.
        const string raw = "Deux\u00a0jours\u202f! \U0001F600\u2028\u001b\u007f \"q\" \\ \n\t";
        var trace = new AnswerTrace("d5276d0355c9", "hashing-256-stem6", "extractive", "v1",
                                    new[] { new Retrieved("teletravail#0", 0.5276), new Retrieved("conges#0", 0.0) }, 2.0, 1,
                                    new[] { "Deux jours.\n[1]", raw });
        var text = Presenter.AnswerToText(new Answer("?", AnswerStatus.Answered, "Deux jours. [1]", Array.Empty<Source>(), trace), verbose: true)
            .Replace("\r\n", "\n");
        Assert.Contains("seuil=2\n", text);
        Assert.Contains("  retrouvé conges#0 score=0\n", text);
        Assert.Contains("  retrouvé teletravail#0 score=0.5276", text);
        Assert.Contains("  sortie brute 1 : \"Deux jours.\\n[1]\"\n", text);
        const string prefix = "  sortie brute 2 : ";
        var last = text[(text.LastIndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..];
        Assert.DoesNotContain('\n', last);   // une sortie par ligne
        Assert.Equal(raw, JsonNode.Parse(last)!.GetValue<string>());   // une chaîne JSON, qui redonne la sortie
    }

    [Fact]
    public void The_json_of_an_answer_is_indented_and_reads_back_the_same()
    {
        // ask --json : indenté, accents tels quels ; relu, les mêmes valeurs, texte compris (espace insécable, guillemets,
        // saut de ligne, emoji).
        var trace = new AnswerTrace("id", "hashing", "extractive", "v1+abc", new[] { new Retrieved("a#0", 1.0), new Retrieved("a#1", 0.25) },
                                    0.4, 1, Array.Empty<string>());
        var answer = new Answer("Combien ?", AnswerStatus.Answered, Bytes.Special, new[] { new Source(1, "a", "Congés", "a#0") }, trace);
        var written = Presenter.ToJson(Presenter.AnswerToJson(answer));
        Assert.StartsWith("{" + Environment.NewLine + "  \"question\": \"Combien ?\",", written);
        Assert.Contains("\"document_title\": \"Congés\"", written);
        Assert.True(JsonNode.DeepEquals(Presenter.AnswerToJson(answer), JsonNode.Parse(written)));
    }
}
