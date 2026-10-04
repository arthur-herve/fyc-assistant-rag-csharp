// Lecture UTF-8 stricte des textes lus par l'application. Fichier source partagé :
// compilé dans Assistant.Infrastructure et dans Assistant.Cli (voir leurs .csproj), qui en ont chacune leur
// copie interne ; la ligne de commande ne dépend donc pas de l'infrastructure (ArchitectureTests).

using System.Text;

namespace Assistant;

/// <summary>
/// Textes lus par l'application (corpus, prompts, instantanés, index, réponses du service IA ; configuration et
/// jeux de questions, côté ligne de commande) : en UTF-8. La marque d'ordre des octets (BOM) qu'ajoutent certains
/// éditeurs sous Windows est acceptée ; un fichier enregistré dans un autre encodage (latin-1…) est une erreur qui dit
/// où, jamais un texte lu avec des « � » (File.ReadAllText les remplace sans rien dire).
/// </summary>
internal static class TextFiles
{
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Les octets sans la marque d'ordre des octets (BOM) qui les précède peut-être.</summary>
    public static ReadOnlySpan<byte> WithoutBom(byte[] bytes) =>
        bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? bytes.AsSpan(Encoding.UTF8.Preamble.Length) : bytes;

    /// <summary>Les octets en texte ; <see cref="FormatException"/> s'ils ne sont pas en UTF-8.</summary>
    public static string DecodeUtf8(byte[] bytes)
    {
        var text = WithoutBom(bytes);
        try
        {
            return Strict.GetString(text);
        }
        catch (DecoderFallbackException error)
        {
            // La position se compte après la marque d'ordre des octets.
            throw new FormatException($"pas en UTF-8 (octet 0x{text[error.Index]:x2} à la position {error.Index})", error);
        }
    }

    /// <summary>
    /// Le texte du fichier ; <see cref="FormatException"/> s'il n'est pas en UTF-8. Le message ne nomme pas le
    /// fichier : l'appelant le préfixe.
    /// </summary>
    public static string ReadUtf8(string path) => DecodeUtf8(File.ReadAllBytes(path));
}
