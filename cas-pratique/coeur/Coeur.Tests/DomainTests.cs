using Coeur;
using Xunit;

namespace Coeur.Tests;

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
