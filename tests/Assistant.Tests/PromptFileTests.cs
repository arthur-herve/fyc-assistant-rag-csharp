// Les fichiers de prompts, édités à la main : un mauvais encodage, une clé en double ou un champ manquant donnent
// un message qui nomme le fichier.

using System.Text;
using Assistant.Infrastructure;
using Xunit;

namespace Assistant.Tests;

public class PromptFileTests
{
    private const string Prompt = """{"version": "v1", "system": "Réponds en français.", "user": "{question}"}""";

    [Fact]
    public void A_byte_order_mark_changes_nothing()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("answer.json"), Encoding.UTF8.GetBytes(Prompt));
        var plain = new FilePromptRepository(dir.Path).Get("answer");
        File.WriteAllBytes(dir.File("answer.json"), Bytes.WithBom(Encoding.UTF8.GetBytes(Prompt)));
        Assert.Equal(plain, new FilePromptRepository(dir.Path).Get("answer"));
    }

    [Fact]
    public void A_prompt_saved_in_latin_1_names_the_file()
    {
        // Sans ce refus, « Réponds » deviendrait « R�ponds » et la version du prompt changerait sans rien dire.
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("answer.json"), Encoding.Latin1.GetBytes(Prompt));
        var error = Assert.Throws<FormatException>(() => new FilePromptRepository(dir.Path).Get("answer"));
        Assert.Equal($"prompt illisible ({dir.File("answer.json")}) : pas en UTF-8 (octet 0xe9 à la position {Prompt.IndexOf('é')})",
                     error.Message);
    }

    [Theory]
    [InlineData("""{"version": "v1", "system": "a"}""")]
    [InlineData("""{"version": null, "system": "a", "user": "{question}"}""")]
    [InlineData("""null""")]
    public void A_missing_field_says_what_to_fix(string content)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("casse.json"), content);
        var error = Assert.Throws<FormatException>(() => new FilePromptRepository(dir.Path).Get("casse"));
        Assert.Equal($"prompt illisible ({dir.File("casse.json")}) : il faut trois textes, version, system et user", error.Message);
    }

    [Fact]
    public void A_deeply_nested_prompt_is_unreadable_not_a_crash()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("answer.json"), Prompt.Replace("}\"}", "}\", \"x\": " + Bytes.Deep + "}"));
        var error = Assert.Throws<FormatException>(() => new FilePromptRepository(dir.Path).Get("answer"));
        Assert.StartsWith($"prompt illisible ({dir.File("answer.json")}) : ", error.Message);
    }

    [Fact]
    public void A_duplicate_key_is_refused()
    {
        // System.Text.Json garderait l'une des deux valeurs sans rien dire.
        using var dir = new TempDir();
        File.WriteAllText(dir.File("answer.json"), Prompt.Replace("\"version\": \"v1\"", "\"version\": \"v1\", \"version\": \"v2\""));
        var error = Assert.Throws<FormatException>(() => new FilePromptRepository(dir.Path).Get("answer"));
        Assert.Equal($"prompt illisible ({dir.File("answer.json")}) : clé « version » en double", error.Message);
    }

    [Theory]
    [InlineData(@"Réponds.\u001c", "Réponds.\u001c")]   // Trim() garde \x1c, qui n'est pas un blanc
    [InlineData(@"Réponds.\u2028", "Réponds.")]
    public void A_prompt_is_trimmed_of_blanks_and_keeps_the_separator_x1c(string system, string kept)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("answer.json"), $$"""{"version": "v1", "system": "{{system}}", "user": "{question}"}""");
        var prompt = new FilePromptRepository(dir.Path).Get("answer");
        Assert.Equal(kept, prompt.System);
    }

    [Fact]
    public void A_trimmed_blank_does_not_change_the_version()
    {
        using var dir = new TempDir();
        string Version(string system)
        {
            File.WriteAllText(dir.File("answer.json"), $$"""{"version": "v1", "system": "{{system}}", "user": "{question}"}""");
            return new FilePromptRepository(dir.Path).Get("answer").Version;
        }
        Assert.Equal(Version("Réponds."), Version(@"Réponds.\u2028"));
    }
}
