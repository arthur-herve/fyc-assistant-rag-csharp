// Empreintes : celle du corpus, l'identifiant d'un index et le SHA-256 d'un texte.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assistant.Domain;

namespace Assistant.Application;

public static class Fingerprints
{
    /// <summary>
    /// Empreinte du corpus. Elle change dès qu'un texte ou un droit d'accès change. Chaque champ est précédé de
    /// sa longueur en octets, sinon « a » + « bc » et « ab » + « c » donneraient la même empreinte.
    /// </summary>
    public static string Corpus(IReadOnlyList<Document> documents)
    {
        using var sha = SHA256.Create();
        foreach (var doc in documents.OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            Feed(sha, doc.Id);
            Feed(sha, doc.Title);
            Feed(sha, doc.Text);
            Feed(sha, string.Join(",", doc.AllowedGroups.OrderBy(g => g, StringComparer.Ordinal)));
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>Le SHA-256 du texte encodé en UTF-8, en hexadécimal minuscule.</summary>
    public static string Sha256Hex(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>
    /// Identifiant d'un index. On écrit en JSON, avec System.Text.Json, ce qui définit l'index : l'empreinte du corpus,
    /// le modèle d'embeddings, sa dimension et le découpage. On garde ensuite les 12 premiers caractères hexadécimaux du
    /// SHA-256 de ce texte. Comme les clés du découpage sont triées, les mêmes valeurs donnent le même identifiant quel
    /// que soit l'ordre des clés, et il suffit qu'une seule valeur change pour que l'identifiant change. Le JSON
    /// distingue aussi le texte « 4 » du nombre 4, que la forme de <see cref="ConfigurationValues.Canonical"/> confond.
    /// </summary>
    public static string IndexId(string corpusFingerprint, string embeddingModel, int dimension,
                                 IReadOnlyDictionary<string, object> splitter)
    {
        var sortedSplitter = new SortedDictionary<string, object>(splitter.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);
        var identity = JsonSerializer.Serialize(new object[] { corpusFingerprint, embeddingModel, dimension, sortedSplitter });
        return Sha256Hex(identity)[..12];
    }

    private static void Feed(SHA256 sha, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var length = Encoding.ASCII.GetBytes($"{bytes.Length.ToString(CultureInfo.InvariantCulture)}:");
        sha.TransformBlock(length, 0, length.Length, null, 0);
        sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
    }
}
