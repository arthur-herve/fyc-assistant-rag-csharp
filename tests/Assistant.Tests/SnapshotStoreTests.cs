// Le magasin des instantanés : ce qui est écrit se relit, et un fichier incomplet ou un nom inconnu ou invalide
// donnent un message clair.

using Assistant.Application;
using Assistant.Infrastructure;
using Xunit;

namespace Assistant.Tests;

public class SnapshotStoreTests
{
    [Theory]
    [InlineData("""{"name": "a", "created_at": "t"}""")]                                   // sans entries
    [InlineData("""{"name": "a", "created_at": "t", "entries": [{"status": "answered"}]}""")]
    [InlineData("""["a"]""")]
    public void An_incomplete_snapshot_names_the_file(string content)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "a.json"), content);
            var error = Assert.Throws<FormatException>(() => new JsonSnapshotStore(dir.FullName).Load("a"));
            Assert.Contains("a.json", error.Message);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Snapshot_store_round_trip_and_names()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var store = new JsonSnapshotStore(Path.Combine(dir.FullName, "instantanes"));
            Assert.Empty(store.Names());
            var snapshot = new Snapshot("ref", "2026-09-11T12:00:00+00:00",
                new Dictionary<string, object?> { ["generation_model"] = "extractive", ["top_k"] = 4, ["splitter"] = new Dictionary<string, object> { ["max_chars"] = 800 } },
                new[] { new SnapshotEntry("q1", "alice", "Q ?", "answered", new[] { "a", "b" }, "Texte [1]") });
            store.Save(snapshot);
            var loaded = store.Load("ref");
            Assert.Equal(snapshot.Entries[0] with { CitedDocuments = Array.Empty<string>() }, loaded.Entries[0] with { CitedDocuments = Array.Empty<string>() });
            Assert.Equal(new[] { "a", "b" }, loaded.Entries[0].CitedDocuments);
            Assert.Equal("extractive", loaded.Configuration["generation_model"]);
            Assert.Equal(4, loaded.Configuration["top_k"]);
            Assert.Empty(SnapshotComparer.Compare(snapshot, loaded).ConfigurationDifferences);   // relu du disque, rien ne diffère
            Assert.Equal(new[] { "ref" }, store.Names());
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Theory]
    [InlineData("../autre")]
    [InlineData("ref\n")]   // « $ » laisserait passer un saut de ligne final
    [InlineData("a b")]
    public void An_invalid_name_is_refused_with_the_allowed_characters(string name)
    {
        // Un nom invalide est cité entre « », suivi des caractères permis.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var store = new JsonSnapshotStore(Path.Combine(dir.FullName, "instantanes"));
            Assert.Equal($"nom d'instantané invalide : « {name} » (lettres, chiffres, . _ - ; 64 caractères au plus)",
                         Assert.Throws<InvalidSnapshotNameException>(() => store.Load(name)).Message);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void An_unknown_snapshot_names_the_known_ones_without_brackets()
    {
        // Les noms connus s'affichent sans crochets : ni « [b, ref] », ni « [] » pour un dossier vide.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var store = new JsonSnapshotStore(Path.Combine(dir.FullName, "instantanes"));
            var snapshot = new Snapshot("ref", "2026-09-11T12:00:00+00:00", new Dictionary<string, object?>(), Array.Empty<SnapshotEntry>());
            foreach (var (saved, known) in new[] { (Array.Empty<string>(), "aucun"), (new[] { "ref", "b" }, "b, ref") })
            {
                foreach (var name in saved)
                {
                    store.Save(snapshot with { Name = name });
                }
                Assert.Equal($"instantané introuvable : absent (connus : {known})",
                             Assert.Throws<SnapshotNotFoundException>(() => store.Load("absent")).Message);
            }
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void A_folder_named_like_a_snapshot_is_not_one()
    {
        // Seuls les fichiers comptent (Directory.GetFiles, File.Exists) : un dossier « dossier.json » n'est pas listé, et
        // on ne peut pas le charger : il est « introuvable ».
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var store = new JsonSnapshotStore(dir.FullName);
            store.Save(new Snapshot("ref", "2026-09-11T12:00:00+00:00", new Dictionary<string, object?>(), Array.Empty<SnapshotEntry>()));
            Directory.CreateDirectory(Path.Combine(dir.FullName, "dossier.json"));
            Assert.Equal(new[] { "ref" }, store.Names());
            Assert.Equal("instantané introuvable : dossier (connus : ref)",
                         Assert.Throws<SnapshotNotFoundException>(() => store.Load("dossier")).Message);
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
