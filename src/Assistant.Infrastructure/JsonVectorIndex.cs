// Adaptateurs : index vectoriel en mémoire et index persisté en JSON.
// Volontairement simple (quelques milliers de morceaux) : la recherche est une
// similarité cosinus exhaustive. Une vraie base vectorielle se brancherait derrière
// le même port. Le format JSON est celui de la version Python : un index construit
// par l'une se lit par l'autre — c'est le modèle d'embeddings qui doit correspondre,
// pas le langage de l'application.

using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Application;
using Assistant.Domain;

namespace Assistant.Infrastructure;

public class InMemoryVectorIndex : IVectorIndex
{
    private IndexManifest? _manifest;
    private List<Chunk> _chunks = new();
    private List<double[]> _vectors = new();

    protected IReadOnlyList<Chunk> Chunks => _chunks;
    protected IReadOnlyList<double[]> Vectors => _vectors;

    public static double[] Normalize(IReadOnlyList<double> vector)
    {
        var norm = Math.Sqrt(vector.Sum(x => x * x));
        return norm == 0 ? vector.Select(_ => 0.0).ToArray() : vector.Select(x => x / norm).ToArray();
    }

    public virtual IndexManifest? Manifest() => _manifest;

    public virtual void Replace(IndexManifest manifest, IReadOnlyList<Chunk> chunks, IReadOnlyList<double[]> vectors)
    {
        if (chunks.Count != vectors.Count)
        {
            throw new ArgumentException("autant de vecteurs que de morceaux sont attendus");
        }
        if (vectors.Any(v => v.Length != manifest.Dimension))
        {
            throw new ArgumentException($"tous les vecteurs doivent avoir {manifest.Dimension} dimensions");
        }
        _manifest = manifest;
        _chunks = chunks.ToList();
        _vectors = vectors.Select(Normalize).ToList();
    }

    public virtual IReadOnlyList<Passage> Search(double[] vector, int topK, Func<Chunk, bool> predicate)
    {
        var query = Normalize(vector);
        var scored = new List<Passage>();
        for (var i = 0; i < _chunks.Count; i++)
        {
            if (!predicate(_chunks[i]))
            {
                continue;
            }
            var stored = _vectors[i];
            var score = 0.0;
            for (var j = 0; j < query.Length && j < stored.Length; j++)
            {
                score += query[j] * stored[j];
            }
            scored.Add(new Passage(_chunks[i], score));
        }
        return scored.OrderByDescending(p => p.Score).Take(topK).ToList();
    }
}

/// <summary>Index persisté dans un fichier JSON lisible (pratique pour l'enseignement).</summary>
public sealed class JsonVectorIndex : InMemoryVectorIndex
{
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
        _loaded = true;
        if (!File.Exists(_path))
        {
            return;
        }
        var root = JsonNode.Parse(File.ReadAllText(_path))!.AsObject();
        var m = root["manifest"]!.AsObject();
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
        base.Replace(manifest, chunks, vectors);
        _loaded = true;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
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
            ["chunks"] = new JsonArray(Chunks.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["document_id"] = c.DocumentId,
                ["document_title"] = c.DocumentTitle,
                ["text"] = c.Text,
                ["position"] = c.Position,
                ["allowed_groups"] = new JsonArray(c.AllowedGroups.OrderBy(g => g, StringComparer.Ordinal).Select(g => (JsonNode)g).ToArray()),
            }).ToArray()),
            ["vectors"] = new JsonArray(Vectors.Select(v => (JsonNode)new JsonArray(v.Select(x => (JsonNode)Math.Round(x, 6)).ToArray())).ToArray()),
        };
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, payload.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.Move(tmp, _path, overwrite: true);
    }
}

/// <summary>Conversion des petits dictionnaires de configuration (découpage) vers et depuis JSON.</summary>
public static class JsonValues
{
    public static object ToObject(JsonNode node) => node switch
    {
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<int>(out var i) => i,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };

    public static JsonObject FromDictionary(IReadOnlyDictionary<string, object> values)
    {
        var result = new JsonObject();
        foreach (var (key, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            result[key] = value switch
            {
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                string s => JsonValue.Create(s),
                _ => JsonValue.Create(value.ToString()),
            };
        }
        return result;
    }
}
