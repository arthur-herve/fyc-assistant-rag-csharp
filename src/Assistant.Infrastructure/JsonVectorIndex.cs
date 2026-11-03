// Adaptateurs : un index vectoriel en mémoire, et un index persisté en JSON. C'est volontairement
// simple. Pour quelques milliers de morceaux, une recherche exhaustive par similarité cosinus suffit,
// et une vraie base vectorielle se brancherait derrière le même port.

using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Application;
using Assistant.Domain;

namespace Assistant.Infrastructure;

/// <summary>Le fichier d'index existe mais ne se lit pas, ce qui n'est pas la même chose que « aucun index ».</summary>
public sealed class IndexUnreadableException : FormatException
{
    public IndexUnreadableException(string message, Exception inner) : base(message, inner) { }
}

public class InMemoryVectorIndex : IVectorIndex
{
    private sealed record State(IndexManifest? Manifest, List<Chunk> Chunks, List<double[]> Vectors);

    // Tout l'état tient dans un seul champ, remplacé d'un coup : une recherche ne peut pas voir le
    // manifeste d'un index avec les vecteurs d'un autre.
    private State _state = new(null, new(), new());

    public static double[] Normalize(IReadOnlyList<double> vector)
    {
        var norm = Math.Sqrt(vector.Sum(x => x * x));
        return norm == 0 ? vector.Select(_ => 0.0).ToArray() : vector.Select(x => x / norm).ToArray();
    }

    public virtual IndexManifest? Manifest() => _state.Manifest;

    public virtual void Replace(IndexManifest manifest, IReadOnlyList<Chunk> chunks, IReadOnlyList<double[]> vectors)
    {
        Check(manifest, chunks, vectors);
        _state = new State(manifest, chunks.ToList(), vectors.Select(Normalize).ToList());
    }

    protected static void Check(IndexManifest manifest, IReadOnlyList<Chunk> chunks, IReadOnlyList<double[]> vectors)
    {
        if (chunks.Count != vectors.Count)
        {
            throw new ArgumentException("autant de vecteurs que de morceaux sont attendus");
        }
        if (vectors.Any(v => v.Length != manifest.Dimension))
        {
            throw new ArgumentException($"tous les vecteurs doivent avoir {manifest.Dimension} dimensions");
        }
    }

    public virtual IReadOnlyList<Passage> Search(double[] vector, int topK, Func<Chunk, bool> predicate)
    {
        var state = _state;
        // La requête doit avoir la longueur des vecteurs stockés. Plus longue, la boucle lirait au-delà du vecteur
        // stocké. Plus courte, elle donnerait en silence un produit scalaire partiel. Un index sans aucun vecteur
        // s'en tient à la dimension de son manifeste.
        var expected = state.Vectors.Count > 0 ? state.Vectors[0].Length : state.Manifest?.Dimension;
        if (expected is not null && vector.Length != expected)
        {
            throw new ArgumentException($"vecteur de requête de {vector.Length} dimensions, l'index en attend {expected}");
        }
        var query = Normalize(vector);
        var scored = new List<Passage>();
        for (var i = 0; i < state.Chunks.Count; i++)
        {
            if (!predicate(state.Chunks[i]))
            {
                continue;
            }
            var stored = state.Vectors[i];
            var score = 0.0;
            for (var j = 0; j < query.Length; j++)
            {
                score += query[j] * stored[j];
            }
            scored.Add(new Passage(state.Chunks[i], score));
        }
        return scored.OrderByDescending(p => p.Score).Take(topK).ToList();
    }
}

/// <summary>
/// Index persisté dans un fichier JSON lisible, ce qui est pratique pour l'enseignement. Le fichier n'est lu
/// qu'une fois, au premier besoin : la ligne de commande construit un nouvel index à chaque commande.
/// </summary>
public sealed class JsonVectorIndex : InMemoryVectorIndex
{
    // Le JSON tient sur une seule ligne, car un index compte des milliers de vecteurs. Les accents restent
    // tels quels.
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Les champs du manifeste, dans l'ordre alphabétique. Un index écrit dans un autre format, sans manifeste ou
    // avec d'autres champs, n'est pas lu à moitié : le message dit de le reconstruire, et comment.
    private static readonly string[] ManifestFields =
        { "chunk_count", "corpus_fingerprint", "created_at", "dimension", "document_count", "embedding_model", "index_id", "splitter" };

    private readonly string _path;
    private bool _loaded;

    public JsonVectorIndex(string path)
    {
        _path = path;
    }

    private void Load()
    {
        if (_loaded)
        {
            return;
        }
        if (File.Exists(_path))
        {
            try
            {
                // UTF-8 strict, BOM accepté, et JSON strict jusque dans les chaînes : un index abîmé est
                // signalé comme illisible.
                var root = JsonText.Parse(TextFiles.ReadUtf8(_path), strings: true)!.AsObject();
                if (root["manifest"] is not JsonObject m
                    || !m.Select(kv => kv.Key).Order(StringComparer.Ordinal).SequenceEqual(ManifestFields))
                {
                    throw new FormatException("écrit dans un autre format");
                }
                var splitter = m["splitter"]!.AsObject()
                    .ToDictionary(kv => kv.Key, kv => JsonValues.ToObject(kv.Value!), StringComparer.Ordinal);
                var manifest = new IndexManifest(
                    m["index_id"]!.GetValue<string>(), m["embedding_model"]!.GetValue<string>(), m["dimension"]!.GetValue<int>(),
                    m["corpus_fingerprint"]!.GetValue<string>(), splitter, m["document_count"]!.GetValue<int>(),
                    m["chunk_count"]!.GetValue<int>(), m["created_at"]!.GetValue<string>());
                var chunks = root["chunks"]!.AsArray().Select(c => new Chunk(
                    c!["id"]!.GetValue<string>(), c["document_id"]!.GetValue<string>(), c["document_title"]!.GetValue<string>(),
                    c["text"]!.GetValue<string>(), c["position"]!.GetValue<int>(),
                    c["allowed_groups"]!.AsArray().Select(g => g!.GetValue<string>()).ToHashSet())).ToList();
                var vectors = root["vectors"]!.AsArray()
                    .Select(v => v!.AsArray().Select(x => x!.GetValue<double>()).ToArray()).ToList();
                base.Replace(manifest, chunks, vectors);
            }
            catch (Exception error) when (JsonFiles.IsMalformed(error))
            {
                // L'index se reconstruit à partir du corpus : quelle que soit la cause, le message dit comment.
                throw new IndexUnreadableException($"index illisible ({_path}) : {JsonFiles.Describe(error)}, "
                                                   + "réindexez avec dotnet run --project src/Assistant.Cli -- index", error);
            }
        }
        _loaded = true;
    }

    public override IndexManifest? Manifest()
    {
        Load();
        return base.Manifest();
    }

    public override IReadOnlyList<Passage> Search(double[] vector, int topK, Func<Chunk, bool> predicate)
    {
        Load();
        return base.Search(vector, topK, predicate);
    }

    public override void Replace(IndexManifest manifest, IReadOnlyList<Chunk> chunks, IReadOnlyList<double[]> vectors)
    {
        // La mémoire garde exactement ce qui est écrit, arrondi compris : les scores restent les mêmes
        // après un redémarrage.
        var stored = vectors.Select(v => Normalize(v).Select(x => Math.Round(x, 6)).ToArray()).ToList();
        Check(manifest, chunks, stored);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        File.WriteAllText(_path, Serialize(manifest, chunks, stored));
        base.Replace(manifest, chunks, stored);
        _loaded = true;
    }

    private static string Serialize(IndexManifest manifest, IReadOnlyList<Chunk> chunks, IReadOnlyList<double[]> stored)
    {
        var payload = new JsonObject
        {
            ["manifest"] = new JsonObject
            {
                ["index_id"] = manifest.IndexId,
                ["embedding_model"] = manifest.EmbeddingModel,
                ["dimension"] = manifest.Dimension,
                ["corpus_fingerprint"] = manifest.CorpusFingerprint,
                ["splitter"] = JsonValues.FromDictionary(manifest.Splitter),
                ["document_count"] = manifest.DocumentCount,
                ["chunk_count"] = manifest.ChunkCount,
                ["created_at"] = manifest.CreatedAt,
            },
            ["chunks"] = new JsonArray(chunks.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["document_id"] = c.DocumentId,
                ["document_title"] = c.DocumentTitle,
                ["text"] = c.Text,
                ["position"] = c.Position,
                ["allowed_groups"] = new JsonArray(c.AllowedGroups.OrderBy(g => g, StringComparer.Ordinal).Select(g => (JsonNode)g).ToArray()),
            }).ToArray()),
            ["vectors"] = new JsonArray(stored.Select(v => (JsonNode)new JsonArray(v.Select(x => (JsonNode)x).ToArray())).ToArray()),
        };
        return payload.ToJsonString(Options);
    }
}
