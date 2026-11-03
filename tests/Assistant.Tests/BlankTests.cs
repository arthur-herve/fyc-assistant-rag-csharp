// Les blancs aux bords des fiches et entre les paragraphes, pour le corpus et pour le découpage.

using Assistant.Infrastructure;
using Xunit;

namespace Assistant.Tests;

/// <summary>
/// Le corpus et le découpage prennent pour blancs ceux de char.IsWhiteSpace, les mêmes que Trim() et \s. Les
/// blancs Unicode comme U+2028 ou U+3000 sont donc retirés aux bords d'une fiche, et une ligne faite seulement de
/// blancs sépare deux paragraphes. Les séparateurs \x1c à \x1f, eux, restent dans le texte.
/// </summary>
public class BlankTests
{
    [Theory]
    [InlineData("\u001c")]
    [InlineData("\u001d")]
    [InlineData("\u001e")]
    [InlineData("\u001f")]
    public void Separators_x1c_to_x1f_at_the_edges_stay_in_the_text(string separator)
    {
        var doc = MarkdownCorpus.Parse($"---\nid: a{separator}\ngroupes: tous\n---\n{separator}Un.{separator}");
        Assert.Equal(($"a{separator}", $"{separator}Un.{separator}"), (doc.Id, doc.Text));
        // Pour la même raison, « ---\x1c » n'est pas la ligne d'en-tête.
        Assert.Throws<CorpusFormatException>(() => MarkdownCorpus.Parse($"---{separator}\nid: a\ngroupes: tous\n---\nUn."));
    }

    [Fact]
    public void Unicode_blanks_at_the_edges_are_trimmed()
    {
        var doc = MarkdownCorpus.Parse("---\nid: a\u2028\ngroupes: tous\u3000\n---\n\fUn.\u2029");
        Assert.Equal(("a", "Un.", "tous"), (doc.Id, doc.Text, Assert.Single(doc.AllowedGroups)));
    }

    [Fact]
    public void The_splitter_cuts_paragraphs_on_white_space_lines_and_keeps_the_separators()
    {
        static IEnumerable<string> Texts(string text) =>
            new ParagraphSplitter(100, 0, includeTitle: false).Split(Fakes.Doc("a", text)).Select(c => c.Text);

        // Les deux paragraphes tiennent dans un seul morceau, séparés par une ligne vide.
        Assert.Equal(new[] { "Un.\n\nDeux." }, Texts("Un.\n \u2028\nDeux."));
        Assert.Equal(new[] { "Un.\n\u001c\nDeux." }, Texts("Un.\n\u001c\nDeux."));   // pas une ligne blanche pour \s
        Assert.Equal(new[] { "\u001dUn.\u001d" }, Texts("\u001dUn.\u001d"));
        Assert.Equal(new[] { "\u001c" }, Texts("\u001c"));
        // Un paragraphe trop long, coupé à l'espace : \x1c reste de part et d'autre de la coupure.
        Assert.Equal(new[] { new string('x', 60) + "\u001c", "\u001c" + new string('y', 60) },
                     Texts(new string('x', 60) + "\u001c \u001c" + new string('y', 60)));
    }
}
