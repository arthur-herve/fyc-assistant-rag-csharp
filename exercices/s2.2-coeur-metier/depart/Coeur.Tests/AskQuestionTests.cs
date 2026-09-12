// Les 13 tests qui décrivent AskQuestion : aucun modèle, aucun réseau. À faire passer un par un, dans l'ordre.

using System.Text.RegularExpressions;
using Coeur;
using Xunit;

namespace Coeur.Tests;

public class AskQuestionTests
{
    [Fact]
    public void Answers_with_cited_sources()
    {
        var generator = new ScriptedGenerator("Deux jours par semaine [1].");
        var answer = Build.Ask(generator).Execute(Fakes.Alice, "Combien de jours de télétravail ?");
        Assert.Equal(AnswerStatus.Answered, answer.Status);
        Assert.Equal(new[] { "teletravail" }, answer.Sources.Select(s => s.DocumentId));
        Assert.Equal("fake-llm", answer.Trace.GenerationModel);
        Assert.Equal("test-v1", answer.Trace.PromptVersion);
    }

    [Fact]
    public void Does_not_call_the_model_without_relevant_passage()
    {
        var generator = new ScriptedGenerator("ne devrait pas être appelé [1]");
        var answer = Build.Ask(generator).Execute(Fakes.Alice, "Quelle est la capitale de l'Australie ?");
        Assert.Equal(AnswerStatus.NoRelevantSource, answer.Status);
        Assert.Empty(generator.Requests);
    }

    [Fact]
    public void Restricted_passages_never_reach_the_prompt()
    {
        var generator = new ScriptedGenerator("[1]");
        Build.Ask(generator, minScore: 0.0).Execute(Fakes.Alice, "Quel salaire pour un senior ?");
        Assert.NotEmpty(generator.Requests);
        Assert.All(generator.Requests, r => Assert.DoesNotContain("56 000", r.Prompt));
    }

    [Fact]
    public void A_huge_or_non_ascii_citation_number_is_invalid_not_a_crash()
    {
        var answer = Build.Ask(new ScriptedGenerator("Appelez le [33612345678].", "Deux jours [1].")).Execute(Fakes.Alice, "Combien de jours de télétravail ?");
        Assert.Equal(AnswerStatus.Answered, answer.Status);
        Assert.Equal(2, answer.Trace.Attempts);
    }

    [Fact]
    public void A_passage_containing_the_placeholder_is_not_substituted_twice()
    {
        var template = new PromptTemplate("t", "v", "s", "P: {passages} Q: {question}");
        Assert.Equal("P: texte {question} Q: réelle ?", template.Render("réelle ?", "texte {question}"));
    }

    [Fact]
    public void Authorized_user_gets_restricted_passages()
    {
        var answer = Build.Ask(new ScriptedGenerator("Entre 56 000 et 68 000 euros [1].")).Execute(Fakes.Bruno, "Quel salaire pour un senior ?");
        Assert.Equal(new[] { "grille" }, answer.Sources.Select(s => s.DocumentId));
    }

    [Fact]
    public void Retries_when_the_model_forgets_to_cite()
    {
        var answer = Build.Ask(new ScriptedGenerator("Deux jours.", "Deux jours [1].")).Execute(Fakes.Alice, "Combien de jours de télétravail ?");
        Assert.Equal(AnswerStatus.Answered, answer.Status);
        Assert.Equal(2, answer.Trace.Attempts);
        Assert.Equal(2, answer.Trace.RawOutputs.Count);
    }

    [Fact]
    public void Refuses_an_unsourced_answer_after_all_attempts()
    {
        var answer = Build.Ask(new ScriptedGenerator("Deux jours.", "Deux jours [9].")).Execute(Fakes.Alice, "Combien de jours de télétravail ?");
        Assert.Equal(AnswerStatus.Unsourced, answer.Status);
        Assert.DoesNotContain("Deux jours", answer.Text);   // la sortie non sourcée n'est pas montrée
        Assert.NotEmpty(answer.Sources);                     // mais les passages le sont
    }

    [Fact]
    public void Passages_are_numbered_in_the_prompt()
    {
        var generator = new ScriptedGenerator("[1]");
        Build.Ask(generator, minScore: 0.0).Execute(Fakes.Alice, "télétravail et frais de repas");
        Assert.Equal(2, Regex.Matches(generator.Requests[0].Prompt, @"^\[\d+\]", RegexOptions.Multiline).Count);
    }

    [Fact]
    public void Changing_the_embedding_model_without_reindexing_is_refused()
    {
        var index = Build.Indexed(new KeywordEmbedder("modele-a"));
        var ask = Build.Ask(new ScriptedGenerator("[1]"), index, new KeywordEmbedder("modele-b"));
        Assert.Throws<IndexModelMismatchException>(() => ask.Execute(Fakes.Alice, "télétravail"));
    }

    [Fact]
    public void Changing_the_generation_model_needs_no_reindexing()
    {
        var index = Build.Indexed();
        foreach (var model in new[] { "llm-a", "llm-b" })
        {
            var answer = Build.Ask(ScriptedGenerator.WithModel(model, "Deux jours [1]."), index).Execute(Fakes.Alice, "jours de télétravail");
            Assert.Equal(model, answer.Trace.GenerationModel);
        }
    }

    [Fact]
    public void Empty_question_is_rejected() =>
        Assert.Throws<EmptyQuestionException>(() => Build.Ask(new ScriptedGenerator("[1]")).Execute(Fakes.Alice, "   "));

    [Fact]
    public void Asking_before_indexing_is_explicit() =>
        Assert.Throws<IndexNotBuiltException>(() => Build.Ask(new ScriptedGenerator("[1]"), new FakeIndex()).Execute(Fakes.Alice, "télétravail"));

    [Fact]
    public void Seed_changes_between_attempts()
    {
        var generator = new ScriptedGenerator("sans citation", "avec [1]");
        Build.Ask(generator, seed: 42).Execute(Fakes.Alice, "jours de télétravail");
        Assert.Equal(new int?[] { 42, 43 }, generator.Requests.Select(r => r.Seed));
    }
}
