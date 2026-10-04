// Adaptateurs : index vectoriel en mémoire et index persisté en JSON.
// Volontairement simple (quelques milliers de morceaux) : la recherche est une
// similarité cosinus exhaustive. Une vraie base vectorielle se brancherait derrière
// le même port.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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

    /// <summary>Plus d'index (son fichier a été supprimé).</summary>
    protected void Clear() => _state = new State(null, new(), new());

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

    public virtual IReadOnlyList<Passage> Search(double[] vector, int topK, Func<Chunk, bool> predicate, string? indexId = null)
    {
        var state = _state;
        if (indexId is not null && state.Manifest?.IndexId != indexId)
        {
            // Vérifié sur l'état même qu'on va interroger : jamais un autre index que celui contrôlé.
            throw new IndexReplacedException();
        }
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
/// un processus long (<c>serve</c>) voit une réindexation faite par la ligne de commande, droits compris,
/// et un fichier supprimé (plus d'index), comme la ligne de commande. Non scellée : les tests simulent
/// un autre processus qui écrit au mauvais moment (<see cref="MoveIntoPlace"/>).
/// </summary>
public class JsonVectorIndex : InMemoryVectorIndex
{
    // Sous Windows, un fichier ouvert par un autre processus ne se remplace pas toujours, et ne s'ouvre
    // pas pendant qu'on le remplace : ces collisions durent quelques millisecondes, on réessaie brièvement.
    private static readonly int[] SharingDelaysMs = { 10, 20, 50, 100, 200 };
    // Un rédacteur renomme son fichier temporaire moins d'une seconde après l'avoir écrit, nouvelles tentatives
    // comprises. Plus vieux que deux minutes, c'est celui d'un rédacteur tué au milieu d'une écriture : il ne fait plus
    // attendre les lecteurs (BeingReplaced). Plus vieux qu'une heure, la prochaine écriture réussie le supprime.
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DeletedAfter = TimeSpan.FromHours(1);
    // Une seule ligne (un index compte des milliers de vecteurs), accents tels quels.
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly Action<TimeSpan> _sleep;
    private readonly object _gate = new();
    // Version du fichier chargé : date, taille et début du fichier. La date seule ne suffit pas :
    // deux réécritures rapides peuvent tomber dans la même tranche d'horloge du disque. Le début du
    // fichier contient le manifeste (index_id, created_at), que toute reconstruction change.
    private (DateTime, long, string)? _version;

    public JsonVectorIndex(string path, Action<TimeSpan>? sleep = null)
    {
        _path = path;
        _sleep = sleep ?? Thread.Sleep;
    }

    private void Load()
    {
        lock (_gate)
        {
            ((DateTime, long, string) Version, byte[]? Content) file;
            try
            {
                file = Shared(ReadIfChanged);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                Clear();   // fichier supprimé : plus d'index, comme le voit la ligne de commande
                _version = null;
                return;
            }
            if (file.Content is null)
            {
                return;   // inchangé
            }
            try
            {
                // UTF-8 strict (BOM accepté) et JSON strict, chaînes comprises : un index abîmé est illisible.
                var root = JsonText.Parse(TextFiles.DecodeUtf8(file.Content), strings: true)!.AsObject();
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
            _version = file.Version;
        }
    }

    /// <summary>
    /// Version et contenu lus sur un même descripteur, qui partage la suppression (FileShare.Delete) :
    /// il ne bloque pas un autre processus qui remplace le fichier (<see cref="MoveIntoPlace"/>).
    /// Pas de contenu quand la version est celle déjà chargée.
    /// </summary>
    private ((DateTime, long, string) Version, byte[]? Content) ReadIfChanged()
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var version = FileVersion(stream);
        if (version == _version)
        {
            return (version, null);
        }
        stream.Position = 0;
        using var content = new MemoryStream();
        stream.CopyTo(content);
        return (version, content.ToArray());
    }

    private static (DateTime, long, string) FileVersion(FileStream stream)
    {
        var head = new byte[1024];
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return (File.GetLastWriteTimeUtc(stream.SafeFileHandle), stream.Length, Convert.ToHexString(head, 0, read));
    }

    private T Shared<T>(Func<T> action)
    {
        foreach (var delay in SharingDelaysMs)
        {
            try
            {
                return action();
            }
            catch (Exception error) when (error is UnauthorizedAccessException || error.GetType() == typeof(IOException)
                                          || (error is FileNotFoundException && BeingReplaced()))
            {
                // Collision passagère : fichier ouvert, ou en cours de remplacement, par un autre processus. Le reste
                // (pas d'index, dossier introuvable…) ne se règle pas en réessayant : on ne fait pas attendre.
                _sleep(TimeSpan.FromMilliseconds(delay));
            }
        }
        return action();
    }

    /// <summary>
    /// L'index manque-t-il seulement le temps d'un remplacement (<see cref="MoveIntoPlace"/>) ? File.Replace met
    /// l'ancien de côté avant d'installer le nouveau, qui garde jusque-là son nom temporaire : pendant cet instant,
    /// des fichiers <c>index.json.….tmp</c> récents sont là. Ou le nouveau vient d'être installé.
    /// </summary>
    private bool BeingReplaced() => Temporaries().Any(t => Age(t.Path) < AbandonedAfter) || File.Exists(_path);

    /// <summary>Temps écoulé depuis la dernière écriture (un fichier disparu entre-temps est daté de 1601 : très vieux).</summary>
    private static TimeSpan Age(string file) => DateTime.UtcNow - File.GetLastWriteTimeUtc(file);

    /// <summary>
    /// Les fichiers temporaires des écritures de cet index, nommés comme dans <see cref="Replace"/> :
    /// <c>index.json.&lt;id&gt;.tmp</c>, et <c>index.json.&lt;id&gt;.old.tmp</c>,
    /// l'ancien index que File.Replace met de côté. L'identifiant est un Guid de 32 chiffres hexadécimaux : rien d'autre
    /// n'est touché.
    /// </summary>
    private IEnumerable<(string Path, string Id, bool SetAside)> Temporaries()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        var name = Path.GetFileName(_path);
        if (!Directory.Exists(directory))
        {
            yield break;
        }
        var temporary = new Regex($@"\A{Regex.Escape(name)}\.([0-9a-f]{{32}})(\.old)?\.tmp\z");
        foreach (var file in Directory.EnumerateFiles(directory, name + ".*.tmp"))
        {
            var match = temporary.Match(Path.GetFileName(file));
            if (match.Success)
            {
                yield return (file, match.Groups[1].Value, match.Groups[2].Success);
            }
        }
    }

    /// <summary>
    /// Supprime, après une écriture réussie, les fichiers temporaires abandonnés depuis plus d'une heure par des
    /// rédacteurs tués. Jamais un fichier dont un rédacteur actif a encore besoin : son fichier temporaire est récent, et
    /// l'ancien index qu'il met de côté (à la date de cet index, parfois ancienne) a encore, tant que File.Replace n'a pas
    /// fini, ce fichier temporaire à côté de lui ; ensuite, il ne sert plus. Les fichiers temporaires sont donc examinés
    /// avant les anciens index mis de côté.
    /// </summary>
    private void DeleteAbandonedTemporaries()
    {
        try
        {
            foreach (var (file, id, setAside) in Temporaries().OrderBy(t => t.SetAside).ToList())
            {
                if (Age(file) > DeletedAfter && !(setAside && File.Exists($"{_path}.{id}.tmp")))
                {
                    DeleteIfPossible(file);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Dossier illisible un instant : la prochaine écriture s'en chargera.
        }
    }

    public override IndexManifest? Manifest()
    {
        Load();
        return base.Manifest();
    }

    public override IReadOnlyList<Passage> Search(double[] vector, int topK, Func<Chunk, bool> predicate, string? indexId = null)
    {
        if (_version is null)   // jamais chargé ; sinon, pas de relecture : indexId est vérifié sur l'état chargé
        {
            Load();
        }
        return base.Search(vector, topK, predicate, indexId);
    }

    public override void Replace(IndexManifest manifest, IReadOnlyList<Chunk> chunks, IReadOnlyList<double[]> vectors)
    {
        // En mémoire, exactement ce qui est écrit : mêmes scores avant et après un redémarrage.
        var stored = vectors.Select(v => Normalize(v).Select(x => Math.Round(x, 6)).ToArray()).ToList();
        Check(manifest, chunks, stored);
        var json = Serialize(manifest, chunks, stored);
        lock (_gate)
        {
            // Le fichier d'abord, la mémoire ensuite : si l'écriture échoue, l'index en service reste le
            // précédent, en mémoire comme sur le disque. Des fichiers temporaires par écriture : deux
            // réindexations simultanées n'écrivent jamais dans les mêmes.
            var id = Guid.NewGuid().ToString("N");
            var (tmp, setAside) = ($"{_path}.{id}.tmp", $"{_path}.{id}.old.tmp");
            (DateTime, long, string) version;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                File.WriteAllText(tmp, json);
                // Version lue avant le renommage, qui garde date, taille et contenu : celle de NOTRE
                // fichier, même si un autre processus le remplace aussitôt (il sera alors relu).
                using (var written = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    version = FileVersion(written);
                }
                Shared(() =>
                {
                    MoveIntoPlace(tmp, setAside);
                    return true;
                });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                DeleteIfPossible(tmp);
                throw new IndexWriteException(_path, error.Message, error);
            }
            base.Replace(manifest, chunks, stored);
            _version = version;
            DeleteAbandonedTemporaries();
        }
    }

    /// <summary>
    /// Met le fichier écrit à la place de l'index : un lecteur lit l'ancien ou le nouveau, jamais un mélange.
    /// File.Replace plutôt que File.Move : sous Windows, File.Move échoue tant qu'un autre processus a le
    /// fichier ouvert, même pour un instant (<c>serve</c> l'ouvre à chaque question pour en lire la version) ;
    /// File.Replace réussit si ce lecteur partage la suppression, comme <see cref="ReadIfChanged"/>. En
    /// contrepartie, il laisse un très court instant sans fichier : voir <see cref="BeingReplaced"/>. Il met
    /// l'ancien index de côté sous <paramref name="setAside"/>, supprimé ensuite : sans ce nom, il en choisit
    /// un (<c>index.json~RF….TMP</c>) qu'il laisse sur le disque quand deux remplacements se croisent.
    /// </summary>
    protected virtual void MoveIntoPlace(string tmp, string setAside)
    {
        try
        {
            File.Replace(tmp, _path, setAside);
        }
        catch (FileNotFoundException) when (!File.Exists(_path))
        {
            File.Move(tmp, _path, overwrite: true);   // pas encore d'index
        }
        finally
        {
            DeleteIfPossible(setAside);   // y compris quand File.Replace a échoué à mi-chemin
        }
    }

    private static void DeleteIfPossible(string path)
    {
        try
        {
            File.Delete(path);   // absent : rien à faire
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Le fichier reste : nom unique, il ne gêne aucune autre écriture.
        }
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
        foreach (var (key, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))   // clés triées
        {
            result[key] = ToNode(value);
        }
        return result;
    }
}
