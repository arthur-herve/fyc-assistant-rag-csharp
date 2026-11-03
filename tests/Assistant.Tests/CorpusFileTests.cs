// Les fiches du corpus, éditées à la main : un mauvais encodage, des identifiants en double ou des fins de ligne
// inattendues se voient, avec le nom du fichier.

using System.Text;
using Assistant.Infrastructure;
using Xunit;

namespace Assistant.Tests;

public class CorpusFileTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_document_saved_in_latin_1_is_refused_with_its_name(bool bom)
    {
        // Sans ce refus, « Congés » deviendrait « Cong�s » dans l'index. La position se compte après la marque
        // d'ordre des octets.
        using var dir = new TempDir();
        const string content = "---\nid: conges\ngroupes: tous\n---\nCongés payés.";
        var bytes = Encoding.Latin1.GetBytes(content);
        File.WriteAllBytes(dir.File("conges.md"), bom ? Bytes.WithBom(bytes) : bytes);
        var error = Assert.Throws<CorpusFormatException>(() => new MarkdownCorpus(dir.Path).Load());
        Assert.Equal($"{dir.File("conges.md")} : pas en UTF-8 (octet 0xe9 à la position {content.IndexOf('é')})", error.Message);
    }

    [Fact]
    public void A_file_with_a_byte_order_mark_loads()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("a.md"), Bytes.WithBom(Encoding.UTF8.GetBytes("---\nid: a\ngroupes: tous\n---\nTexte.")));
        Assert.Equal("a", Assert.Single(new MarkdownCorpus(dir.Path).Load()).Id);
    }

    [Fact]
    public void Invalid_groups_and_duplicate_ids_are_listed_in_code_point_order()
    {
        // Les listes du message s'écrivent [a, b] et sont triées par point de code : « B » passe avant « b ».
        var error = Assert.Throws<CorpusFormatException>(() => MarkdownCorpus.Parse("---\nid: a\ngroupes: tous\u001c, rh #x\n---\nTexte."));
        Assert.Equal("<texte> : groupe(s) invalide(s) [rh #x, tous\u001c] : des noms en minuscules séparés par des virgules, sans commentaire",
                     error.Message);
        using var dir = new TempDir();
        var ids = new[] { "b", "B", "b", "B", "c" };
        for (var n = 0; n < ids.Length; n++)
        {
            File.WriteAllText(dir.File($"{n}.md"), $"---\nid: {ids[n]}\ngroupes: tous\n---\nTexte.");
        }
        error = Assert.Throws<CorpusFormatException>(() => new MarkdownCorpus(dir.Path).Load());
        Assert.Equal("Identifiants de documents en double : [B, b]", error.Message);
    }

    [Theory]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    [InlineData("\f")]
    [InlineData("\v")]
    [InlineData("\u0085")]
    [InlineData("\u001c")]
    [InlineData("\u001d")]
    [InlineData("\u001e")]
    public void Only_lf_and_crlf_end_a_line_the_rest_stays_in_the_text(string separator) =>
        // Le texte lu est celui qu'on indexe. Aucun autre séparateur ne doit y devenir un saut de ligne.
        Assert.Equal($"Un.{separator}Deux.", MarkdownCorpus.Parse($"---\nid: a\ngroupes: tous\n---\nUn.{separator}Deux.").Text);

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void A_line_ends_with_lf_or_crlf(string newline)
    {
        var doc = MarkdownCorpus.Parse(string.Join(newline, "---", "id: a", "groupes: tous", "---", "Un.", "Deux."));
        Assert.Equal(("a", "Un.\nDeux."), (doc.Id, doc.Text));
    }
}
