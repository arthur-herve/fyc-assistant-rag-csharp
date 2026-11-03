// Le dossier de sortie des expériences : daté à la seconde, sous la racine du projet, et toujours neuf.

using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

public class ResultsDirTests
{
    [Fact]
    public void Without_out_the_folder_is_dated_and_shown_from_the_current_folder()
    {
        // Sans --out, les expériences écrivent sous la racine du projet, d'où qu'on lance la commande, dans
        // un dossier daté à la seconde (AAAAMMJJ-HHMMSS). Le chemin affiché part du dossier courant :
        // « eval/resultats/… » depuis la racine, un chemin relatif qui y mène depuis ailleurs.
        var now = new DateTime(2026, 10, 1, 16, 41, 52);
        Assert.Equal("eval/resultats/exp-stabilite-20261001-164152",
                     AppConfig.ResultsDir("exp-stabilite-", now, from: AppConfig.ProjectRoot).Replace('\\', '/'));
        Assert.Equal("../../eval/resultats/exp-stabilite-20261001-164152",
                     AppConfig.ResultsDir("exp-stabilite-", now, from: Path.Combine(AppConfig.ProjectRoot, "src", "Assistant.Cli")).Replace('\\', '/'));
        Assert.Equal(Path.Combine(AppConfig.ProjectRoot, "eval", "resultats", "exp-stabilite-20261001-164152"),
                     Path.GetFullPath(AppConfig.ResultsDir("exp-stabilite-", now)));
        // Depuis un autre lecteur, sous Windows, aucun chemin relatif ne mène au dossier : on affiche le chemin entier.
        // GetRelativePath ne lit pas le disque, ce lecteur n'a donc pas besoin d'exister.
        if (OperatingSystem.IsWindows())
        {
            var other = new[] { @"Z:\", @"Y:\", @"X:\" }.First(d => !AppConfig.ProjectRoot.StartsWith(d[..2], StringComparison.OrdinalIgnoreCase));
            Assert.Equal(Path.GetFullPath(Path.Combine(AppConfig.ProjectRoot, "eval", "resultats", "exp-stabilite-20261001-164152")),
                         AppConfig.ResultsDir("exp-stabilite-", now, from: other));
        }
    }

    [Fact]
    public void A_folder_that_already_exists_gets_a_suffix()
    {
        // Daté à la seconde, le dossier par défaut peut déjà exister. Deux expériences lancées dans la même seconde
        // partageraient alors le même dossier : la seconde écraserait l'index, les instantanés et le rapport de la
        // première. La suivante reçoit donc « -2 », puis « -3 », et un fichier de ce nom compte aussi comme pris.
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "eval", "resultats", "20261001-164152");   // les dossiers parents sont créés
        Assert.Equal(path, AppConfig.CreateNewDir(path));
        Assert.Equal(path + "-2", AppConfig.CreateNewDir(path));
        File.WriteAllText(path + "-3", "");
        Assert.Equal(path + "-4", AppConfig.CreateNewDir(path));
        Assert.Equal(new[] { "20261001-164152", "20261001-164152-2", "20261001-164152-4" },
                     Directory.GetDirectories(Path.GetDirectoryName(path)!).Select(d => Path.GetFileName(d)).Order());
    }

    [Fact]
    public void A_parent_that_is_not_a_folder_is_an_error()
    {
        // Le parent « resultats » existe, mais c'est un fichier. L'IOException remonte et rien n'est créé : ce parent
        // ne doit pas passer pour un nom déjà pris, sinon on essaierait « -2 », « -3 » et ainsi de suite, sans fin.
        using var dir = new TempDir();
        var parent = dir.File("resultats");
        File.WriteAllText(parent, "");
        Assert.ThrowsAny<IOException>(() => AppConfig.CreateNewDir(Path.Combine(parent, "20261001-164152")));
        Assert.Equal(new[] { parent }, Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public void Folders_created_at_the_same_instant_are_all_different()
    {
        // Directory.CreateDirectory réussit aussi sur un dossier qui existe. Vérifier puis créer, sans verrou, pourrait
        // donc donner le même dossier à deux processus. Huit fils lancés ensemble doivent recevoir huit dossiers
        // différents.
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "20261001-164152");
        using var start = new Barrier(8);
        var created = new string[8];
        var failures = new Exception?[8];
        var threads = Enumerable.Range(0, 8).Select(i => new Thread(() =>
        {
            try
            {
                start.SignalAndWait();
                created[i] = AppConfig.CreateNewDir(path);
            }
            catch (Exception error)   // une exception sortie du fil arrêterait l'hôte de test
            {
                failures[i] = error;
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.All(failures, failure => Assert.Null(failure));
        Assert.Equal(new[] { "20261001-164152" }.Concat(Enumerable.Range(2, 7).Select(n => $"20261001-164152-{n}")).Order(),
                     created.Select(d => Path.GetFileName(d)).Order());
    }
}
