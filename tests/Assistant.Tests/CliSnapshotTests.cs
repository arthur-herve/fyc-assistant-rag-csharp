// Les commandes snapshot record, compare et list : leurs options, leurs arguments et ce qu'elles affichent.

using Xunit;

namespace Assistant.Tests;

[Collection("Console")]
public sealed class CliSnapshotTests : CliTestBase
{
    [Theory]
    [InlineData("option(s) inconnue(s) pour snapshot list : --limit", "snapshot", "list", "--limit", "3")]
    [InlineData("option(s) inconnue(s) pour snapshot compare : --questions", "snapshot", "compare", "a", "b", "--questions", "q.json")]
    public void A_mistyped_abbreviated_or_foreign_option_is_refused_before_any_work(string message, params string[] argv) =>
        AssertRefused(message, argv);

    [Theory]
    [InlineData("-v", "-v", "snapshot", "list")]
    [InlineData("-v", "snapshot", "-v", "list")]
    [InlineData("--", "snapshot", "--", "list")]
    public void The_command_comes_first(string option, params string[] argv) =>
        AssertRefused($"option placée avant la commande : {option} (la commande vient d'abord : assistant <commande> [options])", argv);

    // Après « -- », tout est un argument, même ce qui ressemble à une option.
    [Theory]
    [InlineData("snapshot", "compare", "CONFIG", "--", "-a", "-b")]
    public void A_double_dash_ends_the_options(params string[] argv)
    {
        var (code, output, errors) = Run(WithConfig(argv));
        Assert.Equal((1, ""), (code, output));
        Assert.Contains("absente.json", errors);   // la saisie est acceptée : c'est le fichier qui manque
    }

    [Theory]
    [InlineData("a", "--", "--")]
    [InlineData("--", "a", "--")]
    public void A_double_dash_typed_after_the_one_that_ends_the_options_is_a_name(params string[] names)
    {
        // Le second nom est « -- », qui n'est pas un nom valide : on attend un message d'erreur, pas une trace.
        using var ai = new FakeAiService();
        var config = WriteConfig(ai.Url);
        Assert.Equal(0, Run("index", "--config", config).Code);
        Assert.Equal(0, Run("snapshot", "record", "a", "--limit", "1", "--config", config).Code);
        var (code, output, errors) = Run(new[] { "snapshot", "compare", "--config", config }.Concat(names).ToArray());
        Assert.Equal((1, ""), (code, output));
        Assert.Contains("Erreur : nom d'instantané invalide : « -- » (", errors);
    }

    [Theory]
    [InlineData("argument(s) en trop pour snapshot list : tout", "snapshot", "list", "tout")]
    [InlineData("argument(s) en trop pour snapshot compare : c", "snapshot", "compare", "a", "b", "c")]
    public void An_extra_argument_is_refused(string message, params string[] argv) => AssertRefused(message, argv);

    [Theory]
    [InlineData("snapshot record attend un nom", "snapshot", "record")]
    [InlineData("snapshot compare attend deux noms", "snapshot", "compare", "a")]
    public void A_missing_argument_is_refused(string message, params string[] argv) => AssertRefused(message, argv);

    [Theory]
    [InlineData("snapshot attend record, compare ou list", "snapshot", "bidule")]
    public void A_wrong_value_or_an_unknown_subcommand_is_refused_before_any_work(string message, params string[] argv) =>
        AssertRefused(message, argv);

    // Une valeur qui n'est pas un entier est refusée au démarrage, pour toutes les commandes, avant même de lire la
    // configuration. Quand l'option est répétée, chaque valeur est vérifiée, pas seulement la dernière.
    [Theory]
    [InlineData("l'option --limit attend un entier, pas « abc »\n", "snapshot", "record", "x", "--limit", "abc")]
    [InlineData("l'option --limit attend un entier, pas « abc »\n", "snapshot", "record", "x", "--limit", "abc", "--limit", "2")]
    public void A_non_integer_value_is_refused_before_the_configuration_even_if_repeated(string message, params string[] argv) =>
        AssertRefused(message, argv);

    [Fact]
    public void Snapshot_record_refuses_a_limit_below_one_before_any_question()
    {
        // --limit doit valoir au moins 1. Il est vérifié avec le jeu de questions, donc après la lecture de la
        // configuration, comme pour les expériences. Un nombre négatif est lu comme une valeur, pas comme une option.
        using var ai = new FakeAiService();
        var (code, output, errors) = Run("snapshot", "record", "x", "--limit", "-1", "--questions", WriteQuestions(), "--config", WriteConfig(ai.Url));
        Assert.Equal((1, ""), (code, output));
        Assert.EndsWith("Erreur : --limit doit valoir au moins 1, pas -1\n", errors);
        Assert.False(Directory.Exists(Path.Combine(_dir, "instantanes")));
        Assert.Equal(0, ai.Requests);
    }

    [Theory]
    [InlineData("Erreur : snapshot attend record, compare ou list", "snapshot")]
    [InlineData("Commande inconnue : snapshot list", "snapshot list")]   // un seul argument
    public void A_command_is_one_word_or_two_separate_words(string message, params string[] argv)
    {
        var (code, _, errors) = Run(argv);
        Assert.Equal(1, code);
        Assert.Contains(message, errors);
    }

    [Theory]
    [InlineData("snapshot", "record", "x", "--questions", "q.json", "--limit", "2")]
    // La borne : --limit 1.
    [InlineData("snapshot", "record", "x", "--limit", "1")]
    public void The_options_of_each_command_reach_the_configuration(params string[] argv)
    {
        var (code, _, errors) = Run(argv.Append("--config").Append(_missing).ToArray());
        Assert.Equal(1, code);
        Assert.Contains("absente.json", errors);   // la saisie est acceptée : c'est le fichier qui manque
        Assert.DoesNotContain("inconnue", errors);
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
        Assert.Contains("  splitter = {include_title=true, max_chars=800, overlap_chars=120, type=paragraph}", lines);
    }

    [Fact]
    public void The_models_chosen_on_the_command_line_are_named_in_the_snapshot()
    {
        // --embedding-model et --generation-model remplacent les modèles de la configuration. L'instantané nomme ceux
        // qui ont servi, et la racine de composition donne les mêmes aux adaptateurs.
        using var ai = new FakeAiService();
        var config = WriteConfig(ai.Url);
        Assert.Equal(0, Run("index", "--embedding-model", "hashing-512", "--config", config).Code);
        var (code, output, errors) = Run("snapshot", "record", "x", "--questions", WriteQuestions(), "--embedding-model", "hashing-512",
                                         "--generation-model", "extractive-variante", "--config", config);
        Assert.True(code == 0, errors);
        var lines = output.TrimEnd('\n').Split('\n');
        Assert.Contains("  embedding_model = hashing-512", lines);
        Assert.Contains("  generation_model = extractive-variante", lines);
    }

    [Fact]
    public void An_empty_questions_file_is_no_value()
    {
        // Même règle pour les jeux de questions : --questions "" vaut le jeu par défaut, eval/questions.json sous la
        // racine du projet. Pris pour un chemin, il arrêterait la commande sur un message .NET en anglais (« The value
        // cannot be an empty string »).
        using var ai = new FakeAiService();
        var config = WriteConfig(ai.Url);
        Assert.Equal(0, Run("index", "--config", config).Code);
        var (code, output, errors) = Run("snapshot", "record", "vide", "--questions", "", "--limit", "1", "--config", config);
        Assert.True(code == 0, errors);
        Assert.StartsWith("Instantané « vide » : 1 réponses", output);
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
}
