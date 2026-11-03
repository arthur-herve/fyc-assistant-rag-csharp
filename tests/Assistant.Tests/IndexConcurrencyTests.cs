// L'index sur disque est partagé entre processus : il est relu quand il change, et deux index ne sont
// jamais mélangés (S4.2). Ici, les « autres processus » sont d'autres instances de JsonVectorIndex sur
// le même fichier, comme `serve` et la ligne de commande sur data/index.json.

using Assistant.Application;
using Assistant.Domain;
using Assistant.Infrastructure;
using Xunit;

namespace Assistant.Tests;

public sealed class IndexConcurrencyTests : IDisposable
{
    private static readonly double[] Query = Fakes.Vocabulary.Select((_, i) => i == 0 ? 1.0 : 0.0).ToArray();   // « télétravail »

    private readonly string _dir = Directory.CreateTempSubdirectory("fyc-index-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string IndexPath => Path.Combine(_dir, "index.json");

    private sealed record IndexContent(IndexManifest Manifest, IReadOnlyList<Chunk> Chunks, IReadOnlyList<double[]> Vectors)
    {
        public void ReplaceIn(IVectorIndex index) => index.Replace(Manifest, Chunks, Vectors);
    }

    /// <summary>Un index d'un seul morceau, qui parle de télétravail.</summary>
    private static IndexContent Content(string indexId, string docId, string group = "tous", string model = "fake-keywords",
                                        int? dimension = null)
    {
        var size = dimension ?? Fakes.Vocabulary.Length;
        var manifest = new IndexManifest(indexId, model, size, "empreinte", new Dictionary<string, object> { ["type"] = "whole" },
                                         1, 1, "2026-10-01T12:00:00+00:00");
        var chunk = new Chunk($"{docId}#0", docId, docId, $"Télétravail selon {docId}.", 0, new HashSet<string> { group });
        return new IndexContent(manifest, new[] { chunk }, new[] { Enumerable.Range(0, size).Select(i => i == 0 ? 1.0 : 0.0).ToArray() });
    }

    /// <summary>Imite `index` en ligne de commande : une nouvelle instance à chaque appel.</summary>
    private void Write(IndexContent content) => content.ReplaceIn(new JsonVectorIndex(IndexPath));

    /// <summary>Embedder qui laisse un autre processus agir pendant son premier appel.</summary>
    private sealed class Meanwhile : IEmbedder
    {
        private readonly IEmbedder _inner;
        private Action? _action;

        public Meanwhile(IEmbedder inner, Action action) => (_inner, _action) = (inner, action);

        public EmbeddingBatch EmbedQuery(string text)
        {
            var action = _action;
            _action = null;
            action?.Invoke();
            return _inner.EmbedQuery(text);
        }

        public EmbeddingBatch EmbedDocuments(IReadOnlyList<string> texts) => _inner.EmbedDocuments(texts);
    }

    /// <summary>
    /// Imite un autre processus arrêté au milieu de son File.Replace : l'ancien index est déjà mis de côté, le nouveau
    /// porte encore son nom temporaire. Renvoie l'action qui termine le remplacement.
    /// </summary>
    private Action HalfReplaced(IndexContent next)
    {
        var id = Guid.NewGuid().ToString("N");
        var tmp = $"{IndexPath}.{id}.tmp";
        next.ReplaceIn(new JsonVectorIndex(tmp));
        File.Move(IndexPath, $"{IndexPath}.{id}.old.tmp");
        return () => File.Move(tmp, IndexPath);
    }

    private sealed class FailingWrite : JsonVectorIndex
    {
        public FailingWrite(string path) : base(path, sleep: _ => { }) { }

        // Comme un fichier en lecture seule : réessayer n'y change rien.
        protected override void MoveIntoPlace(string tmp, string setAside) => throw new UnauthorizedAccessException("Access to the path is denied.");
    }

    /// <summary>
    /// Un autre processus remplace l'index en même temps : pendant son File.Replace, l'index manque un instant.
    /// </summary>
    private sealed class ReplacedAtTheSameTime : JsonVectorIndex
    {
        private bool _collided;

        public ReplacedAtTheSameTime(string path) : base(path, sleep: _ => { }) { }

        protected override void MoveIntoPlace(string tmp, string setAside)
        {
            if (!_collided)
            {
                _collided = true;
                throw new FileNotFoundException("Unable to find the specified file.");
            }
            base.MoveIntoPlace(tmp, setAside);
        }
    }

    /// <summary>
    /// File.Replace échoue à mi-chemin, comme quand deux remplacements se croisent
    /// (ERROR_UNABLE_TO_MOVE_REPLACEMENT_2) : l'ancien index porte déjà le nom de côté, le nouveau encore son nom
    /// temporaire.
    /// </summary>
    private sealed class HalfFailedReplace : JsonVectorIndex
    {
        private readonly string _path;
        private bool _failed;

        public HalfFailedReplace(string path) : base(path, sleep: _ => { }) => _path = path;

        protected override void MoveIntoPlace(string tmp, string setAside)
        {
            if (!_failed)
            {
                _failed = true;
                File.Move(_path, setAside);
                throw new IOException("Unable to move the replacement file to the file to be replaced.");
            }
            base.MoveIntoPlace(tmp, setAside);
        }
    }

    /// <summary>Un autre processus réécrit l'index juste avant, ou juste après, notre renommage.</summary>
    private sealed class OtherWriterAround : JsonVectorIndex
    {
        private readonly bool _after;
        private Action? _other;

        public OtherWriterAround(string path, Action other, bool after) : base(path) => (_other, _after) = (other, after);

        protected override void MoveIntoPlace(string tmp, string setAside)
        {
            var other = _other;
            _other = null;
            if (!_after)
            {
                other?.Invoke();
            }
            base.MoveIntoPlace(tmp, setAside);
            if (_after)
            {
                other?.Invoke();
            }
        }
    }

    [Fact]
    public void A_question_searches_the_index_whose_model_it_checked()
    {
        // Pendant une question, l'index passe de A à B puis revient à A : deux réindexations par la ligne de
        // commande, et entre les deux une autre requête de `serve` qui recharge B. Les passages viennent bien
        // de A, l'index annoncé.
        Write(Content("index-a", "a"));
        var server = new JsonVectorIndex(IndexPath);
        var embedder = new Meanwhile(new KeywordEmbedder(), () =>
        {
            Write(Content("index-b", "b"));
            server.Manifest();
            Write(Content("index-a", "a"));
        });
        var retrieval = new SearchPassages(embedder, server).Execute(Fakes.Alice, "télétravail", 4);
        Assert.Equal("index-a", retrieval.Manifest.IndexId);
        Assert.Equal(new[] { "a" }, retrieval.Passages.Select(p => p.Chunk.DocumentId));
    }

    [Fact]
    public void An_index_of_another_dimension_in_between_is_not_a_crash()
    {
        // L'index passe de A à C, d'une autre dimension, puis revient à A : on recommence sur A au lieu de répondre 500.
        Write(Content("index-a", "a"));
        var server = new JsonVectorIndex(IndexPath);
        var embedder = new Meanwhile(new KeywordEmbedder(), () =>
        {
            Write(Content("index-c", "c", model: "autre-modele", dimension: 2 * Fakes.Vocabulary.Length));
            server.Manifest();
            Write(Content("index-a", "a"));
        });
        var retrieval = new SearchPassages(embedder, server).Execute(Fakes.Alice, "télétravail", 4);
        Assert.Equal(new[] { "a" }, retrieval.Passages.Select(p => p.Chunk.DocumentId));
    }

    [Fact]
    public void The_cache_reloading_the_index_does_not_mix_two_indexes()
    {
        // Au milieu de la question, le cache relit le manifeste, donc le fichier, comme dans Composition
        // (currentIndex: index.Manifest). B est chargé à ce moment-là, puis A revient pendant l'appel au
        // service. La recherche ne porte quand même que sur A.
        Write(Content("index-a", "a"));
        var server = new JsonVectorIndex(IndexPath);
        var first = true;
        var embedder = new CachedEmbedder(new Meanwhile(new KeywordEmbedder(), () => Write(Content("index-a", "a"))), () =>
        {
            if (first)
            {
                first = false;
                Write(Content("index-b", "b"));
            }
            return server.Manifest();
        });
        var retrieval = new SearchPassages(embedder, server).Execute(Fakes.Alice, "télétravail", 4);
        Assert.Equal("index-a", retrieval.Manifest.IndexId);
        Assert.Equal(new[] { "a" }, retrieval.Passages.Select(p => p.Chunk.DocumentId));
    }

    [Fact]
    public void A_deleted_index_file_means_no_index()
    {
        // Plus de fichier, plus d'index : comme le voient la ligne de commande et un `serve` redémarré.
        Write(Content("index-a", "a"));
        var server = new JsonVectorIndex(IndexPath, sleep: _ => { });
        Assert.Equal("index-a", server.Manifest()!.IndexId);
        File.Delete(IndexPath);
        Assert.Null(server.Manifest());
        Assert.Null(new JsonVectorIndex(IndexPath).Manifest());
        Assert.Throws<IndexNotBuiltException>(() => new SearchPassages(new KeywordEmbedder(), server).Execute(Fakes.Alice, "télétravail", 4));
        Write(Content("index-b", "b"));
        Assert.Equal("index-b", server.Manifest()!.IndexId);
    }

    [Fact]
    public void No_index_at_all_is_answered_without_waiting()
    {
        // Sans fichier, sans dossier, ou avec un fichier supprimé sans remplacement en cours, la réponse « aucun
        // index » vient tout de suite. Seules les collisions passagères, un fichier ouvert ou en cours de
        // remplacement, méritent une nouvelle tentative.
        var slept = new List<TimeSpan>();
        Assert.Null(new JsonVectorIndex(IndexPath, sleep: slept.Add).Manifest());
        Assert.Null(new JsonVectorIndex(Path.Combine(_dir, "absent", "index.json"), sleep: slept.Add).Manifest());
        Write(Content("index-a", "a"));
        var server = new JsonVectorIndex(IndexPath, sleep: slept.Add);
        Assert.Equal("index-a", server.Manifest()!.IndexId);
        File.Delete(IndexPath);
        Assert.Null(server.Manifest());
        Assert.Empty(slept);
    }

    [Fact]
    public void An_index_missing_for_a_moment_during_a_replacement_is_not_a_deleted_index()
    {
        // File.Replace laisse un très court instant sans index.json : on réessaie au lieu de conclure qu'il n'y a pas
        // d'index, qu'un index soit déjà chargé (`serve`) ou non (`ask`, `status`, premier appel de `serve`).
        Write(Content("index-a", "a"));
        Action? finish = null;
        Action<TimeSpan> sleep = _ =>
        {
            finish?.Invoke();   // l'autre processus termine son remplacement
            finish = null;
        };
        var server = new JsonVectorIndex(IndexPath, sleep);
        Assert.Equal("index-a", server.Manifest()!.IndexId);
        finish = HalfReplaced(Content("index-b", "b"));
        Assert.Equal("index-b", server.Manifest()?.IndexId);
        finish = HalfReplaced(Content("index-c", "c"));
        Assert.Equal("index-c", new JsonVectorIndex(IndexPath, sleep).Manifest()?.IndexId);
    }

    [Fact]
    public void A_temporary_file_abandoned_by_a_killed_writer_does_not_make_readers_wait()
    {
        // Un rédacteur tué au milieu d'une écriture laisse son fichier temporaire derrière lui. S'il n'y a pas d'index,
        // chaque lecture attendrait un remplacement qui ne viendra jamais. Au-delà de deux minutes, ce fichier ne compte
        // plus. Plus récent, il peut appartenir à un remplacement en cours, et on réessaie.
        var tmp = $"{IndexPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, "{\"manifest\": {");
        var slept = new List<TimeSpan>();
        File.SetLastWriteTimeUtc(tmp, DateTime.UtcNow - TimeSpan.FromMinutes(3));
        Assert.Null(new JsonVectorIndex(IndexPath, sleep: slept.Add).Manifest());
        Assert.Empty(slept);
        File.SetLastWriteTimeUtc(tmp, DateTime.UtcNow - TimeSpan.FromMinutes(1));
        Assert.Null(new JsonVectorIndex(IndexPath, sleep: slept.Add).Manifest());
        Assert.NotEmpty(slept);
    }

    [Fact]
    public void Temporary_files_abandoned_for_more_than_an_hour_are_deleted_by_the_next_write()
    {
        // Le ménage ne touche jamais un fichier dont un rédacteur actif a encore besoin. Son fichier temporaire est récent.
        // L'ancien index qu'il met de côté pendant File.Replace (index.json.<id>.old.tmp) garde la date de cet ancien
        // index et peut donc paraître vieux, mais son fichier temporaire est encore à côté de lui. Il faut que ce soit
        // un fichier : un dossier du même nom ne le protège pas.
        string Temporary(string name, TimeSpan age, bool folder = false)
        {
            var path = Path.Combine(_dir, name);
            if (folder)
            {
                Directory.CreateDirectory(path);
                Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
            }
            else
            {
                File.WriteAllText(path, "{}");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
            }
            return name;
        }
        IEnumerable<string?> Names() => Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).Order();
        var (killed, active, besideAFolder) = (Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        var hours = TimeSpan.FromHours(2);
        Temporary($"index.json.{killed}.tmp", hours);   // rédacteur tué au milieu de File.Replace
        Temporary($"index.json.{killed}.old.tmp", hours);
        Temporary($"index.json.{Guid.NewGuid():N}.old.tmp", hours);   // tué juste après
        Temporary($"index.json.{Guid.NewGuid():N}.tmp", TimeSpan.FromMinutes(61));   // abandonné depuis un peu plus d'une heure
        Temporary($"index.json.{besideAFolder}.old.tmp", hours);   // à côté d'un dossier, pas d'un fichier temporaire
        var kept = new[]
        {
            Temporary($"index.json.{Guid.NewGuid():N}.tmp", TimeSpan.FromMinutes(59)),   // abandonné depuis un peu moins d'une heure
            Temporary($"index.json.{active}.tmp", TimeSpan.Zero),   // rédacteur actif, au milieu de File.Replace
            Temporary($"index.json.{active}.old.tmp", hours),
            Temporary("index.json.sauvegarde.tmp", hours),   // pas des fichiers temporaires de l'index
            Temporary($"index.json.{Guid.NewGuid():N}.tmp.bak", hours),
            Temporary($"index.json.{Guid.NewGuid().ToString("N").ToUpperInvariant()}.tmp", hours),   // majuscules : jamais écrit ainsi
            Temporary($"index.json.{Guid.NewGuid().ToString("N")[..31]}.tmp", hours),   // 31 chiffres, puis 33 : jamais écrits ainsi
            Temporary($"index.json.{Guid.NewGuid():N}0.tmp", hours),
            Temporary($"indexXjson.{Guid.NewGuid():N}.tmp", hours),   // le « . » du nom de l'index, pris à la lettre
            Temporary($"index.json.{besideAFolder}.tmp", hours, folder: true),   // dossier : pas un fichier temporaire
        };
        var before = Names().ToList();
        // Une écriture qui échoue ne fait pas le ménage.
        Assert.Throws<IndexWriteException>(() => Content("index-a", "a").ReplaceIn(new FailingWrite(IndexPath)));
        Assert.Equal(before, Names());
        Write(Content("index-a", "a"));
        Assert.Equal(kept.Append("index.json").Order(), Names());
    }

    [Fact]
    public void A_failed_write_leaves_the_index_in_service_unchanged()
    {
        // Le fichier d'abord, la mémoire ensuite : `serve` ne sert jamais un index absent du disque.
        Write(Content("index-a", "a"));
        var server = new FailingWrite(IndexPath);
        Assert.Equal("index-a", server.Manifest()!.IndexId);
        Assert.Throws<IndexWriteException>(() => Content("index-b", "b").ReplaceIn(server));
        Assert.Equal("index-a", server.Manifest()!.IndexId);
        Assert.Equal(new[] { "a" }, server.Search(Query, 4, _ => true).Select(p => p.Chunk.DocumentId));
        Assert.Equal("index-a", new JsonVectorIndex(IndexPath).Manifest()!.IndexId);
    }

    [Fact]
    public void A_failed_write_leaves_no_temporary_file()
    {
        Write(Content("index-a", "a"));
        Assert.Throws<IndexWriteException>(() => Content("index-b", "b").ReplaceIn(new FailingWrite(IndexPath)));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));   // le fichier temporaire ne traîne pas
    }

    [Fact]
    public void A_failed_write_names_the_index_file()
    {
        // Le message nomme le fichier d'index, et non le fichier temporaire que l'écriture n'a pas pu installer.
        Write(Content("index-a", "a"));
        var error = Assert.Throws<IndexWriteException>(() => Content("index-b", "b").ReplaceIn(new FailingWrite(IndexPath)));
        Assert.Contains($"écriture impossible de l'index ({IndexPath})", error.Message);
    }

    [Fact]
    public void An_invalid_index_is_refused_before_anything_is_written()
    {
        // Des vecteurs de la mauvaise dimension sont refusés avant d'écrire : le fichier n'est jamais remplacé
        // par un index illisible, et l'index en service reste le précédent.
        Write(Content("index-a", "a"));
        var server = new JsonVectorIndex(IndexPath);
        Assert.Equal("index-a", server.Manifest()!.IndexId);
        var b = Content("index-b", "b");
        Assert.Throws<ArgumentException>(() => server.Replace(b.Manifest, b.Chunks, new[] { b.Vectors[0].Append(0.0).ToArray() }));
        Assert.Equal("index-a", new JsonVectorIndex(IndexPath).Manifest()!.IndexId);
        Assert.Equal("index-a", server.Manifest()!.IndexId);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void A_replacement_that_fails_half_way_leaves_no_copy_of_the_old_index()
    {
        // Quand deux remplacements se croisent, File.Replace peut échouer après avoir mis l'ancien index de côté :
        // la nouvelle tentative installe le nouveau, et la copie de l'ancien ne reste pas sur le disque.
        Write(Content("index-a", "a"));
        Content("index-b", "b").ReplaceIn(new HalfFailedReplace(IndexPath));
        Assert.Equal("index-b", new JsonVectorIndex(IndexPath).Manifest()!.IndexId);
        Assert.Equal(new[] { "index.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void The_version_recorded_is_that_of_the_file_this_process_wrote()
    {
        // Un autre processus réécrit l'index juste après notre renommage : on le relira, droits compris,
        // au lieu de garder pour toujours en mémoire un index qui n'est plus sur le disque.
        var server = new OtherWriterAround(IndexPath, () => Write(Content("index-b", "b", group: "rh")), after: true);
        Content("index-a", "a").ReplaceIn(server);
        Assert.Equal("index-b", server.Manifest()!.IndexId);
        Assert.Equal(new[] { "rh" }, server.Search(Query, 4, _ => true)[0].Chunk.AllowedGroups);
    }

    [Fact]
    public void Two_simultaneous_reindexations_do_not_share_a_temporary_file()
    {
        // Une autre réindexation complète pendant la nôtre : chacune son fichier temporaire, la dernière
        // installée gagne, et tout le monde la voit.
        var other = new JsonVectorIndex(IndexPath);
        var server = new OtherWriterAround(IndexPath, () => Content("index-b", "b").ReplaceIn(other), after: false);
        Content("index-a", "a").ReplaceIn(server);
        Assert.Equal("index-a", new JsonVectorIndex(IndexPath).Manifest()!.IndexId);
        Assert.Equal("index-a", server.Manifest()!.IndexId);
        Assert.Equal("index-a", other.Manifest()!.IndexId);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Two_replacements_at_the_same_instant_both_succeed()
    {
        // File.Replace d'un autre processus laisse l'index absent un instant : on réessaie, comme pour un fichier ouvert.
        Write(Content("index-a", "a"));
        Content("index-b", "b").ReplaceIn(new ReplacedAtTheSameTime(IndexPath));
        Assert.Equal("index-b", new JsonVectorIndex(IndexPath).Manifest()!.IndexId);
    }

    [Fact]
    public void A_reader_holding_the_file_does_not_make_the_reindexation_fail()
    {
        // Sous Windows, un fichier ouvert sans partager la suppression, par une autre application par exemple, ne se
        // remplace pas : on réessaie brièvement. Ailleurs, il se remplace du premier coup.
        Write(Content("index-a", "a"));
        var reader = new FileStream(IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Content("index-b", "b").ReplaceIn(new JsonVectorIndex(IndexPath, sleep: _ => reader.Dispose()));   // le lecteur a fini entre-temps
        }
        finally
        {
            reader.Dispose();
        }
        Assert.Equal("index-b", new JsonVectorIndex(IndexPath).Manifest()!.IndexId);
    }

    [Fact]
    public void A_reader_sharing_the_deletion_never_blocks_a_reindexation()
    {
        // Les lectures de JsonVectorIndex partagent la suppression (FileShare.Delete) : `serve` qui relit
        // l'index ne bloque jamais `index` en ligne de commande, même un instant.
        Write(Content("index-a", "a"));
        var slept = new List<TimeSpan>();
        using (new FileStream(IndexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Content("index-b", "b").ReplaceIn(new JsonVectorIndex(IndexPath, sleep: slept.Add));
        }
        Assert.Empty(slept);
        Assert.Equal("index-b", new JsonVectorIndex(IndexPath).Manifest()!.IndexId);
    }

    [Fact]
    public void Reading_the_index_never_waits_for_a_process_that_is_replacing_it()
    {
        // Un processus qui remplace le fichier l'ouvre avec le droit d'écrire et de le supprimer (ce que donne
        // ici DeleteOnClose) : les lectures de JsonVectorIndex partagent l'écriture et la suppression, et passent.
        Write(Content("index-a", "a"));
        var slept = new List<TimeSpan>();
        using (new FileStream(IndexPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 4096,
                              FileOptions.DeleteOnClose))
        {
            Assert.Equal("index-a", new JsonVectorIndex(IndexPath, sleep: slept.Add).Manifest()!.IndexId);
        }
        Assert.Empty(slept);
    }

    [Fact]
    public void A_file_that_cannot_be_opened_for_a_moment_is_read_again()
    {
        // Pendant qu'un autre processus le remplace, le fichier ne s'ouvre pas un court instant.
        Write(Content("index-a", "a"));
        var holder = new FileStream(IndexPath, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            Assert.Equal("index-a", new JsonVectorIndex(IndexPath, sleep: _ => holder.Dispose()).Manifest()!.IndexId);
        }
        finally
        {
            holder.Dispose();
        }
    }
}
