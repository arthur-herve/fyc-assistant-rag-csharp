// Des aides communes aux tests : la racine du dépôt, un dossier temporaire, et des octets ou des textes délicats
// pour les lectures strictes.

using System.Text;

namespace Assistant.Tests;

/// <summary>
/// La racine du dépôt, c'est-à-dire le dossier de FycAssistantRag.sln. On la trouve en remontant depuis le dossier
/// où les tests sont compilés, sans passer par la configuration de l'application.
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

internal static class Bytes
{
    /// <summary>Des listes imbriquées sur 100 000 niveaux : System.Text.Json s'arrête à 64.</summary>
    public static readonly string Deep = new string('[', 100_000) + new string(']', 100_000);

    public static byte[] WithBom(byte[] bytes) => Encoding.UTF8.Preamble.ToArray().Concat(bytes).ToArray();

    /// <summary>Des listes imbriquées sur <paramref name="levels"/> niveaux.</summary>
    public static string Nested(int levels) => new string('[', levels) + new string(']', levels);

    /// <summary>
    /// Un texte qui contient plusieurs caractères qu'un sérialiseur JSON peut échapper : une espace insécable, un
    /// emoji, un guillemet et un saut de ligne.
    /// </summary>
    public const string Special = "Deux jours\U000000A0: « oui » \"x\"\nfin \U0001F600";

    /// <summary>Le message de JsonText pour une chaîne qui n'est pas du texte.</summary>
    public const string NotText = "chaîne qui n'est pas du texte : surrogate UTF-16 isolé (\\ud800 à \\udfff sans sa paire)";
}
