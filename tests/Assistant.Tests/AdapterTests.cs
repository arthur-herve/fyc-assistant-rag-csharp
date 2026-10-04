// Adaptateurs : encodage des fichiers, fins de ligne et blancs du corpus, instantanés et index (lus strictement,
// relus tels qu'écrits), réponses du service IA d'une autre forme que le contrat.

using System.Net;
using System.Text;
using System.Text.Json;
using Assistant.Application;
using Assistant.Domain;
using Assistant.Infrastructure;
using Xunit;

namespace Assistant.Tests;

/// <summary>Un dossier temporaire, supprimé à la fin du test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory().FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal static class Bytes
{
    /// <summary>Des listes imbriquées sur 100 000 niveaux : System.Text.Json s'arrête à 64.</summary>
    public static readonly string Deep = new string('[', 100_000) + new string(']', 100_000);

    public static byte[] WithBom(byte[] bytes) => Encoding.UTF8.Preamble.ToArray().Concat(bytes).ToArray();

    /// <summary>Des listes imbriquées sur <paramref name="levels"/> niveaux.</summary>
    public static string Nested(int levels) => new string('[', levels) + new string(']', levels);

    /// <summary>
    /// Un texte qui passe par ce qu'un écrivain JSON peut échapper (espace insécable, emoji, guillemet, saut de ligne).
    /// </summary>
    public const string Special = "Deux jours\U000000A0: « oui » \"x\"\nfin \U0001F600";

    /// <summary>Le message de JsonText pour une chaîne qui n'est pas du texte.</summary>
    public const string NotText = "chaîne qui n'est pas du texte : surrogate UTF-16 isolé (\\ud800 à \\udfff sans sa paire)";
}

public class SnapshotFileTests
{
    private const string Entry = """{"question_id": "q", "user_id": "a", "question": "?", "status": "answered", "cited_documents": ["d"], "text": "x", "attempts": 1}""";

    private static string Snapshot(string entry) => $$"""{"name": "a", "created_at": "t", "entries": [{{entry}}]}""";

    /// <summary>(contenu de a.json, ce qui ne va pas) : chaque champ est vérifié et nommé.</summary>
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
        { Snapshot(Entry.Replace("\"attempts\": 1", "\"attempts\": 2147483648")), "« attempts » doit être un entier" },   // un int : 32 bits
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
        // Sans « configuration » ni « attempts » (instantané d'une version plus ancienne) : accepté.
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
        // Sans puis avec marque d'ordre des octets : la position se compte après elle.
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
    /// JSON qui n'est pas strict : une clé en double (System.Text.Json en garderait une valeur sans rien dire), plus de
    /// 64 niveaux d'imbrication, une chaîne qui n'est pas du texte (GetString la refusait en anglais).
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

    [Fact]
    public void A_snapshot_reads_back_the_same()
    {
        // Écrit puis relu : les mêmes valeurs, texte compris (espace insécable, guillemets, saut de ligne, emoji), et la
        // configuration clés triées, découpage compris (le magasin trie les clés des dictionnaires qu'il écrit).
        // Le fichier garde les accents tels quels.
        using var dir = new TempDir();
        var splitter = new Dictionary<string, object> { ["type"] = "paragraph", ["max_chars"] = 800, ["overlap_chars"] = 120, ["include_title"] = true };
        var configuration = new Dictionary<string, object?>
        {
            ["corpus"] = "solveo", ["embedding_model"] = "hashing", ["splitter"] = splitter, ["top_k"] = 4, ["min_score"] = 0.4,
            ["temperature"] = 1.0, ["seed"] = null, ["index_id"] = "id",
        };
        var snapshot = new Snapshot("ref", "2026-09-11T12:00:00+00:00", configuration, new[]
        {
            new SnapshotEntry("q1", "alice", "Combien de congés ?", "answered", new[] { "a", "b" }, Bytes.Special, 1),
            new SnapshotEntry("q2", "bruno", "Et ?", "no_relevant_source", Array.Empty<string>(), "Rien.", 0),
        });
        var store = new JsonSnapshotStore(dir.Path);
        store.Save(snapshot);
        Assert.Contains("\"question\": \"Combien de congés ?\"", File.ReadAllText(dir.File("ref.json")));
        var read = store.Load("ref");
        Assert.Equal((snapshot.Name, snapshot.CreatedAt), (read.Name, read.CreatedAt));
        Assert.Equal(configuration.Keys.Order(StringComparer.Ordinal), read.Configuration.Keys);
        Assert.Equal(splitter.Keys.Order(StringComparer.Ordinal), Assert.IsType<Dictionary<string, object>>(read.Configuration["splitter"]).Keys);
        Assert.Empty(SnapshotComparer.Compare(snapshot, read).ConfigurationDifferences);   // 1.0, relu 1 : la même valeur
        Assert.Equivalent(snapshot.Entries, read.Entries, strict: true);
    }

    [Fact]
    public void An_index_file_missing_a_field_says_so()
    {
        // Le message d'une NullReferenceException ne dit rien d'utile : il est remplacé.
        using var dir = new TempDir();
        File.WriteAllText(dir.File("index.json"), "{}");
        var error = Assert.Throws<IndexUnreadableException>(() => new JsonVectorIndex(dir.File("index.json")).Manifest());
        Assert.Equal($"index illisible ({dir.File("index.json")}) : champ obligatoire manquant", error.Message);
    }
}

/// <summary>
/// L'index se lit strictement (JsonText) ; un index construit par la version Python se lit ici, et inversement. Écrit
/// ici par System.Text.Json, il se relit à l'identique.
/// </summary>
public class IndexFileTests
{
    private const string Index = """{"manifest": {"index_id": "id", "embedding_model": "m", "dimension": 2, "corpus_fingerprint": "fp","""
        + """ "splitter": {"x": 0}, "document_count": 1, "chunk_count": 1, "created_at": "t"}, "chunks": [{"id": "a#0","""
        + """ "document_id": "a", "document_title": "Congés", "text": "x", "position": 0, "allowed_groups": ["tous"]}], "vectors": [[0.6, 0.8]]}""";

    private static IndexManifest? Read(TempDir dir, byte[] content)
    {
        File.WriteAllBytes(dir.File("index.json"), content);
        return new JsonVectorIndex(dir.File("index.json")).Manifest();
    }

    [Fact]
    public void A_byte_order_mark_is_accepted_and_latin_1_is_refused()
    {
        // StreamReader remplaçait l'octet par « � » sans rien dire. La position se compte après la marque d'ordre des octets.
        using var dir = new TempDir();
        Assert.Equal("id", Read(dir, Bytes.WithBom(Encoding.UTF8.GetBytes(Index)))!.IndexId);
        foreach (var bytes in new[] { Encoding.Latin1.GetBytes(Index), Bytes.WithBom(Encoding.Latin1.GetBytes(Index)) })
        {
            var error = Assert.Throws<IndexUnreadableException>(() => Read(dir, bytes));
            Assert.Equal($"index illisible ({dir.File("index.json")}) : pas en UTF-8 (octet 0xe9 à la position {Index.IndexOf('é')})", error.Message);
        }
    }

    /// <summary>
    /// Clé en double, plus de 64 niveaux, chaîne qui n'est pas du texte (GetString la refusait en anglais).
    /// </summary>
    public static TheoryData<string, string> NotStrict => new()
    {
        { Index.Replace("\"index_id\": \"id\"", "\"index_id\": \"id\", \"index_id\": \"autre\""), "clé « index_id » en double" },
        { Index.Replace("{\"x\": 0}", "{\"x\": " + Bytes.Nested(62) + "}"), "JSON trop imbriqué : plus de 64 niveaux" },
        { Index.Replace("\"text\": \"x\"", "\"text\": \"x\\ud800\""), Bytes.NotText },
        { Index.Replace("{\"x\": 0}", "{\"\\ud800\": 0}"), Bytes.NotText },
    };

    [Theory]
    [MemberData(nameof(NotStrict))]
    public void An_index_that_is_not_strict_json_is_unreadable(string content, string problem)
    {
        using var dir = new TempDir();
        var error = Assert.Throws<IndexUnreadableException>(() => Read(dir, Encoding.UTF8.GetBytes(content)));
        Assert.Equal($"index illisible ({dir.File("index.json")}) : {problem}", error.Message);
    }

    [Fact]
    public void Sixty_four_levels_are_read()
    {
        using var dir = new TempDir();
        Assert.Equal("id", Read(dir, Encoding.UTF8.GetBytes(Index.Replace("{\"x\": 0}", "{\"x\": " + Bytes.Nested(61) + "}")))!.IndexId);
    }

    [Fact]
    public void An_integer_of_thousands_of_digits_is_read()
    {
        // Pas de limite de chiffres pour l'index (seules la configuration et GET /v1/models en ont une) : un entier de
        // 5 000 chiffres, dans le découpage, se lit.
        using var dir = new TempDir();
        var index = Index.Replace("{\"x\": 0}", "{\"x\": " + new string('1', 5000) + "}");
        Assert.Equal("id", Read(dir, Encoding.UTF8.GetBytes(index))!.IndexId);
    }

    [Fact]
    public void An_index_reads_back_the_same()
    {
        // Relu par un autre JsonVectorIndex (un autre processus) : le même manifeste, découpage clés triées (l'index trie
        // les clés qu'il écrit), et les mêmes résultats de recherche, scores compris. Le fichier garde les accents tels
        // quels.
        using var dir = new TempDir();
        var splitter = new Dictionary<string, object> { ["type"] = "paragraph", ["max_chars"] = 800, ["overlap_chars"] = 120, ["include_title"] = true };
        var manifest = new IndexManifest("id", "m", 2, "fp", splitter, 1, 2, "2026-09-11T00:00:00+00:00");
        var chunks = new[]
        {
            new Chunk("a#0", "a", "Congés", Bytes.Special, 0, new HashSet<string> { "tous", "rh" }),
            new Chunk("a#1", "a", "Congés", "suite", 1, new HashSet<string> { "tous" }),
        };
        var written = new JsonVectorIndex(dir.File("index.json"));
        written.Replace(manifest, chunks, new[] { new[] { 3.0, 4.0 }, new[] { 0.000012, 1.0 } });
        Assert.Contains("\"document_title\":\"Congés\"", File.ReadAllText(dir.File("index.json")));
        var read = new JsonVectorIndex(dir.File("index.json"));
        Assert.Equivalent(manifest, read.Manifest(), strict: true);
        Assert.Equal(splitter.Keys.Order(StringComparer.Ordinal), read.Manifest()!.Splitter.Keys);
        var query = new[] { 0.6, 0.8 };
        Assert.Equivalent(written.Search(query, 2, _ => true), read.Search(query, 2, _ => true), strict: true);
    }
}

public class PromptFileTests
{
    private const string Prompt = """{"version": "v1", "system": "Réponds en français.", "user": "{question}"}""";

    [Fact]
    public void A_byte_order_mark_changes_nothing()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("answer.json"), Encoding.UTF8.GetBytes(Prompt));
        var plain = new FilePromptRepository(dir.Path).Get("answer");
        File.WriteAllBytes(dir.File("answer.json"), Bytes.WithBom(Encoding.UTF8.GetBytes(Prompt)));
        Assert.Equal(plain, new FilePromptRepository(dir.Path).Get("answer"));
    }

    [Fact]
    public void A_prompt_saved_in_latin_1_names_the_file()
    {
        // Lu en silence, « Réponds » deviendrait « R�ponds », et la version du prompt changerait sans rien dire.
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("answer.json"), Encoding.Latin1.GetBytes(Prompt));
        var error = Assert.Throws<FormatException>(() => new FilePromptRepository(dir.Path).Get("answer"));
        Assert.Equal($"prompt illisible ({dir.File("answer.json")}) : pas en UTF-8 (octet 0xe9 à la position {Prompt.IndexOf('é')})",
                     error.Message);
    }

    [Theory]
    [InlineData("""{"version": "v1", "system": "a"}""")]
    [InlineData("""{"version": null, "system": "a", "user": "{question}"}""")]
    [InlineData("""null""")]
    public void A_missing_field_says_what_to_fix(string content)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("casse.json"), content);
        var error = Assert.Throws<FormatException>(() => new FilePromptRepository(dir.Path).Get("casse"));
        Assert.Equal($"prompt illisible ({dir.File("casse.json")}) : il faut trois textes, version, system et user", error.Message);
    }

    [Fact]
    public void A_deeply_nested_prompt_is_unreadable_not_a_crash()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("answer.json"), Prompt.Replace("}\"}", "}\", \"x\": " + Bytes.Deep + "}"));
        var error = Assert.Throws<FormatException>(() => new FilePromptRepository(dir.Path).Get("answer"));
        Assert.StartsWith($"prompt illisible ({dir.File("answer.json")}) : ", error.Message);
    }

    [Fact]
    public void A_duplicate_key_is_refused()
    {
        // System.Text.Json garderait l'une des deux valeurs sans rien dire.
        using var dir = new TempDir();
        File.WriteAllText(dir.File("answer.json"), Prompt.Replace("\"version\": \"v1\"", "\"version\": \"v1\", \"version\": \"v2\""));
        var error = Assert.Throws<FormatException>(() => new FilePromptRepository(dir.Path).Get("answer"));
        Assert.Equal($"prompt illisible ({dir.File("answer.json")}) : clé « version » en double", error.Message);
    }

    [Theory]
    [InlineData(@"Réponds.\u001c", "Réponds.\u001c", "v1+c6348c72")]   // Trim() garde \x1c (char.IsWhiteSpace dit non)
    [InlineData(@"Réponds.\u2028", "Réponds.", "v1+e8f04166")]
    public void A_prompt_is_trimmed_of_blanks_and_keeps_the_separator_x1c(string system, string kept, string version)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("answer.json"), $$"""{"version": "v1", "system": "{{system}}", "user": "{question}"}""");
        var prompt = new FilePromptRepository(dir.Path).Get("answer");
        Assert.Equal((kept, version), (prompt.System, prompt.Version));
    }
}

public class CorpusFileTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_document_saved_in_latin_1_is_refused_with_its_name(bool bom)
    {
        // Lu en silence, « Congés » deviendrait « Cong�s » dans l'index. La position se compte après la marque d'ordre
        // des octets.
        using var dir = new TempDir();
        const string content = "---\nid: conges\ngroupes: tous\n---\nCongés payés.";
        var bytes = Encoding.Latin1.GetBytes(content);
        File.WriteAllBytes(dir.File("conges.md"), bom ? Bytes.WithBom(bytes) : bytes);
        var error = Assert.Throws<CorpusFormatException>(() => new MarkdownCorpus(dir.Path).Load());
        Assert.Equal($"{dir.File("conges.md")} : pas en UTF-8 (octet 0xe9 à la position {content.IndexOf('é')})", error.Message);
    }

    [Fact]
    public void A_file_with_a_byte_order_mark_loads()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("a.md"), Bytes.WithBom(Encoding.UTF8.GetBytes("---\nid: a\ngroupes: tous\n---\nTexte.")));
        Assert.Equal("a", Assert.Single(new MarkdownCorpus(dir.Path).Load()).Id);
    }

    [Fact]
    public void Invalid_groups_and_duplicate_ids_are_listed_in_code_point_order()
    {
        // Listes écrites [a, b] et rangées par points de code.
        var error = Assert.Throws<CorpusFormatException>(() => MarkdownCorpus.Parse("---\nid: a\ngroupes: tous\u001c, rh #x\n---\nTexte."));
        Assert.Equal("<texte> : groupe(s) invalide(s) [rh #x, tous\u001c] : des noms en minuscules séparés par des virgules, sans commentaire",
                     error.Message);
        using var dir = new TempDir();
        var ids = new[] { "b", "B", "b", "B", "c" };
        for (var n = 0; n < ids.Length; n++)
        {
            File.WriteAllText(dir.File($"{n}.md"), $"---\nid: {ids[n]}\ngroupes: tous\n---\nTexte.");
        }
        error = Assert.Throws<CorpusFormatException>(() => new MarkdownCorpus(dir.Path).Load());
        Assert.Equal("Identifiants de documents en double : [B, b]", error.Message);
    }

    [Theory]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    [InlineData("\f")]
    [InlineData("\v")]
    [InlineData("\u0085")]
    [InlineData("\u001c")]
    [InlineData("\u001d")]
    [InlineData("\u001e")]
    public void Only_lf_and_crlf_end_a_line_the_rest_stays_in_the_text(string separator) =>
        // En Python, str.splitlines() en fait des sauts de ligne : deux textes, deux index_id.
        Assert.Equal($"Un.{separator}Deux.", MarkdownCorpus.Parse($"---\nid: a\ngroupes: tous\n---\nUn.{separator}Deux.").Text);

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void A_line_ends_with_lf_or_crlf(string newline)
    {
        var doc = MarkdownCorpus.Parse(string.Join(newline, "---", "id: a", "groupes: tous", "---", "Un.", "Deux."));
        Assert.Equal(("a", "Un.\nDeux."), (doc.Id, doc.Text));
    }
}

/// <summary>
/// Les blancs du corpus et du découpage, ceux de char.IsWhiteSpace (Trim(), \s) : les blancs Unicode (U+2028,
/// U+3000…) sont retirés aux bords d'une fiche et font une ligne blanche entre deux paragraphes ; les séparateurs
/// \x1c à \x1f restent dans le texte.
/// </summary>
public class BlankTests
{
    [Theory]
    [InlineData("\u001c")]
    [InlineData("\u001d")]
    [InlineData("\u001e")]
    [InlineData("\u001f")]
    public void Separators_x1c_to_x1f_at_the_edges_stay_in_the_text(string separator)
    {
        var doc = MarkdownCorpus.Parse($"---\nid: a{separator}\ngroupes: tous\n---\n{separator}Un.{separator}");
        Assert.Equal(($"a{separator}", $"{separator}Un.{separator}"), (doc.Id, doc.Text));
        // « ---\x1c » n'est pas la ligne d'en-tête.
        Assert.Throws<CorpusFormatException>(() => MarkdownCorpus.Parse($"---{separator}\nid: a\ngroupes: tous\n---\nUn."));
    }

    [Fact]
    public void Unicode_blanks_at_the_edges_are_trimmed()
    {
        var doc = MarkdownCorpus.Parse("---\nid: a\u2028\ngroupes: tous\u3000\n---\n\fUn.\u2029");
        Assert.Equal(("a", "Un.", "tous"), (doc.Id, doc.Text, Assert.Single(doc.AllowedGroups)));
    }

    [Fact]
    public void The_splitter_cuts_paragraphs_on_white_space_lines_and_keeps_the_separators()
    {
        static IEnumerable<string> Texts(string text) =>
            new ParagraphSplitter(100, 0, includeTitle: false).Split(Fakes.Doc("a", text)).Select(c => c.Text);

        // Deux paragraphes sont réunis dans le morceau, séparés par une ligne vide.
        Assert.Equal(new[] { "Un.\n\nDeux." }, Texts("Un.\n \u2028\nDeux."));
        Assert.Equal(new[] { "Un.\n\u001c\nDeux." }, Texts("Un.\n\u001c\nDeux."));   // pas une ligne blanche pour \s
        Assert.Equal(new[] { "\u001dUn.\u001d" }, Texts("\u001dUn.\u001d"));
        Assert.Equal(new[] { "\u001c" }, Texts("\u001c"));
        // Un paragraphe trop long, coupé à l'espace : \x1c reste de part et d'autre de la coupure.
        Assert.Equal(new[] { new string('x', 60) + "\u001c", "\u001c" + new string('y', 60) },
                     Texts(new string('x', 60) + "\u001c \u001c" + new string('y', 60)));
    }
}

public class AiServiceResponseTests
{
    private const string Missing = "Réponse du service IA incomplète, champs manquants : ";
    private const string Incoherent = "Réponse du service IA incohérente : ";
    private const string NotVectors = Incoherent + "« vectors » doit être une liste de listes de nombres";

    private static readonly GenerationRequest Request = new("s", "p", 0.2, 100);

    private static JsonTransport Answering(string json) => (_, _) =>
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    };

    /// <summary>(réponse JSON du service, erreur attendue).</summary>
    public static TheoryData<string, string> MalformedEmbeddings => new()
    {
        { "null", Missing + "[model, dimension, vectors]" },
        { "5", Missing + "[model, dimension, vectors]" },
        { "\"model dimension vectors\"", Missing + "[model, dimension, vectors]" },
        { "[]", Missing + "[model, dimension, vectors]" },
        { """{"vectors": [[1.0]]}""", Missing + "[model, dimension]" },
        { """{"model": 5, "dimension": 2, "vectors": [[0.1, 0.2]]}""", Incoherent + "« model » doit être un texte" },
        { """{"model": null, "dimension": 2, "vectors": [[0.1, 0.2]]}""", Incoherent + "« model » doit être un texte" },
        // Demi-paire de substitution seule : pas un texte Unicode (GetString lève InvalidOperationException).
        { """{"model": "\ud800", "dimension": 2, "vectors": [[0.1, 0.2]]}""", Incoherent + "« model » doit être un texte" },
        { """{"model": "m", "dimension": "abc", "vectors": [[0.1, 0.2]]}""", Incoherent + "« dimension » doit être un entier" },
        { """{"model": "m", "dimension": true, "vectors": [[0.1, 0.2]]}""", Incoherent + "« dimension » doit être un entier" },
        { """{"model": "m", "dimension": 2.0, "vectors": [[0.1, 0.2]]}""", Incoherent + "« dimension » doit être un entier" },
        // Un int : de -2**31 à 2**31 - 1.
        { """{"model": "m", "dimension": 2147483648, "vectors": [[0.1, 0.2]]}""", Incoherent + "« dimension » doit être un entier" },
        { """{"model": "m", "dimension": -2147483649, "vectors": [[0.1, 0.2]]}""", Incoherent + "« dimension » doit être un entier" },
        { """{"model": "m", "dimension": 2147483647, "vectors": [[0.1, 0.2]]}""",
          Incoherent + "1 vecteurs de 2147483647 dimensions attendus, reçu 1 de [2] dimensions" },
        { """{"model": "m", "dimension": 2, "vectors": [1]}""", NotVectors },
        { """{"model": "m", "dimension": 2, "vectors": "ab"}""", NotVectors },
        { """{"model": "m", "dimension": 2, "vectors": [[0.1, "x"]]}""", NotVectors },
        { """{"model": "m", "dimension": 2, "vectors": [[0.1, true]]}""", NotVectors },
        { """{"model": "m", "dimension": 2, "vectors": [[0.1, null]]}""", NotVectors },
        { """{"model": "m", "dimension": 2, "vectors": [[0.1, 1e400]]}""", NotVectors },
        // Un entier de 401 chiffres : trop grand pour un réel.
        { """{"model": "m", "dimension": 2, "vectors": [[0.1, 1""" + new string('0', 400) + "]]}", NotVectors },
        { """{"model": "m", "dimension": 2, "vectors": [[0.1]]}""", Incoherent + "1 vecteurs de 2 dimensions attendus, reçu 1 de [1] dimensions" },
        { """{"model": "m", "dimension": 2, "vectors": [[0.1, 0.2], [0.3, 0.4]]}""", Incoherent + "1 vecteurs de 2 dimensions attendus, reçu 2 de [2] dimensions" },
    };

    public static TheoryData<string, string> MalformedGenerations => new()
    {
        { "null", Missing + "[model, text]" },
        { """{"text": "Deux jours [1]."}""", Missing + "[model]" },
        { """{"model": "m", "text": null}""", Incoherent + "« text » doit être un texte" },
        { """{"model": "m", "text": 5}""", Incoherent + "« text » doit être un texte" },
        { """{"model": "m", "text": "\udc00 ok"}""", Incoherent + "« text » doit être un texte" },
        { """{"model": ["m"], "text": "Deux jours [1]."}""", Incoherent + "« model » doit être un texte" },
    };

    private const string Problem = """{"error": {"code": "backend_error", "message": "modele absent", "retryable": false}}""";

    /// <summary>
    /// (corps d'une réponse 502, précédé d'une marque d'ordre des octets ?, détail attendu dans le message, passagère ?) :
    /// seule une erreur au format du contrat, avec un message textuel, est lue ; tout autre corps est cité tel quel, et un
    /// 5xx reste alors passager.
    /// </summary>
    public static TheoryData<string, bool, string, bool> ErrorBodies => new()
    {
        { Problem, false, "modele absent", false },
        { Problem, true, "modele absent", false },
        { """{"error": {"message": "surcharge"}}""", false, "surcharge", true },
        { """{"error": {"message": null, "retryable": false}}""", false, """{"error": {"message": null, "retryable": false}}""", true },
        { """{"error": {"message": 5, "retryable": false}}""", false, """{"error": {"message": 5, "retryable": false}}""", true },
        { """{"error": {"message": "\ud800", "retryable": false}}""", false, """{"error": {"message": "\ud800", "retryable": false}}""", true },
        { """{"error": {"message": "x", "retryable": NaN}}""", false, """{"error": {"message": "x", "retryable": NaN}}""", true },
        { """{"error": "x"}""", false, """{"error": "x"}""", true },
        { "<html>proxy</html>", false, "<html>proxy</html>", true },
        { Bytes.Deep, false, Bytes.Deep, true },
        // JSON qui n'est pas strict (clé en double, 65 niveaux) : pas une erreur au format du contrat.
        { """{"error": {"message": "a", "message": "b"}}""", false, """{"error": {"message": "a", "message": "b"}}""", true },
        { """{"error": {"message": "x"}, "x": """ + Bytes.Nested(64) + "}", false, """{"error": {"message": "x"}, "x": """ + Bytes.Nested(64) + "}", true },
    };

    [Theory]
    [MemberData(nameof(MalformedEmbeddings))]
    public void An_embeddings_response_of_another_shape_is_an_error_not_a_crash(string json, string message)
    {
        var error = Assert.Throws<AiServiceException>(() => new HttpEmbedder("http://ia", "m", transport: Answering(json)).EmbedQuery("x"));
        Assert.Equal(message, error.Message);
        Assert.False(error.Transient);   // réessayer ne changerait rien
    }

    [Theory]
    [MemberData(nameof(MalformedGenerations))]
    public void A_generation_response_of_another_shape_is_an_error_not_a_crash(string json, string message)
    {
        var error = Assert.Throws<AiServiceException>(() => new HttpGenerator("http://ia", "m", transport: Answering(json)).Generate(Request));
        Assert.Equal(message, error.Message);
        Assert.False(error.Transient);
    }

    [Fact]
    public void A_character_outside_the_basic_plane_is_text()
    {
        // « \ud83d\ude00 » est une paire complète (un emoji) : un texte.
        var generation = new HttpGenerator("http://ia", "m", transport: Answering("""{"model": "m", "text": "Bravo \ud83d\ude00"}""")).Generate(Request);
        Assert.Equal("Bravo " + char.ConvertFromUtf32(0x1F600), generation.Text);
    }

    [Theory]
    [MemberData(nameof(ErrorBodies))]
    public void An_error_body_of_another_shape_is_quoted_as_is(string body, bool bom, string detail, bool transient)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        using var service = new FixedAnswerService(bom ? Bytes.WithBom(bytes) : bytes, HttpStatusCode.BadGateway);
        var error = Assert.Throws<AiServiceException>(() => new HttpEmbedder(service.Url, "m", TimeSpan.FromSeconds(5)).EmbedQuery("x"));
        Assert.Equal($"Service IA : HTTP 502 — {detail}", error.Message);
        Assert.Equal(transient, error.Transient);
    }

    /// <summary>Ni JSON pour System.Text.Json : page d'un proxy, NaN, -Infinity, listes imbriquées sur 100 000 niveaux.</summary>
    public static TheoryData<string> NotJson => new()
    {
        "<html>proxy</html>", """{"model": NaN}""", """{"model": "m", "dimension": 1, "vectors": [[-Infinity]]}""", Bytes.Deep,
    };

    [Theory]
    [MemberData(nameof(NotJson))]
    public void A_response_that_is_not_json_is_unreadable_and_not_transient(string body)
    {
        using var service = new FixedAnswerService(Encoding.UTF8.GetBytes(body));
        var error = Assert.Throws<AiServiceException>(() => new HttpEmbedder(service.Url, "m", TimeSpan.FromSeconds(5)).EmbedQuery("x"));
        Assert.StartsWith($"Réponse du service IA illisible ({service.Url}v1/embeddings) : ", error.Message);
        Assert.False(error.Transient);
    }

    /// <summary>Clé en double (la dernière gagnait sans rien dire), plus de 64 niveaux : refusés avec un message en français.</summary>
    public static TheoryData<string, string> NotStrict => new()
    {
        { """{"model": "m", "model": "autre", "dimension": 1, "vectors": [[1.0]]}""", "clé « model » en double" },
        { """{"model": "m", "dimension": 1, "vectors": [[1.0]], "x": """ + Bytes.Nested(64) + "}", "JSON trop imbriqué : plus de 64 niveaux" },
    };

    [Theory]
    [MemberData(nameof(NotStrict))]
    public void A_response_that_is_not_strict_json_is_unreadable(string body, string problem)
    {
        using var service = new FixedAnswerService(Encoding.UTF8.GetBytes(body));
        var error = Assert.Throws<AiServiceException>(() => new HttpEmbedder(service.Url, "m", TimeSpan.FromSeconds(5)).EmbedQuery("x"));
        Assert.Equal($"Réponse du service IA illisible ({service.Url}v1/embeddings) : {problem}", error.Message);
        Assert.False(error.Transient);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_response_that_is_not_utf_8_says_where(bool bom)
    {
        // ReadAsStringAsync aurait remplacé l'octet par « � » sans rien dire, comme File.ReadAllText. La position se
        // compte après la marque d'ordre des octets.
        var body = Encoding.Latin1.GetBytes("""{"model": "Congés"}""");
        using var service = new FixedAnswerService(bom ? Bytes.WithBom(body) : body);
        var error = Assert.Throws<AiServiceException>(() => new HttpEmbedder(service.Url, "m", TimeSpan.FromSeconds(5)).EmbedQuery("x"));
        Assert.Equal($"Réponse du service IA illisible ({service.Url}v1/embeddings) : pas en UTF-8 (octet 0xe9 à la position {Array.IndexOf(body, (byte)0xE9)})",
                     error.Message);
        Assert.False(error.Transient);
    }

    [Fact]
    public void A_response_with_a_byte_order_mark_is_read()
    {
        var body = Bytes.WithBom(Encoding.UTF8.GetBytes("""{"model": "m", "dimension": 1, "vectors": [[1.0]]}"""));
        using var service = new FixedAnswerService(body);
        Assert.Equal("m", new HttpEmbedder(service.Url, "m", TimeSpan.FromSeconds(5)).EmbedQuery("x").Model);
    }

    /// <summary>
    /// Le corps des requêtes : accents tels quels, en UTF-8 ; relu, chaque texte revient tel quel (guillemets, antislash,
    /// caractères de contrôle, emoji) ; seed vaut null s'il n'est pas fixé. Les autres champs : HttpContractTests.
    /// </summary>
    [Fact]
    public void A_request_body_reads_back_the_same_with_accents_as_they_are()
    {
        var texts = new[] { "Congés « payés »\u00A0\U0001F600", "a\"b\\c/\n\t\u007F\u0001\u2028", "~ ascii" };
        using (var service = new FixedAnswerService(Encoding.UTF8.GetBytes("""{"model": "m", "dimension": 1, "vectors": [[1.0], [1.0], [1.0]]}""")))
        {
            new HttpEmbedder(service.Url, "m", TimeSpan.FromSeconds(5)).EmbedDocuments(texts);
            var body = Assert.Single(service.Requests);
            Assert.Contains("Congés « payés »", Encoding.UTF8.GetString(body));
            using var sent = JsonDocument.Parse(body);
            Assert.Equal(texts, sent.RootElement.GetProperty("inputs").EnumerateArray().Select(t => t.GetString()));
        }
        using (var service = new FixedAnswerService(Encoding.UTF8.GetBytes("""{"model": "m", "text": "ok"}""")))
        {
            new HttpGenerator(service.Url, "m", TimeSpan.FromSeconds(5)).Generate(new GenerationRequest("Réponds.", "Q ?", 1.0, 400));
            using var sent = JsonDocument.Parse(Assert.Single(service.Requests));
            Assert.Equal(JsonValueKind.Null, sent.RootElement.GetProperty("seed").ValueKind);
        }
    }

    /// <summary>
    /// Un passage, une question et une réponse bordés d'un blanc, que Trim() retire, puis de U+001C, qu'il garde
    /// (char.IsWhiteSpace dit non) : ce qu'envoie le cas d'usage.
    /// </summary>
    [Fact]
    public void Separators_at_the_edges_reach_the_ai_service_and_stay_in_the_answer()
    {
        var index = new FakeIndex();
        index.Replace(new IndexManifest("id", "m", 1, "fp", new Dictionary<string, object>(), 1, 1, "t"),
            new[] { new Chunk("a#0", "a", "A", "\u2028\u001cDeux jours par semaine.\u001c ", 0, new HashSet<string> { "tous" }) },
            new[] { new[] { 1.0 } });
        using var embedder = new FixedAnswerService(Encoding.UTF8.GetBytes("""{"model": "m", "dimension": 1, "vectors": [[1.0]]}"""));
        using var generator = new FixedAnswerService(Encoding.UTF8.GetBytes("""{"model": "m", "text": " \u001cDeux jours [1].\u001c\n"}"""));
        var ask = new AskQuestion(new HttpEmbedder(embedder.Url, "m", TimeSpan.FromSeconds(5)), index,
            new HttpGenerator(generator.Url, "m", TimeSpan.FromSeconds(5)), new StaticPrompts(), new AskSettings(MinScore: 0.5));
        var answer = ask.Execute(Fakes.Alice, "\u3000\u001cCombien de jours ?\u001c\t");
        using var query = JsonDocument.Parse(Assert.Single(embedder.Requests));
        Assert.Equal(new[] { "\u001cCombien de jours ?\u001c" }, query.RootElement.GetProperty("inputs").EnumerateArray().Select(t => t.GetString()));
        using var generation = JsonDocument.Parse(Assert.Single(generator.Requests));
        Assert.Equal("Passages :\n[1] A\n\u001cDeux jours par semaine.\u001c\n\nQuestion : \u001cCombien de jours ?\u001c",
                     generation.RootElement.GetProperty("prompt").GetString());
        Assert.Equal(("\u001cCombien de jours ?\u001c", AnswerStatus.Answered, "\u001cDeux jours [1].\u001c"),
                     (answer.Question, answer.Status, answer.Text));
    }

    [Theory]
    [InlineData("vectors", NotVectors)]
    [InlineData("dimension", Incoherent + "« dimension » doit être un entier")]
    public void An_integer_of_thousands_of_digits_is_a_number_out_of_range(string field, string message)
    {
        // Pour System.Text.Json, un nombre comme un autre : trop grand pour un réel fini, ou pour un int.
        var huge = "1" + new string('0', 5000);
        var body = field == "vectors"
            ? $$"""{"model": "m", "dimension": 2, "vectors": [[0.1, {{huge}}]]}"""
            : $$"""{"model": "m", "dimension": {{huge}}, "vectors": [[0.1, 0.2]]}""";
        using var service = new FixedAnswerService(Encoding.UTF8.GetBytes(body));
        var error = Assert.Throws<AiServiceException>(() => new HttpEmbedder(service.Url, "m", TimeSpan.FromSeconds(5)).EmbedQuery("x"));
        Assert.Equal(message, error.Message);
        Assert.False(error.Transient);
    }

    /// <summary>
    /// Un faux service IA qui répond toujours les mêmes octets, avec le même statut, sur un port libre, et garde les corps
    /// des requêtes reçues.
    /// </summary>
    private sealed class FixedAnswerService : IDisposable
    {
        private readonly HttpListener _listener;

        public string Url { get; }

        /// <summary>Les corps reçus, dans l'ordre ; chacun est gardé avant que la réponse ne parte.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<byte[]> Requests { get; } = new();

        public FixedAnswerService(byte[] body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _listener = TestPorts.Listener();
            Url = _listener.Prefixes.Single();   // avec sa barre finale
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
                    {
                        return;
                    }
                    using (var request = new MemoryStream())
                    {
                        await context.Request.InputStream.CopyToAsync(request);
                        Requests.Enqueue(request.ToArray());
                    }
                    context.Response.StatusCode = (int)status;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
            });
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }
    }
}
