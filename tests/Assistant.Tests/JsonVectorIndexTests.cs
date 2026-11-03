// L'index vectoriel, en mémoire et dans son fichier JSON.

using Assistant.Application;
using Assistant.Domain;
using Assistant.Infrastructure;
using Xunit;

namespace Assistant.Tests;

public class JsonVectorIndexTests
{
    [Fact]
    public void Round_trip_through_the_file()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "index.json");
            var index = new JsonVectorIndex(path);
            var manifest = new IndexManifest("id", "m", 2, "fp", new Dictionary<string, object> { ["type"] = "whole" }, 1, 2, "t");
            var chunks = new[] { new Chunk("a#0", "a", "A", "x", 0, new HashSet<string> { "tous" }), new Chunk("a#1", "a", "A", "y", 1, new HashSet<string> { "rh" }) };
            index.Replace(manifest, chunks, new[] { new[] { 1.0, 0.0 }, new[] { 0.0, 1.0 } });

            var reloaded = new JsonVectorIndex(path);
            Assert.Equal("id", reloaded.Manifest()!.IndexId);
            var hits = reloaded.Search(new[] { 0.9, 0.1 }, 2, c => c.AllowedGroups.Contains("tous"));
            Assert.Single(hits);
            Assert.Equal("a#0", hits[0].Chunk.Id);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    private static readonly IndexManifest Manifest2 =
        new("id", "m", 2, "fp", new Dictionary<string, object> { ["type"] = "whole" }, 1, 2, "t");

    private static readonly Chunk[] TwoChunks =
    {
        new("a#0", "a", "A", "x", 0, new HashSet<string> { "tous" }),
        new("b#0", "b", "B", "y", 0, new HashSet<string> { "rh" }),
    };

    [Fact]
    public void Forbidden_passages_take_no_place_in_the_top_k()
    {
        // Pré-filtrage (ADR 0006) : un passage interdit mieux classé ne masque pas le passage autorisé.
        var index = new InMemoryVectorIndex();
        index.Replace(Manifest2, TwoChunks, new[] { new[] { 1.0, 0.0 }, new[] { 0.0, 1.0 } });
        var hits = index.Search(new[] { 0.0, 1.0 }, 1, c => !c.AllowedGroups.Contains("rh"));
        Assert.Equal("a#0", Assert.Single(hits).Chunk.Id);
    }

    [Fact]
    public void Rejects_a_query_of_the_wrong_dimension()
    {
        // Dans les deux sens : une requête plus longue que les vecteurs stockés, puis une plus courte, qui donnerait
        // sinon un score faux sans la moindre erreur.
        var index = new InMemoryVectorIndex();
        index.Replace(Manifest2 with { Dimension = 3 }, TwoChunks, new[] { new[] { 1.0, 0.0, 0.0 }, new[] { 0.0, 1.0, 0.0 } });
        var longer = Assert.Throws<ArgumentException>(() => index.Search(new[] { 1.0, 0.0, 0.0, 0.0 }, 2, _ => true));
        Assert.Equal("vecteur de requête de 4 dimensions, l'index en attend 3", longer.Message);
        var shorter = Assert.Throws<ArgumentException>(() => index.Search(new[] { 1.0, 0.0 }, 2, _ => true));
        Assert.Equal("vecteur de requête de 2 dimensions, l'index en attend 3", shorter.Message);
    }

    [Fact]
    public void An_unreadable_file_is_not_an_absent_index()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "index.json");
            File.WriteAllText(path, "{ pas du json");
            var index = new JsonVectorIndex(path);
            Assert.Throws<IndexUnreadableException>(() => index.Manifest());
            Assert.Throws<IndexUnreadableException>(() => index.Manifest());   // pas « aucun index » la deuxième fois
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void An_index_rebuilt_by_another_process_is_seen()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "index.json");
            new JsonVectorIndex(path).Replace(Manifest2, TwoChunks, new[] { new[] { 1.0, 0.0 }, new[] { 0.0, 1.0 } });
            var server = new JsonVectorIndex(path);   // le processus `serve`
            Assert.Equal("id", server.Manifest()!.IndexId);
            new JsonVectorIndex(path).Replace(Manifest2 with { IndexId = "id-2", ChunkCount = 1 }, TwoChunks[..1], new[] { new[] { 1.0, 0.0 } });
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(1));   // une seconde plus tard
            Assert.Equal("id-2", server.Manifest()!.IndexId);
            Assert.Single(server.Search(new[] { 1.0, 0.0 }, 5, _ => true));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void An_index_rebuilt_with_the_same_size_and_date_is_seen()
    {
        // Même taille, même date : seule la reconstruction (created_at) distingue les deux fichiers.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "index.json");
            var vectors = new[] { new[] { 1.0, 0.0 }, new[] { 0.0, 1.0 } };
            new JsonVectorIndex(path).Replace(Manifest2 with { CreatedAt = "t1" }, TwoChunks, vectors);
            var server = new JsonVectorIndex(path);   // le processus `serve`
            Assert.Equal("t1", server.Manifest()!.CreatedAt);
            var date = File.GetLastWriteTimeUtc(path);
            new JsonVectorIndex(path).Replace(Manifest2 with { CreatedAt = "t2" }, TwoChunks, vectors);
            File.SetLastWriteTimeUtc(path, date);
            Assert.Equal("t2", server.Manifest()!.CreatedAt);
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
