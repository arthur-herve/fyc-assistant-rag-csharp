// Adaptateurs : un index vectoriel en mémoire, et un index persisté en JSON. C'est volontairement
// simple. Pour quelques milliers de morceaux, une recherche exhaustive par similarité cosinus suffit,
// et une vraie base vectorielle se brancherait derrière le même port.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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

    /// <summary>Oublie l'index, par exemple quand son fichier a été supprimé.</summary>
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
            // Le contrôle porte sur l'état même qu'on va interroger : impossible de chercher dans un autre
            // index que celui qu'on vient de vérifier.
            throw new IndexReplacedException();
        }
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
/// Index persisté dans un fichier JSON lisible, ce qui est pratique pour l'enseignement. À chaque lecture
/// du manifeste, que les cas d'usage font avant de chercher, le fichier est relu s'il a changé. Un processus
/// long comme <c>serve</c> voit ainsi une réindexation faite en ligne de commande, droits compris. Il voit
/// aussi, comme la ligne de commande, qu'un fichier supprimé veut dire qu'il n'y a plus d'index.
/// La classe n'est pas scellée pour que les tests puissent simuler, via <see cref="MoveIntoPlace"/>, un
/// autre processus qui écrit au mauvais moment.
/// </summary>
public class JsonVectorIndex : InMemoryVectorIndex
{
    // Sous Windows, un fichier ouvert par un autre processus ne se remplace pas toujours, et il ne s'ouvre
    // pas pendant qu'on le remplace. Ces collisions durent quelques millisecondes : on réessaie brièvement.
    private static readonly int[] SharingDelaysMs = { 10, 20, 50, 100, 200 };
    // Un rédacteur renomme son fichier temporaire moins d'une seconde après l'avoir écrit, nouvelles tentatives
    // comprises. Un fichier temporaire de plus de deux minutes vient donc d'un rédacteur tué en pleine écriture,
    // et il ne fait plus attendre les lecteurs (BeingReplaced). Au-delà d'une heure, la prochaine écriture
    // réussie le supprime.
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DeletedAfter = TimeSpan.FromHours(1);
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
    private readonly Action<TimeSpan> _sleep;
    private readonly object _gate = new();
    // Version du fichier chargé : sa date, sa taille et son début. La date seule ne suffit pas, car deux
    // réécritures rapides peuvent tomber dans la même tranche d'horloge du disque. Le début du fichier
    // contient le manifeste, dont index_id et created_at changent à chaque reconstruction.
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
                Clear();   // fichier supprimé : plus d'index, comme pour la ligne de commande
                _version = null;
                return;
            }
            if (file.Content is null)
            {
                return;   // inchangé
            }
            try
            {
                // UTF-8 strict, BOM accepté, et JSON strict jusque dans les chaînes : un index abîmé est
                // signalé comme illisible.
                var root = JsonText.Parse(TextFiles.DecodeUtf8(file.Content), strings: true)!.AsObject();
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
            _version = file.Version;
        }
    }

    /// <summary>
    /// Lit la version et le contenu sur un même descripteur, ouvert avec FileShare.Delete pour ne pas bloquer
    /// un autre processus qui remplace le fichier (<see cref="MoveIntoPlace"/>). Le contenu n'est pas lu
    /// quand la version est celle déjà chargée.
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
                // Collision passagère avec un autre processus qui a ouvert le fichier ou qui le remplace. Les
                // autres erreurs, comme un index ou un dossier absent, ne se règlent pas en réessayant : on ne
                // fait pas attendre.
                _sleep(TimeSpan.FromMilliseconds(delay));
            }
        }
        return action();
    }

    /// <summary>
    /// Dit si l'index manque seulement le temps d'un remplacement (<see cref="MoveIntoPlace"/>). File.Replace met
    /// l'ancien fichier de côté avant d'installer le nouveau, qui garde jusque-là son nom temporaire. Pendant cet
    /// instant, on trouve des fichiers <c>index.json.….tmp</c> récents. Il se peut aussi que le nouveau fichier
    /// vienne d'être installé.
    /// </summary>
    private bool BeingReplaced() => Temporaries().Any(t => Age(t.Path) < AbandonedAfter) || File.Exists(_path);

    /// <summary>
    /// Temps écoulé depuis la dernière écriture. Un fichier disparu entre-temps est daté de 1601, donc très vieux.
    /// </summary>
    private static TimeSpan Age(string file) => DateTime.UtcNow - File.GetLastWriteTimeUtc(file);

    /// <summary>
    /// Les fichiers temporaires des écritures de cet index, nommés comme dans <see cref="Replace"/> :
    /// <c>index.json.&lt;id&gt;.tmp</c> pour le nouvel index, et <c>index.json.&lt;id&gt;.old.tmp</c>
    /// pour l'ancien, que File.Replace met de côté. L'identifiant doit être un Guid de 32 chiffres hexadécimaux,
    /// pour ne jamais toucher à un fichier qui ne vient pas de ces écritures.
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
    /// Après une écriture réussie, supprime les fichiers temporaires que des rédacteurs tués ont abandonnés depuis
    /// plus d'une heure. Un rédacteur encore actif ne perd jamais les siens. Son fichier temporaire est récent.
    /// L'ancien index qu'il met de côté garde la date de cet index et peut donc sembler vieux, mais tant que
    /// File.Replace n'a pas fini, le fichier temporaire du même identifiant est encore à côté de lui, ce qui le
    /// protège. Après, cet ancien index ne sert plus. Les fichiers temporaires sont examinés en premier : une
    /// paire abandonnée disparaît ainsi en une passe.
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
        if (_version is null)   // jamais chargé. Sinon, pas de relecture : indexId est vérifié sur l'état déjà chargé
        {
            Load();
        }
        return base.Search(vector, topK, predicate, indexId);
    }

    public override void Replace(IndexManifest manifest, IReadOnlyList<Chunk> chunks, IReadOnlyList<double[]> vectors)
    {
        // La mémoire garde exactement ce qui est écrit, arrondi compris : les scores restent les mêmes
        // après un redémarrage.
        var stored = vectors.Select(v => Normalize(v).Select(x => Math.Round(x, 6)).ToArray()).ToList();
        Check(manifest, chunks, stored);
        var json = Serialize(manifest, chunks, stored);
        lock (_gate)
        {
            // Le fichier d'abord, la mémoire ensuite : si l'écriture échoue, l'index en service reste le
            // précédent, en mémoire comme sur le disque. Chaque écriture a ses propres fichiers temporaires,
            // pour que deux réindexations simultanées n'écrivent jamais dans les mêmes.
            var id = Guid.NewGuid().ToString("N");
            var (tmp, setAside) = ($"{_path}.{id}.tmp", $"{_path}.{id}.old.tmp");
            (DateTime, long, string) version;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                File.WriteAllText(tmp, json);
                // La version est lue avant le renommage, qui conserve date, taille et contenu. C'est donc bien
                // celle de notre fichier, même si un autre processus le remplace aussitôt : il sera alors relu.
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
    /// Met le fichier écrit à la place de l'index : un lecteur lit l'ancien ou le nouveau, jamais un mélange des
    /// deux. On passe par File.Replace et non File.Move, car sous Windows File.Move échoue tant qu'un autre
    /// processus a le fichier ouvert, ne serait-ce qu'un instant, et <c>serve</c> l'ouvre à chaque question pour
    /// en lire la version. File.Replace réussit si ce lecteur partage la suppression, comme le fait
    /// <see cref="ReadIfChanged"/>. En contrepartie, il laisse le fichier absent un très court instant (voir
    /// <see cref="BeingReplaced"/>). L'ancien index est mis de côté sous <paramref name="setAside"/>, puis
    /// supprimé. Sans ce nom imposé, File.Replace en choisit un (<c>index.json~RF….TMP</c>) qu'il laisse sur le
    /// disque quand deux remplacements se croisent.
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
            // Le fichier reste, mais son nom est unique : il ne gêne aucune autre écriture.
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
