// La commande benchmark : ses options, vérifiées avant d'interroger le service IA, et son dossier de sortie, qu'elle
// partage avec les expériences.

using System.Diagnostics;
using System.Text;
using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

[Collection("Console")]
public sealed class CliBenchmarkTests : CliTestBase
{
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

    [Theory]
    [InlineData("option(s) inconnue(s) pour benchmark : -v", "benchmark", "-v")]
    public void A_mistyped_abbreviated_or_foreign_option_is_refused_before_any_work(string message, params string[] argv) =>
        AssertRefused(message, argv);

    [Theory]
    [InlineData("argument(s) en trop pour benchmark : b", "benchmark", "--embedding", "a", "CONFIG", "--", "b")]
    public void After_a_double_dash_everything_is_an_argument(string message, params string[] argv)
    {
        var (code, output, errors) = Run(WithConfig(argv));
        Assert.Equal((1, ""), (code, output));
        Assert.Contains($"Erreur : {message}", errors);
        Assert.DoesNotContain("Attention", errors);   // refusé avant de lire la configuration
    }

    [Theory]
    [InlineData("argument(s) en trop pour benchmark : hashing", "benchmark", "hashing")]
    public void An_extra_argument_is_refused(string message, params string[] argv) => AssertRefused(message, argv);

    // Une valeur qui n'est pas un entier est refusée au démarrage, avant même de lire la configuration.
    [Theory]
    [InlineData("l'option --seed attend un entier, pas « abc »\n", "benchmark", "--seed", "abc")]
    public void A_non_integer_value_is_refused_before_the_configuration_even_if_repeated(string message, params string[] argv) =>
        AssertRefused(message, argv);

    [Theory]
    [InlineData("benchmark", "--embedding-model", "hashing-512", "--generation-model", "extractive")]
    // La borne : --limit 1.
    [InlineData("benchmark", "--limit", "1")]
    public void The_options_of_each_command_reach_the_configuration(params string[] argv)
    {
        var (code, _, errors) = Run(argv.Append("--config").Append(_missing).ToArray());
        Assert.Equal(1, code);
        Assert.Contains("absente.json", errors);   // la saisie est acceptée : c'est le fichier qui manque
        Assert.DoesNotContain("inconnue", errors);
    }

    [Fact]
    public void An_unreachable_ai_service_stops_the_benchmark_and_the_experiments_before_any_folder()
    {
        // Le banc comme les expériences s'arrêtent sur le message « injoignable », avec le code 1, avant de créer le
        // moindre dossier.
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
        // Seul le dossier par défaut, daté à la seconde, reçoit un suffixe « -2 », « -3 » quand il existe déjà. Avec
        // --out, le banc écrit dans le dossier donné, même s'il existe.
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
        // Sans --out, le dossier de sortie est daté à la seconde, sous eval/resultats à la racine du projet : deux
        // commandes lancées dans la même seconde tomberaient sur le même. La deuxième prend donc le suivant, « -2 », et
        // laisse l'autre intact (NewOutDir pour le banc, newOutDir pour les expériences). La commande est lancée depuis
        // une copie du projet (CopyProject, Launch) pour écrire sous le dossier du test et non dans ce dépôt. Les
        // dossiers des 60 secondes à venir y sont créés d'avance, comme ceux d'une commande lancée juste avant.
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
        // --out "" vaut l'option absente : comme dans le test précédent, la commande écrit dans le dossier daté sous
        // eval/resultats, jamais dans un dossier qui existe déjà. Pris pour un chemin, il arrêterait la commande sur un
        // message .NET en anglais (« The value cannot be an empty string »).
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

    // Même avec un service IA injoignable, une option fausse est signalée, car elle est vérifiée avant de
    // l'interroger, et aucun dossier de résultats n'est créé. Le message est comparé jusqu'à la fin de ligne, pour
    // vérifier qu'il ne traîne pas le « (Parameter 'maxChars') » que .NET ajoute.
    [Theory]
    [InlineData("Erreur : --runs doit valoir au moins 1, pas 0\n", "--runs", "0")]
    [InlineData("Erreur : --min-score attend config, auto ou un nombre de [-1, 1] (ex. 0.6), pas « abc »\n", "--min-score", "abc")]
    [InlineData("Erreur : max_chars doit valoir au moins 100\n", "--max-chars", "50")]
    [InlineData("Erreur : overlap_chars doit être compris entre 0 et max_chars / 2\n", "--overlap-chars", "900")]
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
}
