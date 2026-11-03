using Assistant.Domain;
using Xunit;

namespace Assistant.Tests;

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
    public void The_length_is_counted_in_code_points()
    {
        // La longueur se compte en points de code, avec EnumerateRunes. Un emoji fait deux unités UTF-16 pour
        // string.Length mais un seul caractère ici, et une moitié de paire isolée compte aussi pour un. Ce ne
        // sont pas des graphèmes : un « é » décomposé ou un drapeau comptent pour deux.
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
        };
        foreach (var (text, problems) in cases)
        {
            Assert.Equal(problems, OutputRules.Check(text, maxChars: 500).Problems);
        }
    }

    [Fact]
    public void The_rules_see_unicode_blanks_not_the_separators_x1c_to_x1f()
    {
        // Le rognage par Trim() et les marqueurs de raisonnement, écrits avec \s, voient les mêmes blancs : ceux de
        // char.IsWhiteSpace. Les séparateurs \x1c à \x1f n'en font pas partie.
        static string[] Reasoning(string found) => new[] { $"raisonnement du modèle déversé dans la réponse (« {found} »)" };
        var tooLong = new[] { "réponse trop longue (1501 caractères, 1500 au plus)" };
        var plane = Enumerable.Range(0, 0x10000).Select(c => (char)c).ToList();
        var blanks = plane.Where(char.IsWhiteSpace).ToList();
        // Sur tout le plan de base, ce sont exactement ces blancs qui sont rognés ou reconnus entre « ok, » et
        // « let ». U+200B, par exemple, n'en fait pas partie.
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
        foreach (var separator in "\u001c\u001d\u001e\u001f")   // ce ne sont pas des blancs pour char.IsWhiteSpace
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
