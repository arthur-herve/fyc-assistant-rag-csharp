// Tests déterministes du cas d'usage AskQuestion : aucun modèle, aucun réseau.

using System.Text.RegularExpressions;
using Assistant.Application;
using Assistant.Domain;
using Xunit;

namespace Assistant.Tests;

public class AskQuestionTests
{
    [Fact]
    public void Answers_with_cited_sources()
    {
        var generator = new ScriptedGenerator("Deux jours par semaine [1].");
        var answer = Build.Ask(generator).Execute(Fakes.Alice, "Combien de jours de télétravail ?");
        Assert.Equal(AnswerStatus.Answered, answer.Status);
        Assert.Equal(new[] { "teletravail" }, answer.Sources.Select(s => s.DocumentId));
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
    public void A_citation_points_to_the_passage_it_numbers()
    {
        // [2] désigne le deuxième passage du prompt, pas le premier ni le dernier.
        var generator = new ScriptedGenerator("Le repas est remboursé [2].");
        var answer = Build.Ask(generator, minScore: 0.1).Execute(Fakes.Alice, "télétravail jours frais");
        Assert.Equal(2, Regex.Matches(generator.Requests[0].Prompt, @"^\[\d+\]", RegexOptions.Multiline).Count);
        Assert.Equal(new[] { (2, "frais") }, answer.Sources.Select(s => (s.Number, s.DocumentId)));
    }

    [Fact]
    public void A_huge_citation_number_is_invalid_not_a_crash()
    {
        var answer = Build.Ask(new ScriptedGenerator("Appelez le [33612345678].", "Deux jours [1].")).Execute(Fakes.Alice, "Combien de jours de télétravail ?");
        Assert.Equal(AnswerStatus.Answered, answer.Status);
        Assert.Equal(2, answer.Trace.Attempts);
    }

    [Fact]
    public void A_citation_in_non_ascii_digits_is_not_a_citation()
    {
        // Seul [1] compte : « [٩] » n'est pas une citation. Lu comme un numéro, il désignerait un passage qui
        // n'existe pas, et la réponse serait refusée.
        var answer = Build.Ask(new ScriptedGenerator("Deux jours [1], voir aussi [٩].")).Execute(Fakes.Alice, "Combien de jours de télétravail ?");
        Assert.Equal(AnswerStatus.Answered, answer.Status);
    }

    [Fact]
    public void A_passage_containing_the_placeholder_is_not_substituted_twice()
    {
        var template = new PromptTemplate("t", "s", "P: {passages} Q: {question}");
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
    }

    [Fact]
    public void Refusing_an_unsourced_answer_still_gives_the_passages()
    {
        var answer = Build.Ask(new ScriptedGenerator("Deux jours.")).Execute(Fakes.Alice, "Combien de jours de télétravail ?");
        Assert.Equal(AnswerStatus.Unsourced, answer.Status);
        Assert.Equal(new[] { "teletravail" }, answer.Sources.Select(s => s.DocumentId));
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

    [Fact]
    public void A_rejected_output_counts_as_a_failed_attempt()
    {
        const string leak = "Okay, let's see. The user is asking about remote work. The passage [1] says two days per week.";
        var inner = new ScriptedGenerator(leak, "Deux jours par semaine [1].");
        var answer = Build.Ask(new OutputValidatingGenerator(inner), minScore: 0.1).Execute(Fakes.Alice, "Combien de jours de télétravail ?");
        Assert.Equal(AnswerStatus.Answered, answer.Status);
        Assert.Equal(2, answer.Trace.Attempts);
        Assert.StartsWith("<rejetée : ", answer.Trace.RawOutputs[0]);
    }
}
