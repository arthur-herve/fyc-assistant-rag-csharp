// Les expériences, de bout en bout contre un faux service IA (embeddings par mots-clés, génération qui cite [1]).

using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

/// <summary>
/// Le déroulé des expériences, de bout en bout contre <see cref="FakeAiService"/>. Les options et la configuration
/// sont vérifiées avant tout travail, le seuil `default` est signalé après ces vérifications, et la console montre
/// la progression. Ces tests lisent la console, d'où la collection « Console », comme ceux de la ligne de commande.
/// </summary>
[Collection("Console")]
public sealed class ExperimentRunTests : EvalRunTestBase
{
    private int Experiment(string name, params string[] options) => Experiment(_config, new Overrides(), name, options);

    private int Experiment(AppConfig config, Overrides common, string name, params string[] options) =>
        Experiments.Run(name, new Args(new[] { "experience", name, "--questions", _questionsPath }.Concat(options).ToArray()),
                        config, common, "config");

    [Fact]
    public void Experiment_options_are_refused_before_any_work()
    {
        var outDir = Path.Combine(_dir, "exp");
        Assert.Equal("max_chars doit valoir au moins 100",
                     Assert.Throws<ArgumentException>(() => Experiment("cace-decoupage", "--max-chars", "50", "--out", outDir)).Message);
        Assert.Equal(NoPrompt, Assert.Throws<Application.PromptNotFoundException>(
            () => Experiment("prompt-v2", "--other", "nexiste", "--out", outDir)).Message);
        Assert.Equal("--limit doit valoir au moins 1, pas 0",
                     Assert.Throws<ArgumentException>(() => Experiment("stabilite", "--limit", "0", "--out", outDir)).Message);
        File.WriteAllText(_questionsPath, """{"questions": [{"id": "q", "user": "mallory", "question": "?"}]}""");
        Assert.Contains("mallory", Assert.Throws<UnknownUserException>(() => Experiment("stabilite", "--out", outDir)).Message);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void The_main_configuration_of_an_experiment_is_checked_before_any_work()
    {
        // Pas seulement ce que l'expérience change : le prompt principal aussi, qu'il vienne de la configuration ou
        // de --prompt.
        var outDir = Path.Combine(_dir, "exp");
        Assert.Equal(NoPrompt, Assert.Throws<Application.PromptNotFoundException>(
            () => Experiment(_config with { PromptName = "nexiste" }, new Overrides(), "stabilite", "--out", outDir)).Message);
        Assert.Equal(NoPrompt, Assert.Throws<Application.PromptNotFoundException>(
            () => Experiment(_config, new Overrides(PromptName: "nexiste"), "stabilite", "--out", outDir)).Message);
        Assert.False(Directory.Exists(outDir));
    }

    /// <summary>
    /// Le résultat de l'action et ce qu'elle a écrit en console, sortie et erreurs, avec des « \n ». Avec
    /// <paramref name="raw"/>, le texte reste tel quel, avec les fins de ligne de WriteLine propres à la plateforme.
    /// </summary>
    private static (T Result, string Out, string Err) Captured<T>(Func<T> action, bool raw = false)
    {
        var (previousOut, previousError) = (Console.Out, Console.Error);
        var (output, errors) = (new StringWriter(), new StringWriter());
        Console.SetOut(output);
        Console.SetError(errors);
        try
        {
            var result = action();
            string Text(StringWriter writer) => raw ? writer.ToString() : writer.ToString().Replace("\r\n", "\n");
            return (result, Text(output), Text(errors));
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    [Fact]
    public void An_experiment_says_when_the_default_threshold_applies()
    {
        // Ni le modèle principal ni --other n'ont de seuil configuré : `default` s'applique, et l'expérience le dit en
        // console et dans le rapport.
        var outDir = Path.Combine(_dir, "exp-embeddings");
        var (code, _, errors) = Captured(() => Experiment("changement-embeddings", "--other", "hashing-512", "--out", outDir));
        Assert.Equal(0, code);
        var warnings = new[] { "hashing", "hashing-512" }
            .Select(alias => $"Attention : aucun seuil de pertinence configuré pour « {alias} » : valeur `default` 0.15 (à calibrer avec le banc d'essai)")
            .ToArray();
        Assert.Equal(warnings, errors.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        var report = File.ReadAllText(Path.Combine(outDir, "rapport.md"));
        foreach (var warning in warnings)
        {
            Assert.Contains($"> {warning}", report);
        }
        Assert.Contains("| seuil | 0.15 | 0.15 |", report);
    }

    [Fact]
    public void An_experiment_report_has_lf_line_endings_only()
    {
        // Le rapport est en « \n », sous Windows aussi, le bloc « Détail » de la comparaison compris.
        var outDir = Path.Combine(_dir, "exp-embeddings");
        var (code, _, _) = Captured(() => Experiment("changement-embeddings", "--other", "hashing-512", "--out", outDir));
        Assert.Equal(0, code);
        var report = File.ReadAllText(Path.Combine(outDir, "rapport.md"));
        Assert.DoesNotContain('\r', report);
    }

    [Theory]
    [InlineData("changement-embeddings", "extractive", "modèle d'embeddings (servis : hashing, hashing-512)")]
    [InlineData("changement-embeddings", "nexiste", "modèle d'embeddings (servis : hashing, hashing-512)")]
    [InlineData("changement-generateur", "hashing", "modèle de génération (servis : extractive)")]
    public void The_other_alias_of_an_experiment_is_checked_before_any_index(string name, string other, string served)
    {
        // --other doit être servi, et du bon type, selon GET /v1/models. Sinon l'expérience s'arrête sans index ni
        // instantané, et sans afficher d'avertissement avant l'erreur.
        var outDir = Path.Combine(_dir, "exp-autre");
        var (error, output, errors) = Captured(() => Assert.Throws<ArgumentException>(
            () => Experiment(name, "--limit", "1", "--other", other, "--out", outDir)));
        Assert.Equal($"le service IA ne sert pas « {other} » comme {served}", error.Message);
        Assert.Equal(("", ""), (output, errors));
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void The_default_threshold_is_announced_after_the_checks()
    {
        // Avec une option fausse ou un service IA injoignable, seule l'erreur s'affiche, sans avertissement avant elle.
        var outDir = Path.Combine(_dir, "exp");
        var (invalid, _, errors) = Captured(() => Assert.Throws<ArgumentException>(
            () => Experiment("cace-decoupage", "--max-chars", "50", "--out", outDir)));
        Assert.Equal("max_chars doit valoir au moins 100", invalid.Message);
        Assert.Equal("", errors);
        var (unreachable, _, stillNothing) = Captured(() => Assert.Throws<Application.AiServiceException>(
            () => Experiment(_config with { AiBaseUrl = "http://127.0.0.1:9" }, new Overrides(), "stabilite", "--out", outDir)));
        Assert.Equal("service IA injoignable (http://127.0.0.1:9). Lancez-le d'abord : python -m ai_service", unreachable.Message);
        Assert.Equal("", stillNothing);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void An_experiment_prints_its_steps_and_the_path_of_its_report()
    {
        // La console affiche « Avant : 800 / 120 », une ligne par index et par instantané, puis « Rapport : » et son
        // chemin. Les valeurs sont connues d'avance : trois documents d'un paragraphe donnent 3 morceaux avant comme
        // après, et la dimension est celle du faux service, un axe par mot du vocabulaire plus l'axe constant
        // (FakeAiService).
        var outDir = Path.Combine(_dir, "exp-cace");
        var (code, output, _) = Captured(() => Experiment("cace-decoupage", "--limit", "1", "--out", outDir));
        Assert.Equal(0, code);
        Assert.Contains("Avant : 800 / 120\n", output);
        Assert.Contains("Après : 300 / 50\n", output);
        foreach (var name in new[] { "avant", "apres" })
        {
            Assert.Contains($"  index « {name} » : 3 morceaux, faux:hashing, {Fakes.Vocabulary.Length + 1} dim., ", output);
            Assert.Contains($"  instantané « {name} » : 1 réponses en ", output);
        }
        Assert.EndsWith($"\nRapport : {Path.Combine(outDir, "rapport.md").Replace('\\', '/')}\n", output);
    }

    [Fact]
    public void An_experiment_prints_the_line_endings_of_the_platform()
    {
        var outDir = Path.Combine(_dir, "exp-cace");
        var (code, raw, _) = Captured(() => Experiment("cace-decoupage", "--limit", "1", "--out", outDir), raw: true);
        Assert.Equal(0, code);
        // Les fins de ligne sont celles de la plateforme. Sous Windows, aucun « \n » ne doit rester seul, pas même pour
        // la ligne vide avant « Rapport : ».
        Assert.DoesNotContain("\n", raw.Replace(Environment.NewLine, ""));
    }

    [Fact]
    public void Stability_writes_its_mean_drift_in_the_report()
    {
        var outDir = Path.Combine(_dir, "exp-stabilite");
        Assert.Equal(0, Captured(() => Experiment("stabilite", "--runs", "2", "--out", outDir)).Result);
        // Avec un générateur déterministe, la dérive est nulle et s'écrit « 0 » (format « 0.### »).
        var report = File.ReadAllText(Path.Combine(outDir, "rapport.md"));
        Assert.Contains("**Dérive moyenne à configuration constante : 0** (0 changement(s) de statut sur 1 comparaison(s)).", report);
    }

    [Fact]
    public void Stability_writes_its_reading_in_the_report()
    {
        var outDir = Path.Combine(_dir, "exp-stabilite");
        Assert.Equal(0, Captured(() => Experiment("stabilite", "--runs", "2", "--out", outDir)).Result);
        // Avec un générateur déterministe, la lecture dit qu'aucune réponse n'a bougé (StabilityReading).
        var report = File.ReadAllText(Path.Combine(outDir, "rapport.md"));
        Assert.Contains("\n## Lecture\n\n- Aucune réponse n'a bougé d'un passage à l'autre : avec ces réglages, ce générateur s'est montré déterministe.",
                        report);
    }

    [Fact]
    public void Two_experiments_of_the_same_second_keep_their_own_folder()
    {
        // Comme pour le banc, le second dossier reçoit « -2 » (newOutDir) et ses index y sont écrits.
        var path = Path.Combine(_dir, "resultats", "exp-stabilite-20261001-164152");
        var (experiments, _, _) = Captured(() =>
        {
            var made = new[] { 1, 2 }.Select(i => new Experiment("stabilite", _config, Questions, path, "questions", "config", newOutDir: true))
                                     .ToList();
            made.ForEach(e => e.Index("partage"));
            return made;
        });
        Assert.Equal(new[] { path, path + "-2" }, experiments.Select(e => e.OutDir));
        Assert.All(experiments, e => Assert.True(File.Exists(Path.Combine(e.OutDir, "index-partage.json"))));
    }

    [Fact]
    public void With_out_an_experiment_writes_in_the_folder_given_even_if_it_exists()
    {
        // Seul le dossier par défaut, daté à la seconde, reçoit « -2 » quand il existe déjà. Avec --out, l'expérience
        // écrit dans le dossier donné, même s'il existe.
        var outDir = Directory.CreateDirectory(Path.Combine(_dir, "exp-existant")).FullName;
        var (code, output, _) = Captured(() => Experiment("stabilite", "--runs", "2", "--out", outDir));
        Assert.Equal(0, code);
        Assert.EndsWith($"\nRapport : {Path.Combine(outDir, "rapport.md").Replace('\\', '/')}\n", output);
        Assert.False(Path.Exists(outDir + "-2"));
    }
}
