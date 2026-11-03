// Les instantanés sont des fichiers, que l'on peut éditer à la main. Un mauvais encodage, du JSON qui n'est pas
// strict ou un champ manquant donnent un message qui nomme le fichier et le champ, et ce qui est écrit se relit
// tel quel.

using System.Text;
using Assistant.Application;
using Assistant.Infrastructure;
using Xunit;

namespace Assistant.Tests;

public class SnapshotFileTests
{
    private const string Entry = """{"question_id": "q", "user_id": "a", "question": "?", "status": "answered", "cited_documents": ["d"], "text": "x", "attempts": 1}""";

    private static string Snapshot(string entry) => $$"""{"name": "a", "created_at": "t", "entries": [{{entry}}]}""";

    /// <summary>
    /// Le contenu de a.json et le problème attendu. Chaque champ est vérifié, et le message dit lequel est en faute.
    /// </summary>
    public static TheoryData<string, string> Malformed => new()
    {
        { """["a"]""", "un objet JSON est attendu" },
        { """{"name": "a", "created_at": "t"}""", "champ « entries » manquant ou qui n'est pas une liste" },
        { """{"name": "a", "created_at": "t", "entries": "x"}""", "champ « entries » manquant ou qui n'est pas une liste" },
        { """{"name": "a", "created_at": "t", "configuration": null, "entries": []}""", "« configuration » doit être un objet" },
        { """{"name": "a", "created_at": "t", "configuration": [], "entries": []}""", "« configuration » doit être un objet" },
        { """{"created_at": "t", "entries": []}""", "champ « name » manquant ou non textuel" },
        { """{"name": "a", "created_at": 5, "entries": []}""", "champ « created_at » manquant ou non textuel" },
        { """{"name": "a", "created_at": "t", "entries": ["x"]}""", "chaque réponse doit être un objet JSON" },
        { Snapshot(Entry.Replace("[\"d\"]", "\"abc\"")), "« cited_documents » doit être une liste de textes" },
        { Snapshot(Entry.Replace("[\"d\"]", "[1]")), "« cited_documents » doit être une liste de textes" },
        { Snapshot(Entry.Replace("\"cited_documents\": [\"d\"], ", "")), "« cited_documents » doit être une liste de textes" },
        { Snapshot(Entry.Replace("\"attempts\": 1", "\"attempts\": null")), "« attempts » doit être un entier" },
        { Snapshot(Entry.Replace("\"attempts\": 1", "\"attempts\": true")), "« attempts » doit être un entier" },
        { Snapshot(Entry.Replace("\"attempts\": 1", "\"attempts\": 1.5")), "« attempts » doit être un entier" },
        { Snapshot(Entry.Replace("\"attempts\": 1", "\"attempts\": \"1\"")), "« attempts » doit être un entier" },
        { Snapshot(Entry.Replace("\"attempts\": 1", "\"attempts\": 2147483648")), "« attempts » doit être un entier" },   // int.MaxValue + 1
        { Snapshot(Entry.Replace("\"attempts\": 1", "\"attempts\": -2147483649")), "« attempts » doit être un entier" },
        { Snapshot(Entry.Replace("\"attempts\": 1", "\"attempts\": 1" + new string('0', 4300))), "« attempts » doit être un entier" },
        { Snapshot(Entry.Replace("\"question_id\": \"q\", ", "")), "champ « question_id » manquant ou non textuel" },
        { Snapshot(Entry.Replace("\"user_id\": \"a\"", "\"user_id\": null")), "champ « user_id » manquant ou non textuel" },
        { Snapshot(Entry.Replace("\"text\": \"x\"", "\"text\": 5")), "champ « text » manquant ou non textuel" },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void A_malformed_snapshot_names_the_file_and_the_field(string content, string problem)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("a.json"), content);
        var error = Assert.Throws<FormatException>(() => new JsonSnapshotStore(dir.Path).Load("a"));
        Assert.Equal($"instantané illisible ({dir.File("a.json")}) : {problem}", error.Message);
    }

    [Fact]
    public void Optional_fields_take_their_default()
    {
        // Un instantané écrit par une version plus ancienne n'a ni « configuration » ni « attempts ». Il doit rester
        // lisible, avec les valeurs par défaut.
        using var dir = new TempDir();
        File.WriteAllText(dir.File("a.json"), Snapshot(Entry.Replace(", \"attempts\": 1", "")));
        var snapshot = new JsonSnapshotStore(dir.Path).Load("a");
        Assert.Empty(snapshot.Configuration);
        Assert.Equal(0, snapshot.Entries[0].Attempts);
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Attempts_take_any_int(int attempts)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("a.json"), Snapshot(Entry.Replace("\"attempts\": 1", $"\"attempts\": {attempts}")));
        Assert.Equal(attempts, new JsonSnapshotStore(dir.Path).Load("a").Entries[0].Attempts);
    }

    [Fact]
    public void A_byte_order_mark_is_accepted_and_latin_1_is_refused()
    {
        using var dir = new TempDir();
        const string content = """{"name": "Congés", "created_at": "t", "entries": []}""";
        File.WriteAllBytes(dir.File("a.json"), Bytes.WithBom(Encoding.UTF8.GetBytes(content)));
        Assert.Equal("Congés", new JsonSnapshotStore(dir.Path).Load("a").Name);
        // Le Latin-1 est refusé avec ou sans marque d'ordre des octets, et la position se compte après la marque.
        foreach (var bytes in new[] { Encoding.Latin1.GetBytes(content), Bytes.WithBom(Encoding.Latin1.GetBytes(content)) })
        {
            File.WriteAllBytes(dir.File("a.json"), bytes);
            var error = Assert.Throws<FormatException>(() => new JsonSnapshotStore(dir.Path).Load("a"));
            Assert.Equal($"instantané illisible ({dir.File("a.json")}) : pas en UTF-8 (octet 0xe9 à la position {content.IndexOf('é')})",
                         error.Message);
        }
    }

    [Fact]
    public void A_deeply_nested_snapshot_is_unreadable_not_a_crash()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("a.json"), $$"""{"name": "a", "created_at": "t", "configuration": {"x": {{Bytes.Deep}}}, "entries": []}""");
        var error = Assert.Throws<FormatException>(() => new JsonSnapshotStore(dir.Path).Load("a"));
        Assert.StartsWith($"instantané illisible ({dir.File("a.json")}) : ", error.Message);
    }

    /// <summary>
    /// Du JSON qui n'est pas strict. System.Text.Json garderait sans rien dire une des valeurs d'une clé en
    /// double, et refuserait en anglais plus de 64 niveaux d'imbrication ou une chaîne qui n'est pas du texte.
    /// Chaque cas a ici son message en français.
    /// </summary>
    public static TheoryData<string, string> NotStrict => new()
    {
        { """{"name": "a", "name": "b", "created_at": "t", "entries": []}""", "clé « name » en double" },
        { """{"name": "a", "created_at": "t", "configuration": {"x": 1, "x": 2}, "entries": []}""", "clé « x » en double" },
        { Snapshot(Entry.Replace("\"q\"", "\"q\", \"question_id\": \"r\"")), "clé « question_id » en double" },
        { $$"""{"name": "a", "created_at": "t", "configuration": {"x": {{Bytes.Nested(63)}}}, "entries": []}""",
          "JSON trop imbriqué : plus de 64 niveaux" },
        { Snapshot(Entry.Replace("\"x\"", "\"x\\ud800\"")), Bytes.NotText },
        { """{"name": "a", "created_at": "t", "configuration": {"\udc00": 1}, "entries": []}""", Bytes.NotText },
    };

    [Theory]
    [MemberData(nameof(NotStrict))]
    public void A_snapshot_that_is_not_strict_json_is_unreadable(string content, string problem)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("a.json"), content);
        var error = Assert.Throws<FormatException>(() => new JsonSnapshotStore(dir.Path).Load("a"));
        Assert.Equal($"instantané illisible ({dir.File("a.json")}) : {problem}", error.Message);
    }

    [Fact]
    public void Sixty_four_levels_are_read()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("a.json"), $$"""{"name": "a", "created_at": "t", "configuration": {"x": {{Bytes.Nested(62)}}}, "entries": []}""");
        Assert.Equal("a", new JsonSnapshotStore(dir.Path).Load("a").Name);
    }

    private static readonly Dictionary<string, object> Splitter = new()
    {
        ["type"] = "paragraph", ["max_chars"] = 800, ["overlap_chars"] = 120, ["include_title"] = true,
    };

    /// <summary>Une configuration dont les clés, comme celles du découpage, ne sont pas dans l'ordre.</summary>
    private static readonly Dictionary<string, object?> Configuration = new()
    {
        ["corpus"] = "solveo", ["embedding_model"] = "hashing", ["splitter"] = Splitter, ["top_k"] = 4, ["min_score"] = 0.4,
        ["temperature"] = 1.0, ["seed"] = null,
    };

    private static readonly Snapshot Reference = new("ref", "2026-09-11T12:00:00+00:00", Configuration, new[]
    {
        new SnapshotEntry("q1", "alice", "Combien de congés ?", "answered", new[] { "a", "b" }, Bytes.Special, 1),
        new SnapshotEntry("q2", "bruno", "Et ?", "no_relevant_source", Array.Empty<string>(), "Rien.", 0),
    });

    [Fact]
    public void A_snapshot_reads_back_the_same()
    {
        // Écrit puis relu, l'instantané redonne les mêmes valeurs, y compris les textes délicats de Bytes.Special.
        using var dir = new TempDir();
        var store = new JsonSnapshotStore(dir.Path);
        store.Save(Reference);
        var read = store.Load("ref");
        Assert.Equal((Reference.Name, Reference.CreatedAt), (read.Name, read.CreatedAt));
        Assert.Empty(SnapshotComparer.Compare(Reference, read).ConfigurationDifferences);   // 1.0 se relit 1 : même valeur
        Assert.Equivalent(Reference.Entries, read.Entries, strict: true);
    }

    [Fact]
    public void A_snapshot_file_keeps_accents_as_they_are()
    {
        using var dir = new TempDir();
        new JsonSnapshotStore(dir.Path).Save(Reference);
        Assert.Contains("\"question\": \"Combien de congés ?\"", File.ReadAllText(dir.File("ref.json")));
    }

    [Fact]
    public void The_store_sorts_the_keys_it_writes()
    {
        // Le magasin trie les clés des dictionnaires qu'il écrit : la configuration et le découpage qu'elle contient
        // reviennent donc en ordre trié.
        using var dir = new TempDir();
        var store = new JsonSnapshotStore(dir.Path);
        store.Save(Reference);
        var read = store.Load("ref");
        Assert.Equal(Configuration.Keys.Order(StringComparer.Ordinal), read.Configuration.Keys);
        Assert.Equal(Splitter.Keys.Order(StringComparer.Ordinal), Assert.IsType<Dictionary<string, object>>(read.Configuration["splitter"]).Keys);
    }
}
