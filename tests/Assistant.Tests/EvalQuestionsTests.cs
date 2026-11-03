// Les jeux de questions d'évaluation, lus strictement : un fichier mal formé donne un message qui nomme le
// fichier et le champ en faute.

using System.Text;
using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

public class EvalQuestionsTests
{
    [Fact]
    public void Evaluation_questions_load_with_their_expectations()
    {
        var questions = EvalQuestions.Load(Path.Combine(AppConfig.ProjectRoot, "eval", "questions.json"));
        Assert.True(questions.Count >= 20);
        Assert.Contains(questions, q => !q.Answerable);
        Assert.Contains(questions, q => q.ForbiddenDocuments.Count > 0);
        Assert.All(questions.Where(q => q.Answerable), q => Assert.NotEmpty(q.ExpectedDocuments));
        foreach (var path in Directory.GetFiles(Path.Combine(AppConfig.ProjectRoot, "eval"), "questions*.json"))
        {
            Assert.NotEmpty(EvalQuestions.Load(path));   // tous les jeux livrés passent la lecture stricte
        }
    }

    private const string KnownKeys = "answerable, expected_documents, expected_keywords, forbidden_documents, id, question, user";
    private const string NotText = "chaîne qui n'est pas du texte : surrogate UTF-16 isolé (\\ud800 à \\udfff sans sa paire)";

    /// <summary>Jeux mal formés et ce que dit le message.</summary>
    public static readonly TheoryData<string, string> Malformed = new()
    {
        { "[1, 2]", "un objet JSON avec une liste « questions » est attendu" },
        { """{"questions": {}}""", "un objet JSON avec une liste « questions » est attendu" },
        { """{"questions": [1]}""", "question n° 1 : un objet JSON est attendu" },
        { """{"questions": [{"id": 1, "question": "?"}]}""", "question n° 1 : champ « id » manquant ou non textuel" },
        { """{"questions": [{"id": "a", "question": "?"}, {"id": "b"}]}""", "question n° 2 : champ « question » manquant ou non textuel" },
        { """{"questions": [{"id": "a", "question": 12}]}""", "question n° 1 : champ « question » manquant ou non textuel" },
        { """{"questions": [{"id": "a", "question": "?", "user": 3}]}""", "question n° 1 : « user » doit être un texte" },
        { """{"questions": [{"id": "a", "question": "?", "answerable": "false"}]}""", "question n° 1 : « answerable » doit valoir true ou false" },
        { """{"questions": [{"id": "a", "question": "?", "expected_documents": "teletravail"}]}""",
          "question n° 1 : « expected_documents » doit être une liste de textes" },
        { """{"questions": [{"id": "a", "question": "?", "expected_keywords": [1]}]}""", "question n° 1 : « expected_keywords » doit être une liste de textes" },
        { """{"questions": [{"id": "a", "question": "?", "expected_document": ["x"]}]}""",
          $"question n° 1 : clé(s) inconnue(s) [expected_document] (connues : {KnownKeys})" },
        { """{"questions": [{"id": "a", "id": "b", "question": "?"}]}""", "clé « id » en double" },
        { """{"questions": [{"id": {"x": 1, "x": 2}, "id": "a", "question": "?"}]}""", "clé « x » en double" },   // l'objet intérieur d'abord
        { """{"questions": [{"id": "a", "question": "?"}, {"id": "a", "question": "?"}]}""", "question n° 2 : identifiant « a » déjà utilisé" },
        { "{\"x\": " + Bytes.Nested(64) + ", \"questions\": []}", "JSON trop imbriqué : plus de 64 niveaux" },
        { Bytes.Deep, "JSON trop imbriqué : plus de 64 niveaux" },
        // Un « \ud800 » isolé est du JSON, mais pas du texte : GetString lèverait une InvalidOperationException, et
        // l'utilisateur verrait une trace. Il est refusé partout, y compris dans une clé de commentaire. Le message
        // nomme le premier défaut rencontré, même si une imbrication trop profonde ou un NaN suivent. Un nombre très
        // long, lui, n'est pas un défaut dans un jeu de questions.
        { """{"questions": [{"id": "a", "question": "\ud800"}]}""", NotText },
        { """{"questions": [{"id": "a", "question": "?", "user": "\udc00"}]}""", NotText },
        { """{"questions": [{"id": "\ud800", "question": "?"}]}""", NotText },
        { """{"questions": [{"id": "a", "question": "?", "expected_documents": ["\ud83d"]}]}""", NotText },
        { """{"questions": [{"id": "a", "question": "?", "\ud800": 1}]}""", NotText },
        { """{"questions": [{"id": "a", "question": "?", "\ud800": 1, "\ud800": 2}]}""", NotText },   // en double, mais pas du texte
        { """{"questions": [{"id": "a", "question": "?", "_note": "\ud800"}]}""", NotText },
        { """{"questions": [{"id": "\ud800", "question": "?", "_x": """ + Bytes.Nested(70) + "}]}", NotText },
        { """{"questions": [{"_x": """ + Bytes.Nested(70) + """, "id": "\ud800", "question": "?"}]}""", "JSON trop imbriqué : plus de 64 niveaux" },
        { """{"questions": [{"id": "\ud800", "question": "?", "_x": NaN}]}""", NotText },
        { """{"questions": [{"id": "\ud800", "question": "?", "_n": """ + new string('1', 4301) + "}]}", NotText },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Malformed_question_sets_name_the_file_and_the_field(string content, string detail)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("fyc-questions-").FullName, "questions.json");
        try
        {
            File.WriteAllText(path, content);
            var error = Assert.Throws<FormatException>(() => EvalQuestions.Load(path));
            Assert.Equal($"{path} : jeu de questions mal formé ({detail})", error.Message);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Theory]
    [InlineData("{pas json", "jeu de questions mal formé (JSON invalide : ")]
    [InlineData("""{"questions": [{"id": "a", "question": "?"},]}""", "jeu de questions mal formé (JSON invalide : ")]   // virgule finale
    [InlineData("""{"x": NaN, "questions": [{"id": "a", "question": "?"}]}""", "jeu de questions mal formé (JSON invalide : ")]   // NaN
    [InlineData("""{"questions": []}""", "aucune question dans ")]
    public void Unreadable_or_empty_question_sets_name_the_file(string content, string start)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("fyc-questions-").FullName, "questions.json");
        try
        {
            File.WriteAllText(path, content);
            var error = Assert.Throws<FormatException>(() => EvalQuestions.Load(path));
            Assert.Contains(path, error.Message);
            Assert.Contains(start, error.Message);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    private const string AccentedSet = """{"questions": [{"id": "a", "question": "Congés ?"}]}""";

    [Fact]
    public void A_question_set_that_is_not_utf8_names_the_byte_and_its_position()
    {
        // File.ReadAllText aurait lu « Cong�s » sans rien dire. Quand le fichier a une marque d'ordre des octets, la
        // position se compte après elle.
        using var dir = new TempDir();
        foreach (var bytes in new[] { Encoding.Latin1.GetBytes(AccentedSet), Bytes.WithBom(Encoding.Latin1.GetBytes(AccentedSet)) })
        {
            File.WriteAllBytes(dir.File("q.json"), bytes);
            Assert.Equal($"{dir.File("q.json")} : jeu de questions mal formé (pas en UTF-8 (octet 0xe9 à la position {AccentedSet.IndexOf('é')}))",
                         Assert.Throws<FormatException>(() => EvalQuestions.Load(dir.File("q.json"))).Message);
        }
    }

    [Fact]
    public void A_question_set_with_a_byte_order_mark_is_read()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("q.json"), Bytes.WithBom(Encoding.UTF8.GetBytes(AccentedSet)));
        Assert.Equal("Congés ?", Assert.Single(EvalQuestions.Load(dir.File("q.json"))).Question);
    }

    [Fact]
    public void A_key_that_starts_with_an_underscore_is_a_comment()
    {
        // Comme dans la configuration JSON, une clé qui commence par « _ » est lue sans effet. Les autres clés
        // inconnues restent refusées.
        using var dir = new TempDir();
        File.WriteAllText(dir.File("q.json"), """{"questions": [{"id": "a", "_note": "à revoir", "question": "?", "_x": [1]}]}""");
        Assert.Equal("a", Assert.Single(EvalQuestions.Load(dir.File("q.json"))).Id);
        File.WriteAllText(dir.File("q.json"), """{"questions": [{"id": "a", "note": "à revoir", "question": "?"}]}""");
        Assert.Contains("clé(s) inconnue(s) [note]", Assert.Throws<FormatException>(() => EvalQuestions.Load(dir.File("q.json"))).Message);
    }

    [Fact]
    public void A_complete_surrogate_pair_and_an_integer_of_4300_digits_are_read()
    {
        // Un emoji écrit en paire (« \ud83d\ude00 ») est du texte, contrairement aux demi-paires de Malformed. Un
        // entier, même de milliers de chiffres, se lit aussi.
        using var dir = new TempDir();
        File.WriteAllText(dir.File("q.json"), """{"questions": [{"id": "a", "question": "\ud83d\ude00 ?", "_n": -""" + new string('9', 4300)
                                              + ", \"_m\": " + new string('1', 5000) + "}]}");
        Assert.Equal("\U0001F600 ?", Assert.Single(EvalQuestions.Load(dir.File("q.json"))).Question);
    }

    [Fact]
    public void Limit_keeps_the_first_questions_and_refuses_zero_or_less()
    {
        var questions = Enumerable.Range(0, 3)
            .Select(i => new EvalQuestion($"{i}", "alice", "?", Array.Empty<string>(), Array.Empty<string>(), true, Array.Empty<string>())).ToList();
        Assert.Equal(questions, EvalQuestions.Limit(questions, null));
        Assert.Equal(new[] { "0", "1" }, EvalQuestions.Limit(questions, 2).Select(q => q.Id));
        foreach (var limit in new[] { 0, -1 })
        {
            Assert.Equal($"--limit doit valoir au moins 1, pas {limit}", Assert.Throws<ArgumentException>(() => EvalQuestions.Limit(questions, limit)).Message);
        }
    }
}
