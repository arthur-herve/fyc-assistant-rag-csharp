// La commande status : ses options et ses arguments, refusés ou acceptés avant tout travail, puis ce qu'elle fait d'un
// prompt inconnu et des options communes données vides.

using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

[Collection("Console")]
public sealed class CliStatusTests : CliTestBase
{
    [Theory]
    [InlineData("option(s) inconnue(s) pour status : -json (acceptées : --config, --embedding-model, --generation-model, --json, --prompt, --verbose, -v)",
                "status", "-json")]
    [InlineData("option(s) inconnue(s) pour status : --jsn", "status", "--jsn")]   // et non « --jsn attend une valeur »
    [InlineData("option(s) inconnue(s) pour status : --if-stale, --user", "status", "--if-stale", "--user", "bruno")]
    public void A_mistyped_abbreviated_or_foreign_option_is_refused_before_any_work(string message, params string[] argv) =>
        AssertRefused(message, argv);

    [Theory]
    [InlineData("--json", "--json", "status")]
    [InlineData("--config", "--config=app.json", "status")]
    public void The_command_comes_first(string option, params string[] argv) =>
        AssertRefused($"option placée avant la commande : {option} (la commande vient d'abord : assistant <commande> [options])", argv);

    // Après « -- », tout est un argument, même ce qui ressemble à une option.
    [Theory]
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
    public void After_a_double_dash_everything_is_an_argument(string message, params string[] argv)
    {
        var (code, output, errors) = Run(WithConfig(argv));
        Assert.Equal((1, ""), (code, output));
        Assert.Contains($"Erreur : {message}", errors);
        Assert.DoesNotContain("Attention", errors);   // refusé avant de lire la configuration
    }

    [Theory]
    [InlineData("argument(s) en trop pour status : foo", "status", "foo")]
    public void An_extra_argument_is_refused(string message, params string[] argv) => AssertRefused(message, argv);

    [Theory]
    [InlineData("l'option --json ne prend pas de valeur", "status", "--json=1")]
    public void A_wrong_value_or_an_unknown_subcommand_is_refused_before_any_work(string message, params string[] argv) =>
        AssertRefused(message, argv);

    [Theory]
    [InlineData("--json", "status", "--help")]   // l'aide passe avant tout, où qu'elle soit
    [InlineData("status", "--jsn", "-h")]
    public void Help_comes_first_wherever_it_is(params string[] argv) => Assert.Equal(0, Run(argv).Code);

    [Theory]
    [InlineData("status", "--json")]
    public void The_options_of_each_command_reach_the_configuration(params string[] argv)
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
        // du message .NET « Could not find file ». status lit le prompt même sans index.
        using var ai = new FakeAiService();
        var (code, output, errors) = Run("status", "--prompt", "nexiste", "--config", WriteConfig(ai.Url));
        Assert.Equal((1, ""), (code, output));
        Assert.Equal($"Erreur : prompt introuvable : nexiste dans {Composition.PromptsDir} (connus : answer, answer-v2)\n", errors);
    }

    [Fact]
    public void An_empty_value_is_no_value()
    {
        // Donnés vides, --config, --embedding-model, --generation-model et --prompt valent la configuration, comme si
        // l'option était absente : status affiche alors exactement le même état. ASSISTANT_CONFIG vaut pour tout le
        // processus, d'où la collection « Console », qui ne tourne en même temps qu'aucune autre.
        using var ai = new FakeAiService();
        var config = WriteConfig(ai.Url);
        Assert.Equal(0, Run("index", "--config", config).Code);
        var status = Run("status", "--config", config);
        Assert.Equal(0, status.Code);
        foreach (var option in new[] { "--embedding-model", "--generation-model", "--prompt" })
        {
            Assert.Equal(status, Run("status", option, "", "--config", config));
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
}
