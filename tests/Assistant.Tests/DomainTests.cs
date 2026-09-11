using Assistant.Domain;
using Xunit;

namespace Assistant.Tests;

public class AccessPolicyTests
{
    private static Chunk Chunk(params string[] groups) => new("d#0", "d", "D", "texte", 0, groups.ToHashSet());

    [Fact]
    public void Public_document_is_readable_by_everyone() =>
        Assert.True(new AccessPolicy().CanRead(new User("x", new HashSet<string>()), Chunk("tous")));

    [Fact]
    public void Restricted_document_requires_a_shared_group()
    {
        var restricted = Chunk("rh", "direction");
        Assert.False(new AccessPolicy().CanRead(Fakes.Alice, restricted));
        Assert.True(new AccessPolicy().CanRead(Fakes.Bruno, restricted));
    }
}

public class CitationsTests
{
    [Fact]
    public void Valid_citations()
    {
        var check = Citations.Check("Deux jours [1]. Indemnité de 30 euros [2, 3].", passageCount: 3);
        Assert.Equal(new[] { 1, 2, 3 }, check.Cited);
        Assert.True(check.IsValid);
    }

    [Fact]
    public void No_citation_is_invalid() => Assert.False(Citations.Check("Deux jours par semaine.", 3).IsValid);

    [Fact]
    public void Citation_to_a_passage_that_was_not_provided_is_invalid()
    {
        var check = Citations.Check("Deux jours [1] et [7].", passageCount: 2);
        Assert.Equal(new[] { 7 }, check.Invalid);
        Assert.False(check.IsValid);
    }

    [Fact]
    public void Duplicates_are_counted_once() => Assert.Equal(new[] { 2, 1 }, Citations.Check("[2] puis [2,1]", 2).Cited);
}

public class OutputRulesTests
{
    private const string Leak = "Okay, let's see. The user is asking how many days of remote work per week. "
                              + "First, I need to check the provided passages. Passage [1] says two days per week.";

    [Fact]
    public void Short_french_answer_with_a_citation_is_valid() =>
        Assert.True(OutputRules.Check("Vous pouvez télétravailler deux jours par semaine [1].").IsValid);

    [Fact]
    public void Empty_output_is_rejected() => Assert.Equal(new[] { "réponse vide" }, OutputRules.Check("   ").Problems);

    [Fact]
    public void Leaked_reasoning_is_rejected_even_with_a_citation()
    {
        var check = OutputRules.Check(Leak);
        Assert.False(check.IsValid);
        Assert.Contains(check.Problems, p => p.Contains("raisonnement"));
        Assert.Contains(check.Problems, p => p.Contains("langue"));
    }

    [Fact]
    public void English_answer_is_rejected() =>
        Assert.Contains("réponse dans une autre langue que le français",
                        OutputRules.Check("The employee is allowed to work from home two days a week and the manager agrees [1].").Problems);

    [Fact]
    public void French_answer_with_a_few_english_words_is_accepted() =>
        Assert.True(OutputRules.Check("Le salarié peut demander un « time off » ; the request is faite auprès du manager, dans les délais du passage [1].").IsValid);

    [Theory]
    [InlineData("Le billet me semble clair : 25 jours calendaires de congé [1].")]
    [InlineData("Passage [1] et passage [2] répondent à cette question : oui, sous conditions.")]
    public void French_words_that_look_like_reasoning_markers_are_accepted(string text) =>
        Assert.True(OutputRules.Check(text).IsValid, string.Join(" ; ", OutputRules.Check(text).Problems));

    [Fact]
    public void Short_english_answer_is_rejected() => Assert.False(OutputRules.Check("The answer is two days [1].").IsValid);

    [Fact]
    public void Too_long_output_is_rejected() =>
        Assert.Contains(OutputRules.Check(string.Concat(Enumerable.Repeat("Le salarié a droit à des congés. ", 60)), maxChars: 500).Problems,
                        p => p.Contains("trop longue"));

    [Fact]
    public void Think_tags_are_rejected() => Assert.False(OutputRules.Check("<think>je réfléchis</think> Deux jours [1].").IsValid);
}
