// Le dépôt des prompts : la version que chaque trace enregistre, et des erreurs qui nomment le fichier ou le
// dossier.

using Assistant.Application;
using Assistant.Cli;
using Assistant.Infrastructure;
using Xunit;

namespace Assistant.Tests;

public class PromptRepositoryTests
{
    [Fact]
    public void The_answer_prompt_receives_the_passages()
    {
        var template = new FilePromptRepository(Composition.PromptsDir).Get("answer");
        Assert.Contains("{passages}", template.User);
        Assert.Contains("[1]", template.Render("Q ?", "[1] Titre\ntexte"));
    }

    [Fact]
    public void Prompt_versions_of_the_repository_keep_their_value()
    {
        // Ces valeurs sont figées : les réponses et les instantanés les tracent, et eval/resultats/README.md les cite.
        // La formule de l'empreinte ne doit donc pas changer d'une version du code à l'autre.
        var prompts = new FilePromptRepository(Composition.PromptsDir);
        Assert.Equal("v1+085b70e7", prompts.Get("answer").Version);
        Assert.Equal("v2+1708960b", prompts.Get("answer-v2").Version);
        Assert.Equal("v1+085b70e7"[3..], FilePromptRepository.Fingerprint("v1", prompts.Get("answer").System, prompts.Get("answer").User));
    }

    [Fact]
    public void Prompt_modified_without_changing_its_declared_version_is_detected()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "answer.json");
            File.WriteAllText(path, """{"version": "v1", "system": "a", "user": "{question}"}""");
            var first = new FilePromptRepository(dir.FullName).Get("answer").Version;
            File.WriteAllText(path, """{"version": "v1", "system": "b", "user": "{question}"}""");
            var second = new FilePromptRepository(dir.FullName).Get("answer").Version;
            Assert.NotEqual(first, second);   // contenu modifié : détecté
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_comment_in_a_prompt_does_not_change_its_version()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "answer.json");
            File.WriteAllText(path, """{"version": "v1", "system": "a", "user": "{question}"}""");
            var first = new FilePromptRepository(dir.FullName).Get("answer").Version;
            File.WriteAllText(path, """{"version": "v1", "_commentaire": "sans effet", "system": "a", "user": "{question}"}""");
            var second = new FilePromptRepository(dir.FullName).Get("answer").Version;
            Assert.Equal(first, second);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("""{"system": "a", "user": "{question}"}""")]                   // sans version
    [InlineData("""{"version": 2, "system": "a", "user": "{question}"}""")]     // version qui n'est pas un texte
    [InlineData("""["version"]""")]                                             // pas un objet
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
                File.WriteAllText(Path.Combine(dir.FullName, $"{name}.json"), """{"version": "v1", "system": "s", "user": "{question}"}""");
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
