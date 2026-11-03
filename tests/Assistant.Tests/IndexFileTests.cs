// Le fichier d'index, frontière avec le disque : un mauvais encodage, du JSON qui n'est pas strict, un champ
// manquant ou un autre format donnent un message qui nomme le fichier et la commande qui le reconstruit.

using System.Text;
using Assistant.Application;
using Assistant.Domain;
using Assistant.Infrastructure;
using Xunit;

namespace Assistant.Tests;

/// <summary>
/// L'index est lu strictement, par JsonText. Écrit par System.Text.Json, il doit se relire à l'identique.
/// </summary>
public class IndexFileTests
{
    // La clé « x », que la lecture ignore, reçoit les valeurs de plusieurs cas ci-dessous.
    private const string Index = """{"manifest": {"embedding_model": "m", "dimension": 2, "document_count": 1, "chunk_count": 1}, "chunks": [{"id": "a#0","""
        + """ "document_id": "a", "document_title": "Congés", "text": "x", "position": 0, "allowed_groups": ["tous"]}], "vectors": [[0.6, 0.8]], "x": 0}""";

    private static IndexManifest? Read(TempDir dir, byte[] content)
    {
        File.WriteAllBytes(dir.File("index.json"), content);
        return new JsonVectorIndex(dir.File("index.json")).Manifest();
    }

    private static string Unreadable(TempDir dir, string problem) =>
        $"index illisible ({dir.File("index.json")}) : {problem}, réindexez avec dotnet run --project src/Assistant.Cli -- index";

    [Fact]
    public void A_byte_order_mark_is_accepted_and_latin_1_is_refused()
    {
        // Un StreamReader remplacerait l'octet par « � » sans rien dire. La position se compte après la marque
        // d'ordre des octets.
        using var dir = new TempDir();
        Assert.Equal("m", Read(dir, Bytes.WithBom(Encoding.UTF8.GetBytes(Index)))!.EmbeddingModel);
        foreach (var bytes in new[] { Encoding.Latin1.GetBytes(Index), Bytes.WithBom(Encoding.Latin1.GetBytes(Index)) })
        {
            var error = Assert.Throws<IndexUnreadableException>(() => Read(dir, bytes));
            Assert.Equal(Unreadable(dir, $"pas en UTF-8 (octet 0xe9 à la position {Index.IndexOf('é')})"), error.Message);
        }
    }

    /// <summary>Les mêmes cas de JSON non strict que pour les instantanés.</summary>
    public static TheoryData<string, string> NotStrict => new()
    {
        { Index.Replace("\"embedding_model\": \"m\"", "\"embedding_model\": \"m\", \"embedding_model\": \"autre\""), "clé « embedding_model » en double" },
        { Index.Replace("\"x\": 0", "\"x\": " + Bytes.Nested(64)), "JSON trop imbriqué : plus de 64 niveaux" },
        { Index.Replace("\"text\": \"x\"", "\"text\": \"x\\ud800\""), Bytes.NotText },
        { Index.Replace("\"x\": 0", "\"\\ud800\": 0"), Bytes.NotText },
    };

    [Theory]
    [MemberData(nameof(NotStrict))]
    public void An_index_that_is_not_strict_json_is_unreadable(string content, string problem)
    {
        using var dir = new TempDir();
        var error = Assert.Throws<IndexUnreadableException>(() => Read(dir, Encoding.UTF8.GetBytes(content)));
        Assert.Equal(Unreadable(dir, problem), error.Message);
    }

    [Fact]
    public void Sixty_four_levels_are_read()
    {
        using var dir = new TempDir();
        Assert.Equal("m", Read(dir, Encoding.UTF8.GetBytes(Index.Replace("\"x\": 0", "\"x\": " + Bytes.Nested(63))))!.EmbeddingModel);
    }

    [Fact]
    public void An_integer_of_thousands_of_digits_is_read()
    {
        // L'index n'a pas de limite de chiffres, seules la configuration et GET /v1/models en ont une. Un entier de
        // 5 000 chiffres placé dans le fichier se lit donc sans erreur.
        using var dir = new TempDir();
        var index = Index.Replace("\"x\": 0", "\"x\": " + new string('1', 5000));
        Assert.Equal("m", Read(dir, Encoding.UTF8.GetBytes(index))!.EmbeddingModel);
    }

    private static readonly IndexManifest Manifest = new("m", 2, 1, 2);

    private static readonly Chunk[] Chunks =
    {
        new("a#0", "a", "Congés", Bytes.Special, 0, new HashSet<string> { "tous", "rh" }),
        new("a#1", "a", "Congés", "suite", 1, new HashSet<string> { "tous" }),
    };

    private static readonly double[][] Vectors = { new[] { 3.0, 4.0 }, new[] { 0.000012, 1.0 } };

    [Fact]
    public void An_index_reads_back_the_same()
    {
        // Un second JsonVectorIndex relit le fichier, comme le ferait un autre processus. Il doit trouver le même
        // manifeste et les mêmes résultats de recherche, scores compris.
        using var dir = new TempDir();
        var written = new JsonVectorIndex(dir.File("index.json"));
        written.Replace(Manifest, Chunks, Vectors);
        var read = new JsonVectorIndex(dir.File("index.json"));
        Assert.Equivalent(Manifest, read.Manifest(), strict: true);
        var query = new[] { 0.6, 0.8 };
        Assert.Equivalent(written.Search(query, 2, _ => true), read.Search(query, 2, _ => true), strict: true);
    }

    [Fact]
    public void An_index_file_keeps_accents_as_they_are()
    {
        using var dir = new TempDir();
        new JsonVectorIndex(dir.File("index.json")).Replace(Manifest, Chunks, Vectors);
        Assert.Contains("\"document_title\":\"Congés\"", File.ReadAllText(dir.File("index.json")));
    }

    [Fact]
    public void An_index_file_missing_a_field_says_so()
    {
        // Le message d'une NullReferenceException ne dit rien d'utile : il est remplacé.
        using var dir = new TempDir();
        File.WriteAllText(dir.File("index.json"), Index.Replace("\"position\": 0, ", ""));
        var error = Assert.Throws<IndexUnreadableException>(() => new JsonVectorIndex(dir.File("index.json")).Manifest());
        Assert.Equal(Unreadable(dir, "champ obligatoire manquant"), error.Message);
    }

    /// <summary>
    /// Un objet vide, un index sans manifeste et un index au manifeste plus riche : aucun n'a le format attendu, et
    /// le message donne la commande qui reconstruit l'index.
    /// </summary>
    public static TheoryData<string> OtherFormats => new()
    {
        "{}",
        """{"chunks": [], "vectors": []}""",
        Index.Replace("\"chunk_count\": 1}", "\"chunk_count\": 1, \"splitter\": {\"type\": \"paragraph\"}, \"created_at\": \"t\"}"),
    };

    [Theory]
    [MemberData(nameof(OtherFormats))]
    public void An_index_of_another_format_says_to_reindex(string content)
    {
        using var dir = new TempDir();
        var error = Assert.Throws<IndexUnreadableException>(() => Read(dir, Encoding.UTF8.GetBytes(content)));
        Assert.Equal(Unreadable(dir, "écrit dans un autre format"), error.Message);
    }
}
