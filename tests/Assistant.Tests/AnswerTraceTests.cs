// La trace d'une réponse explique ce qui s'est passé, et celle d'un refus aussi.

using Assistant.Domain;
using Xunit;

namespace Assistant.Tests;

public class AnswerTraceTests
{
    [Fact]
    public void A_refusal_without_relevant_source_keeps_the_passages_and_their_scores()
    {
        // La question partage un mot, « jours », avec la fiche du télétravail : c'est trop peu pour le seuil de 0,5. Le
        // modèle n'est pas appelé, mais la trace garde ce que la recherche a trouvé. On peut ainsi dire si le seuil
        // était trop haut ou si le corpus n'a pas la réponse.
        var answer = Build.Ask(new ScriptedGenerator("ne devrait pas être appelé [1]"))
            .Execute(Fakes.Alice, "Combien de jours de congés en Australie ?");

        Assert.Equal(AnswerStatus.NoRelevantSource, answer.Status);
        var trace = answer.Trace;
        Assert.Equal(new[] { "teletravail#0", "frais#0" }, trace.Retrieved.Select(r => r.ChunkId));
        Assert.Equal(1 / Math.Sqrt(6), trace.Retrieved[0].Score, precision: 4);   // un mot commun aux trois de la question et aux deux de la fiche
        Assert.Equal(0.0, trace.Retrieved[1].Score, precision: 4);
        Assert.Empty(trace.RawOutputs);
    }

    [Fact]
    public void A_refusal_without_relevant_source_keeps_the_threshold()
    {
        var answer = Build.Ask(new ScriptedGenerator("ne devrait pas être appelé [1]"))
            .Execute(Fakes.Alice, "Combien de jours de congés en Australie ?");
        Assert.Equal(AnswerStatus.NoRelevantSource, answer.Status);
        Assert.Equal(0.5, answer.Trace.MinScore);
    }
}
