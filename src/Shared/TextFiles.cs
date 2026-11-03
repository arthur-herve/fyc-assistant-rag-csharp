// Lecture UTF-8 stricte des textes que lit l'application.
// Ce fichier source est partagé : Assistant.Infrastructure et Assistant.Cli le compilent chacun (voir leurs .csproj)
// et en ont leur propre copie interne. Ainsi, la ligne de commande n'a pas à nommer l'infrastructure, que seule la
// racine de composition connaît (InfrastructureLeakTests).

using System.Text;

namespace Assistant;

/// <summary>
/// Tous les textes que lit l'application sont en UTF-8 : le corpus, les prompts, les instantanés, l'index et les
/// réponses du service IA, et côté ligne de commande la configuration et les jeux de questions. La marque d'ordre des
/// octets (BOM) qu'ajoutent certains éditeurs sous Windows est acceptée. Un fichier enregistré dans un autre encodage,
/// en latin-1 par exemple, donne une erreur qui indique où se trouve l'octet fautif. On ne lit jamais un texte truffé
/// de « � », comme le ferait File.ReadAllText, qui remplace les octets invalides sans rien dire.
/// </summary>
internal static class TextFiles
{
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Les octets, sans la marque d'ordre des octets (BOM) qui peut les précéder.</summary>
    public static ReadOnlySpan<byte> WithoutBom(byte[] bytes) =>
        bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? bytes.AsSpan(Encoding.UTF8.Preamble.Length) : bytes;

    /// <summary>Décode les octets, ou lève <see cref="FormatException"/> s'ils ne sont pas en UTF-8.</summary>
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
    /// Lit le fichier, ou lève <see cref="FormatException"/> s'il n'est pas en UTF-8. Le message ne nomme pas le
    /// fichier, c'est à l'appelant de l'ajouter en tête.
    /// </summary>
    public static string ReadUtf8(string path) => DecodeUtf8(File.ReadAllBytes(path));
}
