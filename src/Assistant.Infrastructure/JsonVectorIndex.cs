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

/// <summary>Le fichier d'index existe mais ne se lit pas : ce n'est pas « aucun index ».</summary>
public sealed class IndexUnreadableException : FormatException
{
    public IndexUnreadableException(string message, Exception inner) : base(message, inner) { }
}

public class InMemoryVectorIndex : IVectorIndex
{
    private sealed record State(IndexManifest? Manifest, List<Chunk> Chunks, List<double[]> Vectors);

    // Un seul champ, remplacé d'un coup : une recherche ne voit jamais le manifeste d'un index
    // et les vecteurs d'un autre.
    private State _state = new(null, new(), new());

    protected IReadOnlyList<Chunk> Chunks => _state.Chunks;
    protected IReadOnlyList<double[]> Vectors => _state.Vectors;

    public static double[] Normalize(IReadOnlyList<double> vector)
    {
        var norm = Math.Sqrt(vector.Sum(x => x * x));
        return norm == 0 ? vector.Select(_ => 0.0).ToArray() : vector.Select(x => x / norm).ToArray();
    }

    public virtual IndexManifest? Manifest() => _state.Manifest;

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
        _state = new State(manifest, chunks.ToList(), vectors.Select(Normalize).ToList());
    }

    public virtual IReadOnlyList<Passage> Search(double[] vector, int topK, Func<Chunk, bool> predicate)
    {
        var state = _state;
        if (state.Manifest is not null && vector.Length != state.Manifest.Dimension)
        {
            throw new ArgumentException($"vecteur de requête de {vector.Length} dimensions, l'index en attend {state.Manifest.Dimension}");
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
/// Index persisté dans un fichier JSON lisible (pratique pour l'enseignement). Le fichier est relu
/// quand il a changé, à la lecture du manifeste (que les cas d'usage consultent avant de chercher) :
/// un processus long (<c>serve</c>) voit une réindexation faite par la ligne de commande, droits compris.
/// </summary>
public sealed class JsonVectorIndex : InMemoryVectorIndex
{
    private readonly string _path;
    private readonly object _gate = new();
    // Version du fichier chargé : date, taille et début du fichier. La date seule ne suffit pas :
    // deux réécritures rapides peuvent tomber dans la même tranche d'horloge du disque. Le début du
    // fichier contient le manifeste (index_id, created_at), que toute reconstruction change.
    private (DateTime, long, string)? _version;

    public JsonVectorIndex(string path)
    {
        _path = path;
    }

    private void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return;
            }
            var version = FileVersion();
            if (version == _version)
            {
                return;
            }
            try
            {
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
            catch (Exception error) when (JsonFiles.IsMalformed(error))
            {
                throw new IndexUnreadableException($"index illisible ({_path}) : {JsonFiles.Describe(error)}", error);
            }
            _version = version;
        }
    }

    private (DateTime, long, string) FileVersion()
    {
        var file = new FileInfo(_path);
        var head = new byte[1024];
        int read;
        using (var stream = file.OpenRead())
        {
            read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        return (file.LastWriteTimeUtc, file.Length, Convert.ToHexString(head, 0, read));
    }

    public override IndexManifest? Manifest()
    {
        Load();
        return base.Manifest();
    }

    public override IReadOnlyList<Passage> Search(double[] vector, int topK, Func<Chunk, bool> predicate)
    {
        if (_version is null)   // jamais chargé ; sinon, pas de relecture : SearchPassages vérifie après coup
        {
            Load();
        }
        return base.Search(vector, topK, predicate);
    }

    public override void Replace(IndexManifest manifest, IReadOnlyList<Chunk> chunks, IReadOnlyList<double[]> vectors)
    {
        lock (_gate)
        {
            ReplaceAndWrite(manifest, chunks, vectors);
            _version = FileVersion();
        }
    }

    private void ReplaceAndWrite(IndexManifest manifest, IReadOnlyList<Chunk> chunks, IReadOnlyList<double[]> vectors)
    {
        // En mémoire, exactement ce qui est écrit : mêmes scores avant et après un redémarrage.
        var stored = vectors.Select(v => Normalize(v).Select(x => Math.Round(x, 6)).ToArray()).ToList();
        base.Replace(manifest, chunks, stored);
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
            ["vectors"] = new JsonArray(stored.Select(v => (JsonNode)new JsonArray(v.Select(x => (JsonNode)x).ToArray())).ToArray()),
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
        JsonObject o => o.ToDictionary(kv => kv.Key, kv => kv.Value is null ? (object)"" : ToObject(kv.Value), StringComparer.Ordinal),
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<int>(out var i) => i,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray a => a.Select(item => item is null ? (object)"" : ToObject(item)).ToList(),
        _ => node.ToJsonString(),
    };

    public static object? ToObjectOrNull(JsonNode? node) => node is null ? null : ToObject(node);

    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        IReadOnlyDictionary<string, object?> d => FromDictionary(d),
        System.Collections.IEnumerable items => new JsonArray(items.Cast<object?>().Select(ToNode).ToArray()),
        _ => throw new ArgumentException($"valeur non sérialisable : {value.GetType().Name}"),
    };

    public static JsonObject FromDictionary<TValue>(IReadOnlyDictionary<string, TValue> values)
    {
        var result = new JsonObject();
        foreach (var (key, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            result[key] = ToNode(value);
        }
        return result;
    }
}
