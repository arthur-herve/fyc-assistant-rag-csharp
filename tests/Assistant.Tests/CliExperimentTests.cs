// La commande experience : les options de chaque expérience, vérifiées avant tout travail, et l'en-tête du
// rapport.

using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

[Collection("Console")]
public sealed class CliExperimentTests : CliTestBase
{
    [Theory]
    [InlineData("option(s) inconnue(s) pour experience stabilite : --max-chars", "experience", "stabilite", "--max-chars", "300")]
    [InlineData("option(s) inconnue(s) pour experience cace-decoupage : --other", "experience", "cace-decoupage", "--other", "nomic")]
    public void A_mistyped_abbreviated_or_foreign_option_is_refused_before_any_work(string message, params string[] argv) =>
        AssertRefused(message, argv);

    [Theory]
    [InlineData("--other", "experience", "--other", "nomic", "changement-embeddings")]
    public void The_command_comes_first(string option, params string[] argv) =>
        AssertRefused($"option placée avant la commande : {option} (la commande vient d'abord : assistant <commande> [options])", argv);

    [Theory]
    [InlineData("argument(s) en trop pour experience stabilite : encore", "experience", "stabilite", "encore")]
    public void An_extra_argument_is_refused(string message, params string[] argv) => AssertRefused(message, argv);

    [Theory]
    [InlineData("expérience inconnue : nope (connues : cace-decoupage, changement-embeddings, changement-generateur, stabilite)",
                "experience", "nope")]
    public void A_wrong_value_or_an_unknown_subcommand_is_refused_before_any_work(string message, params string[] argv) =>
        AssertRefused(message, argv);

    // Pour une option d'expérience fausse, la configuration est lue d'abord : si elle manque, c'est elle que l'erreur
    // nomme. L'option est ensuite refusée par Experiments.Run avant tout travail, sans dossier de sortie ni appel au
    // service IA.
    [Theory]
    [InlineData("--runs doit valoir au moins 2 : il faut deux passages pour mesurer une dérive", "stabilite", "--runs", "1")]
    [InlineData("--limit doit valoir au moins 1, pas 0", "stabilite", "--limit", "0")]
    [InlineData("max_chars doit valoir au moins 100", "cace-decoupage", "--max-chars", "50")]
    // 150 est la moitié des 300 caractères par défaut : c'est la première valeur refusée.
    [InlineData("overlap_chars doit être compris entre 0 et max_chars / 2", "cace-decoupage", "--overlap-chars", "150")]
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

    // Une valeur qui n'est pas un entier est refusée au démarrage, avant même de lire la configuration.
    [Theory]
    [InlineData("l'option --runs attend un entier, pas « deux »\n", "experience", "stabilite", "--runs", "deux")]
    public void A_non_integer_value_is_refused_before_the_configuration_even_if_repeated(string message, params string[] argv) =>
        AssertRefused(message, argv);

    [Theory]
    [InlineData("Erreur : experience attend un nom : cace-decoupage, changement-embeddings, changement-generateur, stabilite",
                "experience")]
    public void A_command_is_one_word_or_two_separate_words(string message, params string[] argv)
    {
        var (code, _, errors) = Run(argv);
        Assert.Equal(1, code);
        Assert.Contains(message, errors);
    }

    [Theory]
    [InlineData("experience", "cace-decoupage", "--max-chars", "300", "--overlap-chars", "50", "--questions", "q.json")]
    [InlineData("experience", "changement-embeddings", "--other", "nomic", "--limit", "2")]
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
}
