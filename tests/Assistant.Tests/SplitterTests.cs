// Le découpage des documents en morceaux.

using Assistant.Infrastructure;
using Xunit;

namespace Assistant.Tests;

public class SplitterTests
{
    [Fact]
    public void Short_document_gives_one_chunk_with_title()
    {
        var chunks = new ParagraphSplitter().Split(Fakes.Doc("a", "Un paragraphe court."));
        Assert.Single(chunks);
        Assert.StartsWith("A\n", chunks[0].Text);
    }

    [Fact]
    public void A_chunk_id_is_the_document_id_and_its_rank() =>
        Assert.Equal(new[] { "a#0", "a#1" }, new ParagraphSplitter(100, 0, includeTitle: false)
            .Split(Fakes.Doc("a", new string('x', 60) + "\n\n" + new string('y', 60))).Select(c => c.Id));

    [Fact]
    public void Long_document_is_cut_with_overlap()
    {
        // Des paragraphes tous différents : le recouvrement ne peut pas être trouvé par hasard.
        var text = string.Join("\n\n", Enumerable.Range(1, 12).Select(i =>
            $"Paragraphe numéro {i} : " + string.Join(" ", Enumerable.Range(1, 25).Select(j => $"mot{i}x{j}"))));
        var chunks = new ParagraphSplitter(400, 60, includeTitle: false).Split(Fakes.Doc("a", text));
        Assert.True(chunks.Count > 3);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 400 + 60));
        var lastWord = chunks[0].Text.TrimEnd().Split(' ')[^1];        // mot unique du corpus
        Assert.Contains(lastWord, chunks[1].Text[..Math.Min(100, chunks[1].Text.Length)]);   // recouvrement
    }

    [Fact]
    public void Describe_gives_the_type_and_the_max_chars_of_the_splitter()
    {
        var describe = new ParagraphSplitter(300, 50).Describe();
        Assert.Equal(300, describe["max_chars"]);
        Assert.Equal("paragraph", describe["type"]);
    }

    [Fact]
    public void The_splitter_turns_crlf_into_lf_and_keeps_a_lone_cr()
    {
        // Les lecteurs de corpus ramènent déjà les fins de ligne à « \n », mais un Document construit dans le code peut
        // garder ses « \r\n » : le découpage les convertit donc aussi. Un « \r » seul reste tel quel.
        static IEnumerable<string> Texts(string text) =>
            new ParagraphSplitter(100, 0, includeTitle: false).Split(Fakes.Doc("a", text)).Select(c => c.Text);

        Assert.Equal(new[] { "Un.\nDeux.\n\nTrois.\rQuatre." }, Texts("Un.\r\nDeux.\r\n\r\nTrois.\rQuatre."));
        // 40 lignes d'un caractère font 79 caractères avec « \n », sous la limite de 98, mais 118 avec « \r\n ».
        var lines = Enumerable.Repeat("x", 40).ToList();
        Assert.Equal(new[] { string.Join("\n", lines) }, Texts(string.Join("\r\n", lines)));
    }

    [Fact]
    public void An_emoji_at_a_boundary_is_never_cut_in_two()
    {
        // Les longueurs sont des string.Length : un emoji y compte pour deux unités UTF-16 (une paire de substitution).
        // À la limite d'un morceau comme au début du recouvrement, la coupure passe avant ou après lui, jamais entre ses
        // deux moitiés, sinon le texte ne serait plus de l'Unicode valide.
        const string emoji = "\U0001F600";
        static string[] Texts(string text, int overlap) =>
            new ParagraphSplitter(100, overlap, includeTitle: false).Split(Fakes.Doc("a", text)).Select(c => c.Text).ToArray();

        // Sans espace, un paragraphe de plus de 98 unités est coupé après la 98e : l'emoji occupe les 98e et 99e.
        Assert.Equal(new[] { new string('x', 97), emoji + new string('y', 10) },
                     Texts(new string('x', 97) + emoji + new string('y', 10), 0));
        // Le recouvrement reprend les 20 dernières unités du morceau : la première est la seconde moitié de l'emoji.
        var first = new string('a', 50) + emoji + new string('b', 19);
        Assert.Equal(new[] { first, new string('b', 19) + "\n\n" + new string('c', 50) },
                     Texts(first + "\n\n" + new string('c', 50), 20));
    }
}
