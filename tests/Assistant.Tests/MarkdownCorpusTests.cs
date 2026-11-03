// L'adaptateur du corpus : l'en-tête des fiches Markdown et leurs droits d'accès, puis les corpus livrés.

using Assistant.Cli;
using Assistant.Infrastructure;
using Xunit;

namespace Assistant.Tests;

public class MarkdownCorpusTests
{
    [Fact]
    public void Parses_header_and_groups()
    {
        var doc = MarkdownCorpus.Parse("---\nid: rh-1\ntitre: Grille des salaires\ngroupes: rh, direction\n---\n# Grille\nTexte.");
        Assert.Equal("rh-1", doc.Id);
        Assert.Equal("Grille des salaires", doc.Title);
        Assert.Equal(new HashSet<string> { "rh", "direction" }, doc.AllowedGroups);
        Assert.StartsWith("# Grille", doc.Text);
    }

    [Theory]
    [InlineData("id: x")]
    [InlineData("id: x\ngroupes:")]
    [InlineData("id: x\ngroupe: rh")]
    public void Access_groups_are_mandatory(string header) =>
        // Un droit oublié ou mal écrit ne rend jamais un document public en silence.
        Assert.Throws<CorpusFormatException>(() => MarkdownCorpus.Parse($"---\n{header}\n---\nTexte."));

    [Fact]
    public void A_comment_in_the_groups_is_rejected() =>
        // Lu tel quel, ce commentaire ouvrirait le document à « direction » et le fermerait à « rh » : de faux droits.
        Assert.Throws<CorpusFormatException>(() => MarkdownCorpus.Parse("---\nid: x\ngroupes: rh  # ou : rh, direction\n---\nTexte."));

    [Fact]
    public void Id_is_mandatory() =>
        Assert.Throws<CorpusFormatException>(() => MarkdownCorpus.Parse("---\ntitre: Sans id\n---\nTexte."));

    [Fact]
    public void Project_corpora_load()
    {
        var root = AppConfig.ProjectRoot;
        Assert.True(new MarkdownCorpus(Path.Combine(root, "corpus", "solveo")).Load().Count >= 5);
        var real = new MarkdownCorpus(Path.Combine(root, "corpus", "service-public")).Load();
        Assert.True(real.Count >= 300);
        var course = new MarkdownCorpus(Path.Combine(root, "corpus", "service-public-reduit")).Load();
        Assert.Equal(50, course.Count);
    }

    [Fact]
    public void The_service_public_corpora_use_the_groups_tous_rh_and_direction()
    {
        var root = AppConfig.ProjectRoot;
        var real = new MarkdownCorpus(Path.Combine(root, "corpus", "service-public")).Load();
        Assert.Equal(new HashSet<string> { "tous", "rh", "direction" }, real.SelectMany(d => d.AllowedGroups).ToHashSet());
        var course = new MarkdownCorpus(Path.Combine(root, "corpus", "service-public-reduit")).Load();
        Assert.Equal(new HashSet<string> { "tous", "rh", "direction" }, course.SelectMany(d => d.AllowedGroups).ToHashSet());
    }
}
