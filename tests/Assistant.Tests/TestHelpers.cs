// Des aides communes aux tests : la racine du dépôt et un dossier temporaire.

namespace Assistant.Tests;

/// <summary>
/// La racine du dépôt, c'est-à-dire le dossier de FycAssistantRag.sln. On la trouve en remontant depuis le dossier
/// où les tests sont compilés.
/// </summary>
internal static class Repository
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FycAssistantRag.sln")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException($"FycAssistantRag.sln introuvable au-dessus de {AppContext.BaseDirectory}");
    }
}

/// <summary>Un dossier temporaire, supprimé à la fin du test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory().FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
