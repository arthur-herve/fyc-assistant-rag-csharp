// La commande index et son option --if-stale. Les règles communes de l'analyse sont vérifiées dans CliTests.cs.

using Xunit;

namespace Assistant.Tests;

[Collection("Console")]
public sealed class CliIndexTests : CliTestBase
{
    [Theory]
    [InlineData("option(s) inconnue(s) pour index : -if-stale", "index", "-if-stale")]
    public void A_mistyped_abbreviated_or_foreign_option_is_refused_before_any_work(string message, params string[] argv) =>
        AssertRefused(message, argv);

    [Theory]
    [InlineData("index", "--if-stale", "-v")]
    public void The_options_of_each_command_reach_the_configuration(params string[] argv)
    {
        var (code, _, errors) = Run(argv.Append("--config").Append(_missing).ToArray());
        Assert.Equal(1, code);
        Assert.Contains("absente.json", errors);   // la saisie est acceptée : c'est le fichier qui manque
        Assert.DoesNotContain("inconnue", errors);
    }
}
