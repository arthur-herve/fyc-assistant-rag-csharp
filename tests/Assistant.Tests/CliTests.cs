// La ligne de commande, appelée comme on la tape. Une saisie fausse doit être refusée avant tout travail. Ce
// fichier vérifie les règles de l'analyse, montrées sur index et ask, et ce qui vaut pour toutes les commandes.
// Les tests propres à une commande sont dans un fichier à son nom.

using System.Text.Json.Nodes;
using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

[Collection("Console")]
public sealed class CliTests : CliTestBase
{
    private const string Question = "Combien de jours de télétravail par semaine ?";

    [Theory]
    [InlineData("option(s) inconnue(s) pour ask : --us", "ask", Question, "--us", "bruno")]
    [InlineData("option(s) inconnue(s) pour ask : --usr", "ask", Question, "--usr=bruno")]
    [InlineData("option(s) inconnue(s) pour index : --user", "index", "--user", "bruno")]
    public void A_mistyped_abbreviated_or_foreign_option_is_refused_before_any_work(string message, params string[] argv) =>
        AssertRefused(message, argv);

    [Theory]
    [InlineData("option(s) inconnue(s) pour index : --jsn (acceptées", "index", "--jsn", "--jsn")]
    public void An_unknown_option_given_twice_is_named_once(string message, params string[] argv) => AssertRefused(message, argv);

    [Theory]
    [InlineData("--json", "--json")]
    [InlineData("--", "--", "index")]
    public void The_command_comes_first(string option, params string[] argv) =>
        AssertRefused($"option placée avant la commande : {option} (la commande vient d'abord : assistant <commande> [options])", argv);

    // Après « -- », tout est un argument, même ce qui ressemble à une option, comme une question qui commence par un
    // tiret ou « -h ». Les options se placent donc avant.
    [Theory]
    [InlineData("ask", "CONFIG", "--", "-x")]
    [InlineData("ask", "--user", "bruno", "CONFIG", "--", "-v jours")]
    [InlineData("ask", "CONFIG", "--", "-télétravail")]   // l'exemple de l'aide et du README
    [InlineData("ask", "CONFIG", "--", "-h")]   // pas une demande d'aide
    public void A_double_dash_ends_the_options(params string[] argv)
    {
        var (code, output, errors) = Run(WithConfig(argv));
        Assert.Equal((1, ""), (code, output));
        Assert.Contains("absente.json", errors);   // la saisie est acceptée : c'est le fichier qui manque
    }

    [Theory]
    [InlineData("ask attend une seule question, entre guillemets", "ask", "Q", "CONFIG", "--", "--json")]
    [InlineData("option(s) inconnue(s) pour ask : --usr", "ask", "--usr", "x", "CONFIG", "--", "-h")]   // -h : un argument
    public void After_a_double_dash_everything_is_an_argument(string message, params string[] argv)
    {
        var (code, output, errors) = Run(WithConfig(argv));
        Assert.Equal((1, ""), (code, output));
        Assert.Contains($"Erreur : {message}", errors);
    }

    [Theory]
    [InlineData("argument(s) en trop pour index : maintenant", "index", "maintenant")]
    [InlineData("ask attend une seule question, entre guillemets", "ask", "Combien", "de", "jours", "?")]
    public void An_extra_argument_is_refused(string message, params string[] argv) => AssertRefused(message, argv);

    [Theory]
    [InlineData("ask attend une seule question, entre guillemets", "ask")]
    public void A_missing_argument_is_refused(string message, params string[] argv) => AssertRefused(message, argv);

    [Fact]
    public void A_call_without_a_command_is_an_error() => Assert.Equal(1, Run().Code);

    [Fact]
    public void An_option_without_its_value_is_refused() =>
        Assert.Contains("Erreur : l'option --user attend une valeur", Run("ask", Question, "--config", _missing, "--user").Err);

    [Theory]
    [InlineData("--help")]
    [InlineData("ask", "-h")]
    [InlineData("ask", "--us", "-h")]
    public void Help_comes_first_wherever_it_is(params string[] argv) => Assert.Equal(0, Run(argv).Code);

    [Theory]
    [InlineData("ask", Question, "--user", "alice", "--json", "--verbose")]
    public void The_options_of_each_command_reach_the_configuration(params string[] argv)
    {
        var (code, _, errors) = Run(argv.Append("--config").Append(_missing).ToArray());
        Assert.Equal(1, code);
        Assert.Contains("absente.json", errors);   // la saisie est acceptée : c'est le fichier qui manque
        Assert.DoesNotContain("inconnue", errors);
    }

    // Sans « -- », un mot qui commence par un tiret est un argument s'il est « - », un nombre négatif, ou si son nom
    // (ce qui précède un éventuel « = ») contient une espace. Une valeur peut donc en contenir une.
    [Theory]
    [InlineData("ask", "-")]
    [InlineData("ask", "-1")]
    [InlineData("ask", "-5 jours ?")]
    [InlineData("ask", "-vingt degrés ?")]
    [InlineData("ask", "-v jours")]
    [InlineData("ask", "- Combien de jours ?", "--user=bruno martin")]
    public void A_lone_dash_a_negative_number_or_a_name_with_a_space_is_an_argument(params string[] argv)
    {
        var (code, _, errors) = Run(argv.Append("--config").Append(_missing).ToArray());
        Assert.Equal(1, code);
        Assert.Contains("absente.json", errors);   // la saisie est acceptée : c'est le fichier qui manque
        Assert.DoesNotContain("inconnue", errors);
    }

    [Fact]
    public void An_unknown_prompt_names_its_folder_and_the_known_ones()
    {
        // Le message dit « prompt introuvable » et nomme les prompts connus, au lieu d'un « fichier illisible » suivi
        // du message .NET « Could not find file ». Le prompt est celui que nomme generation.prompt dans la
        // configuration. ask ne le lit qu'après avoir retrouvé des passages, d'où l'index.
        using var ai = new FakeAiService();
        var config = WriteConfig(ai.Url);
        var json = JsonNode.Parse(File.ReadAllText(config))!.AsObject();
        json["generation"] = new JsonObject { ["prompt"] = "nexiste" };
        File.WriteAllText(config, json.ToJsonString());
        Assert.Equal(0, Run("index", "--config", config).Code);
        var (code, output, errors) = Run("ask", Question, "--config", config);
        Assert.Equal((1, ""), (code, output));
        Assert.Equal($"Erreur : prompt introuvable : nexiste dans {Composition.PromptsDir} (connus : answer)\n", errors);
    }

    [Fact]
    public void The_help_says_how_to_end_the_options()
    {
        var help = Run("--help").Out;
        // Les exemples de l'aide sont vérifiés par A_double_dash_ends_the_options (« -télétravail ») et
        // A_lone_dash_a_negative_number_or_a_name_with_a_space_is_an_argument (« -vingt degrés ? »).
        Assert.Contains("« -- » termine les options : ce qui suit est un argument, même un mot qui commence par un tiret "
                        + "(ask -- -télétravail) ; sans « -- », un tel mot n'est un argument que s'il est « - », un nombre négatif, ou si son "
                        + "nom (ce qui précède un éventuel « = ») contient une espace (ask \"-vingt degrés ?\").", help);
    }
}
