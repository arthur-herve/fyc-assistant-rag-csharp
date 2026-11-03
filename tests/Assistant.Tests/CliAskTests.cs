// La commande ask, lancée pour de bon contre le faux service IA, jusqu'à la réponse. Les options communes données
// vides y sont vérifiées aussi, puisqu'il faut une réponse pour les comparer.

using System.Text.Json.Nodes;
using Xunit;

namespace Assistant.Tests;

[Collection("Console")]
public sealed class CliAskTests : CliTestBase
{
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
        // U+001C n'est pas un blanc pour Trim() : la question est posée, alors qu'une question faite de blancs est
        // refusée.
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

    [Fact]
    public void An_empty_value_is_no_value()
    {
        // Donnés vides, --config, --embedding-model et --generation-model valent la configuration, comme si l'option
        // était absente. Pris pour un nom, ils donneraient un modèle « » que le faux service servirait comme « faux: »,
        // ou, pour --config, un message .NET en anglais (« The value cannot be an empty string »). ASSISTANT_CONFIG
        // vaut pour tout le processus, d'où la collection « Console », qui ne tourne en même temps qu'aucune autre.
        using var ai = new FakeAiService();
        var config = WriteConfig(ai.Url);
        const string question = "Combien de jours de télétravail par semaine ?";
        Assert.Equal(0, Run("index", "--config", config).Code);
        var ask = Run("ask", question, "--config", config);
        Assert.Equal(0, ask.Code);
        foreach (var option in new[] { "--embedding-model", "--generation-model" })
        {
            Assert.Equal(ask, Run("ask", question, option, "", "--config", config));
        }
        var previousConfig = Environment.GetEnvironmentVariable("ASSISTANT_CONFIG");
        Environment.SetEnvironmentVariable("ASSISTANT_CONFIG", config);
        try
        {
            Assert.Equal(ask, Run("ask", question, "--config", ""));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASSISTANT_CONFIG", previousConfig);
        }
    }
}
