// Ce que la ligne de commande affiche doit se lire : une réponse et sa trace, en texte ou en JSON.

using System.Text.Json.Nodes;
using Assistant.Cli;
using Assistant.Domain;
using Xunit;

namespace Assistant.Tests;

/// <summary>Des affichages qui se lisent : une sortie brute par ligne, un JSON indenté qui garde les accents.</summary>
public sealed class ReadableDisplayTests
{
    [Fact]
    public void The_verbose_answer()
    {
        // Chaque sortie brute s'écrit en chaîne JSON, entre guillemets, sur une seule ligne grâce aux sauts de ligne
        // échappés. Relue, elle redonne la sortie telle quelle, y compris espaces insécables, emoji et caractères de
        // contrôle.
        const string raw = "Deux\u00a0jours\u202f! \U0001F600\u2028\u001b\u007f \"q\" \\ \n\t";
        var trace = new AnswerTrace(new[] { new Retrieved("teletravail#0", 0.5276), new Retrieved("conges#0", 0.0) }, 2.0,
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

    /// <summary>Une réponse aux textes délicats de Bytes.Special, qui cite un document au titre accentué.</summary>
    private static readonly Answer Sample = new("Combien ?", AnswerStatus.Answered, Bytes.Special, new[] { new Source(1, "a", "Congés", "a#0") },
        new AnswerTrace(new[] { new Retrieved("a#0", 1.0), new Retrieved("a#1", 0.25) },
                        0.4, Array.Empty<string>()));

    [Fact]
    public void The_json_of_an_answer_is_indented_and_reads_back_the_same()
    {
        // ask --json écrit un JSON indenté. Relu, il redonne les mêmes valeurs, y compris le texte avec son espace
        // insécable, ses guillemets, son saut de ligne et son emoji.
        var written = Presenter.ToJson(Presenter.AnswerToJson(Sample));
        Assert.StartsWith("{" + Environment.NewLine + "  \"question\": \"Combien ?\",", written);
        Assert.True(JsonNode.DeepEquals(Presenter.AnswerToJson(Sample), JsonNode.Parse(written)));
    }

    [Fact]
    public void The_json_of_an_answer_keeps_accents_as_they_are() =>
        Assert.Contains("\"document_title\": \"Congés\"", Presenter.ToJson(Presenter.AnswerToJson(Sample)));
}
