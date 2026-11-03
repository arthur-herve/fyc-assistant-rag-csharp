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

    /// <summary>
    /// Des sorties inhabituelles du modèle, avec les citations valides et invalides attendues pour 2 passages. Aucune
    /// ne doit lever d'exception.
    /// </summary>
    public static TheoryData<string, int[], int[]> Unusual => new()
    {
        { "Deux jours [1\u001c, 2].", Array.Empty<int>(), Array.Empty<int>() },   // \x1c à \x1f : pas des blancs pour \s
        { "[1\u001f,2]", Array.Empty<int>(), Array.Empty<int>() },
        { "[1,\u001e2]", Array.Empty<int>(), Array.Empty<int>() },
        { "[1,\u00a02]", new[] { 1, 2 }, Array.Empty<int>() },   // espace insécable, U+2028, \x85 : des blancs pour \s
        { "[1,\u20282]", new[] { 1, 2 }, Array.Empty<int>() },
        { "[1\u0085, 2]", new[] { 1, 2 }, Array.Empty<int>() },
        { "[33612345678] puis [44612345678]", Array.Empty<int>(), new[] { int.MaxValue } },   // trop grands : int.MaxValue
        { "[2147483648]", Array.Empty<int>(), new[] { int.MaxValue } },
        { "[1234567890]", Array.Empty<int>(), new[] { 1234567890 } },   // dix chiffres sous la borne : le nombre lui-même
        { "[" + new string('9', 5000) + "]", Array.Empty<int>(), new[] { int.MaxValue } },
        { "[00000000002]", new[] { 2 }, Array.Empty<int>() },
        { "[1, 00000000002]", new[] { 1, 2 }, Array.Empty<int>() },   // un blanc puis onze chiffres : rogné, puis lu comme 2
        { "[0]", Array.Empty<int>(), new[] { 0 } },
        { "[\u0661]", Array.Empty<int>(), Array.Empty<int>() },
    };

    [Theory]
    [MemberData(nameof(Unusual))]
    public void Unusual_outputs_give_their_citations_never_an_exception(string text, int[] cited, int[] invalid)
    {
        var check = Citations.Check(text, 2);
        Assert.Equal(cited, check.Cited);
        Assert.Equal(invalid, check.Invalid);
    }
}
