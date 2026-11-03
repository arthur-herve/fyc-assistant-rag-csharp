// La commande experience : les options de chaque expérience, vérifiées avant tout travail, l'en-tête du rapport et
// le dossier de sortie.

using System.Diagnostics;
using System.Text;
using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

[Collection("Console")]
public sealed class CliExperimentTests : CliTestBase
{
    [Theory]
    [InlineData("option(s) inconnue(s) pour experience stabilite : --user", "experience", "stabilite", "--user", "bruno")]
    public void A_mistyped_abbreviated_or_foreign_option_is_refused_before_any_work(string message, params string[] argv) =>
        AssertRefused(message, argv);

    [Theory]
    [InlineData("--runs", "experience", "--runs", "2", "stabilite")]
    public void The_command_comes_first(string option, params string[] argv) =>
        AssertRefused($"option placée avant la commande : {option} (la commande vient d'abord : assistant <commande> [options])", argv);

    [Theory]
    [InlineData("argument(s) en trop pour experience stabilite : encore", "experience", "stabilite", "encore")]
    public void An_extra_argument_is_refused(string message, params string[] argv) => AssertRefused(message, argv);

    [Theory]
    [InlineData("expérience inconnue : nope (connues : stabilite)", "experience", "nope")]
    public void A_wrong_value_or_an_unknown_subcommand_is_refused_before_any_work(string message, params string[] argv) =>
        AssertRefused(message, argv);

    // Pour une option d'expérience fausse, la configuration est lue d'abord : si elle manque, c'est elle que l'erreur
    // nomme. L'option est ensuite refusée par Experiments.Run avant tout travail, sans dossier de sortie ni appel au
    // service IA.
    [Theory]
    [InlineData("--runs doit valoir au moins 2 : il faut deux passages pour mesurer une dérive", "stabilite", "--runs", "1")]
    [InlineData("--limit doit valoir au moins 1, pas 0", "stabilite", "--limit", "0")]
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

    // Une valeur qui n'est pas un entier est refusée au démarrage, avant même de lire la configuration.
    [Theory]
    [InlineData("l'option --runs attend un entier, pas « deux »\n", "experience", "stabilite", "--runs", "deux")]
    public void A_non_integer_value_is_refused_before_the_configuration_even_if_repeated(string message, params string[] argv) =>
        AssertRefused(message, argv);

    [Theory]
    [InlineData("Erreur : experience attend un nom : stabilite", "experience")]
    public void A_command_is_one_word_or_two_separate_words(string message, params string[] argv)
    {
        var (code, _, errors) = Run(argv);
        Assert.Equal(1, code);
        Assert.Contains(message, errors);
    }

    [Theory]
    [InlineData("experience", "stabilite", "--runs", "3", "--seed", "42", "--out", "ici")]
    // La borne : --limit 1.
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
    public void An_experiment_names_its_files_as_given()
    {
        // L'en-tête du rapport cite --config et --questions tels qu'ils ont été donnés, avec des « / » : le chemin
        // de la configuration n'est pas réécrit depuis la racine du projet. La console donne ensuite « Rapport : » et
        // son chemin.
        using var ai = new FakeAiService();
        var config = WriteConfig(ai.Url);
        var questions = WriteQuestions();
        var outDir = Path.Combine(_dir, "exp");
        var (code, output, errors) = Run("experience", "stabilite", "--runs", "2", "--questions", questions, "--out", outDir, "--config", config);
        Assert.True(code == 0, errors);
        Assert.Contains($"\nConfiguration `{config.Replace('\\', '/')}` · 1 questions de `{questions.Replace('\\', '/')}` · corpus `solveo`.\n",
                        File.ReadAllText(Path.Combine(outDir, "rapport.md")));
        Assert.EndsWith($"\nRapport : {Path.Combine(outDir, "rapport.md").Replace('\\', '/')}\n", output);
    }

    [Fact]
    public void Without_config_an_experiment_names_the_configuration_from_the_project_root()
    {
        // Sans --config, l'en-tête nomme la configuration du projet depuis la racine (« config/app.json ») et non par
        // son chemin absolu. AI_SERVICE_URL pointe vers le faux service IA, et ASSISTANT_CONFIG est retirée le temps de
        // la commande. Ces variables valent pour tout le processus : c'est pourquoi cette collection ne tourne en même
        // temps qu'aucune autre.
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
        // Sans --config, c'est ASSISTANT_CONFIG qui désigne la configuration, comme pour les autres commandes, et
        // l'en-tête la nomme depuis la racine du projet. AI_SERVICE_URL est retirée le temps de la commande, pour que
        // l'adresse vienne du fichier. Ces variables valent pour tout le processus, comme dans le test précédent.
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

    /// <summary>
    /// Un projet en miniature dans le dossier du test : une copie du binaire dans bin/, les prompts, et une
    /// configuration config/app.json qui pointe vers le faux service IA et le corpus Solvéo de ce dépôt. Lancé de là
    /// par Launch, assistant prend ce dossier pour la racine du projet, car AppConfig.ProjectRoot cherche, au-dessus du
    /// binaire, le premier dossier qui contient config/app.json. Sans --out, les dossiers de résultats s'écrivent donc
    /// ici, et non dans ce dépôt.
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
    /// (dotnet bin/assistant.dll). Elle tourne depuis la copie <paramref name="root"/> faite par CopyProject, qui sert
    /// aussi de dossier courant.
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

    [Fact]
    public void An_unreachable_ai_service_stops_the_experiments_before_any_folder()
    {
        // L'expérience s'arrête sur le message « injoignable », avec le code 1, avant de créer le moindre dossier.
        var config = WriteConfig("http://127.0.0.1:1");
        var questions = Path.Combine(_dir, "questions.json");
        File.WriteAllText(questions, """{"questions": [{"id": "a", "user": "alice", "question": "Combien ?"}]}""");
        var outDir = Path.Combine(_dir, "sortie");
        var (code, output, errors) = Run("experience", "stabilite", "--questions", questions, "--out", outDir, "--config", config);
        Assert.Equal((1, ""), (code, output));
        Assert.Equal("Erreur : service IA injoignable (http://127.0.0.1:1). Lancez-le d'abord : python -m ai_service\n", errors);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void Without_out_an_experiment_never_writes_in_a_folder_that_exists()
    {
        // Sans --out, le dossier de sortie est daté à la seconde, sous eval/resultats à la racine du projet : deux
        // commandes lancées dans la même seconde tomberaient sur le même. La deuxième prend donc le suivant, « -2 », et
        // laisse l'autre intact (newOutDir). La commande est lancée depuis une copie du projet (CopyProject, Launch)
        // pour écrire sous le dossier du test et non dans ce dépôt. Les dossiers des 60 secondes à venir y sont créés
        // d'avance, comme ceux d'une commande lancée juste avant.
        using var ai = new FakeAiService();
        var root = CopyProject(ai.Url);
        var now = DateTime.Now;
        var taken = Enumerable.Range(0, 60)   // le dossier que la commande prendrait à chacune de ces secondes
                              .Select(s => Path.Combine(root, AppConfig.ResultsDir("exp-stabilite-", now.AddSeconds(s), from: AppConfig.ProjectRoot)))
                              .ToList();
        taken.ForEach(folder => Directory.CreateDirectory(folder));
        var (code, output, errors) = Launch(root, "experience", "stabilite", "--runs", "2", "--questions", WriteQuestions(),
                                            "--config", Path.Combine(root, "config", "app.json"));
        Assert.True(code == 0, errors);
        Assert.All(taken, folder => Assert.Empty(Directory.GetFileSystemEntries(folder)));
        var second = Assert.Single(taken, folder => Directory.Exists(folder + "-2")) + "-2";
        Assert.EndsWith($"\nRapport : {Path.GetRelativePath(root, second).Replace('\\', '/')}/rapport.md\n", output);
    }

    [Fact]
    public void An_empty_out_is_the_dated_folder()
    {
        // --out "" vaut l'option absente : comme dans le test précédent, l'expérience écrit dans le dossier daté sous
        // eval/resultats, jamais dans un dossier qui existe déjà. Pris pour un chemin, il arrêterait la commande sur un
        // message .NET en anglais (« The value cannot be an empty string »).
        using var ai = new FakeAiService();
        var root = CopyProject(ai.Url);
        var now = DateTime.Now;
        var taken = Enumerable.Range(0, 60)   // le dossier que la commande prendrait à chacune de ces secondes
                              .Select(s => Path.Combine(root, AppConfig.ResultsDir("exp-stabilite-", now.AddSeconds(s), from: AppConfig.ProjectRoot)))
                              .ToList();
        taken.ForEach(folder => Directory.CreateDirectory(folder));
        var (code, output, errors) = Launch(root, "experience", "stabilite", "--runs", "2", "--out", "", "--questions", WriteQuestions(),
                                            "--config", Path.Combine(root, "config", "app.json"));
        Assert.True(code == 0, errors);
        Assert.All(taken, folder => Assert.Empty(Directory.GetFileSystemEntries(folder)));
        var second = Assert.Single(taken, folder => Directory.Exists(folder + "-2")) + "-2";
        Assert.EndsWith($"\nRapport : {Path.GetRelativePath(root, second).Replace('\\', '/')}/rapport.md\n", output);
    }
}
