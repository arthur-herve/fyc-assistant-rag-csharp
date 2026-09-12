// Cas d'usage : indexer le corpus documentaire.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Coeur;

public static class Fingerprints
{
    /// <summary>Empreinte du corpus : change dès qu'un texte ou un droit d'accès change.</summary>
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

    public static string Sha256Hex(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>Sérialisation à la manière de Python (`json.dumps(sort_keys=True)`) pour les valeurs simples.</summary>
    public static string PythonJson(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => JsonSerializer.Serialize(s, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
        IReadOnlyDictionary<string, object> d => "{" + string.Join(", ", d.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"\"{kv.Key}\": {PythonJson(kv.Value)}")) + "}",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture)!,
        _ => JsonSerializer.Serialize(value),
    };

    private static void Feed(SHA256 sha, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
    }
}

public sealed class IndexCorpus
{
    private readonly IDocumentSource _source;
    private readonly ITextSplitter _splitter;
    private readonly IEmbedder _embedder;
    private readonly IVectorIndex _index;
    private readonly IClock _clock;
    private readonly int _batchSize;

    public IndexCorpus(IDocumentSource source, ITextSplitter splitter, IEmbedder embedder,
                       IVectorIndex index, IClock clock, int batchSize = 32)
    {
        _source = source;
        _splitter = splitter;
        _embedder = embedder;
        _index = index;
        _clock = clock;
        _batchSize = batchSize;
    }

    public IndexManifest Execute()
    {
        var documents = _source.Load();
        var chunks = documents.SelectMany(d => _splitter.Split(d)).ToList();
        if (chunks.Count == 0)
        {
            throw new EmptyCorpusException();
        }

        var vectors = new List<double[]>();
        string? model = null;
        var dimension = 0;
        for (var start = 0; start < chunks.Count; start += _batchSize)
        {
            var batch = chunks.Skip(start).Take(_batchSize).ToList();
            var result = _embedder.EmbedDocuments(batch.Select(c => c.Text).ToList());
            if (result.Vectors.Count != batch.Count)
            {
                throw new InconsistentEmbeddingsException(
                    $"{batch.Count} textes envoyés, {result.Vectors.Count} vecteurs reçus");
            }
            if (model is null)
            {
                (model, dimension) = (result.Model, result.Dimension);
            }
            else if (result.Model != model || result.Dimension != dimension)
            {
                // Le service IA a changé de modèle en cours d'indexation.
                throw new InconsistentEmbeddingsException($"modèle {model} puis {result.Model} pendant la même indexation");
            }
            vectors.AddRange(result.Vectors);
        }

        var fingerprint = Fingerprints.Corpus(documents);
        var splitter = _splitter.Describe();
        // Même sérialisation que `json.dumps([...], sort_keys=True)` en Python : un même corpus, un même
        // modèle et un même découpage donnent le même identifiant d'index dans les deux versions.
        var identity = $"[{Fingerprints.PythonJson(fingerprint)}, {Fingerprints.PythonJson(model!)}, {dimension}, {Fingerprints.PythonJson(splitter)}]";
        var manifest = new IndexManifest(
            IndexId: Fingerprints.Sha256Hex(identity)[..12],
            EmbeddingModel: model!,
            Dimension: dimension,
            CorpusFingerprint: fingerprint,
            Splitter: splitter,
            DocumentCount: documents.Count,
            ChunkCount: chunks.Count,
            CreatedAt: _clock.Now().ToString("yyyy-MM-dd'T'HH:mm:ssK"));
        _index.Replace(manifest, chunks, vectors);
        return manifest;
    }
}
