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
    /// (sortie du modèle, citations valides, citations invalides) pour 2 passages : des sorties inhabituelles, jamais une
    /// exception.
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
    public void The_length_is_counted_in_code_points_like_in_python()
    {
        // Un emoji tient en deux unités UTF-16 (string.Length) mais compte pour un caractère : la longueur se compte en
        // points de code (EnumerateRunes). Une moitié de paire isolée compte pour un caractère. Ce sont des points de
        // code, pas des caractères perçus (graphèmes) : un « é » décomposé ou un drapeau en comptent deux. Et c'est la
        // longueur de la réponse rognée.
        static string Times(string text, int count) => string.Concat(Enumerable.Repeat(text, count));
        static string[] TooLong(int length) => new[] { $"réponse trop longue ({length} caractères, 500 au plus)" };
        var cases = new (string Text, string[] Problems)[]
        {
            (Times("\U0001F600", 500), Array.Empty<string>()),   // 1 000 unités UTF-16
            (Times("\U0001F600", 501), TooLong(501)),
            (Times("\ud800", 501), TooLong(501)),
            (Times("\udc00", 501), TooLong(501)),
            (Times("e\u0301", 251), TooLong(502)),   // « é » décomposé : 251 graphèmes, 502 points de code
            (Times("\U0001F1EB\U0001F1F7", 251), TooLong(502)),   // drapeau : 251 graphèmes, 502 points de code
            ("  " + Times("a", 500) + "\n", Array.Empty<string>()),   // 503 avant rognage, 500 après
        };
        foreach (var (text, problems) in cases)
        {
            Assert.Equal(problems, OutputRules.Check(text, maxChars: 500).Problems);
        }
    }

    [Fact]
    public void The_rules_see_unicode_blanks_not_the_separators_x1c_to_x1f()
    {
        // Le rognage (Trim()) et les marqueurs de raisonnement (\s) voient les mêmes blancs, ceux de char.IsWhiteSpace :
        // les séparateurs \x1c à \x1f n'en sont pas.
        static string[] Reasoning(string found) => new[] { $"raisonnement du modèle déversé dans la réponse (« {found} »)" };
        var tooLong = new[] { "réponse trop longue (1501 caractères, 1500 au plus)" };
        var plane = Enumerable.Range(0, 0x10000).Select(c => (char)c).ToList();
        var blanks = plane.Where(char.IsWhiteSpace).ToList();
        // Rognés, ou entre « ok, » et « let » : ces blancs, ni plus ni moins (pas U+200B, par exemple).
        var empty = new[] { "réponse vide" };
        Assert.Equal(blanks, plane.Where(c => OutputRules.Check(new string(c, 3)).Problems.SequenceEqual(empty)));
        Assert.Equal(blanks, plane.Where(c => OutputRules.Check($"ok,{c}let").Problems.Count > 0));
        foreach (var blank in blanks)
        {
            Assert.Empty(OutputRules.Check(blank + new string('a', 1500) + blank).Problems);
            Assert.Equal(Reasoning($"ok,{blank}let"), OutputRules.Check($"ok,{blank}let").Problems);
            Assert.Equal(Reasoning($"first,{blank}i need"), OutputRules.Check($"first,{blank}i need").Problems);
            Assert.Equal(Reasoning("wait,"), OutputRules.Check($"wait,{blank}x").Problems);
        }
        foreach (var separator in "\u001c\u001d\u001e\u001f")   // pas des blancs (char.IsWhiteSpace dit non)
        {
            Assert.Empty(OutputRules.Check(new string(separator, 3)).Problems);
            Assert.Equal(tooLong, OutputRules.Check(separator + new string('a', 1500)).Problems);
            Assert.Equal(tooLong, OutputRules.Check(new string('a', 1500) + separator).Problems);
            foreach (var text in new[] { $"ok,{separator}let", $"first,{separator}i need", $"wait,{separator}x" })
            {
                Assert.Empty(OutputRules.Check(text).Problems);
            }
        }
    }

    [Fact]
    public void Think_tags_are_rejected() => Assert.False(OutputRules.Check("<think>je réfléchis</think> Deux jours [1].").IsValid);
}
