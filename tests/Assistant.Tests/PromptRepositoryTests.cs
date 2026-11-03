// Le dépôt des prompts : des erreurs qui nomment le fichier ou le dossier.

using Assistant.Application;
using Assistant.Infrastructure;
using Xunit;

namespace Assistant.Tests;

public class PromptRepositoryTests
{
    [Theory]
    [InlineData("""{"system": "a"}""")]                                         // sans user
    [InlineData("""{"system": 2, "user": "{question}"}""")]                     // system qui n'est pas un texte
    [InlineData("""["system"]""")]                                              // pas un objet
    public void An_incomplete_prompt_names_the_file(string content)
    {
        // Édité à la main : un champ oublié donne une erreur qui dit quoi corriger, pas une NullReferenceException.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "casse.json"), content);
            var error = Assert.Throws<FormatException>(() => new FilePromptRepository(dir.FullName).Get("casse"));
            Assert.Contains("casse.json", error.Message);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void An_unknown_prompt_names_its_folder_and_the_known_ones()
    {
        // Pour un nom mal tapé ou un dossier absent, le message donne le nom, le dossier et les prompts connus (« aucun »
        // s'il n'y en a pas), au lieu de « Could not find file … ».
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            foreach (var name in new[] { "b", "a-v2", "a" })
            {
                File.WriteAllText(Path.Combine(dir.FullName, $"{name}.json"), """{"system": "s", "user": "{question}"}""");
            }
            File.WriteAllText(Path.Combine(dir.FullName, "notes.txt"), "pas un prompt");
            Directory.CreateDirectory(Path.Combine(dir.FullName, "dossier.json"));   // un dossier non plus
            var absent = Path.Combine(dir.FullName, "absent");
            foreach (var (directory, known) in new[] { (dir.FullName, "a, a-v2, b"), (absent, "aucun") })
            {
                var error = Assert.Throws<PromptNotFoundException>(() => new FilePromptRepository(directory).Get("answr"));
                Assert.Equal($"prompt introuvable : answr dans {directory} (connus : {known})", error.Message);
            }
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
