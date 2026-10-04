// JSON lu strictement (JsonText) : une clé en double, plus de 64 niveaux, une chaîne qui n'est pas du texte et un entier
// trop long ont un message en français ; une erreur de syntaxe, NaN compris, garde celui de System.Text.Json.

using System.Text.Json;
using Xunit;

namespace Assistant.Tests;

public class JsonTextTests
{
    [Theory]
    [InlineData("""{"a": 1, "a": {"x": 1, "x": 2}}""", "clé « a » en double")]   // dite dès que la clé revient
    [InlineData("""{"a": 1, "b": 1, "b": 2, "a": 2}""", "clé « b » en double")]   // le premier doublon de l'objet
    [InlineData("""[{"a": 1}, {"a": 1, "a": 1}]""", "clé « a » en double")]
    public void A_duplicate_key_is_refused_as_soon_as_it_comes_back(string json, string message)
    {
        Assert.Equal(message, Assert.Throws<FormatException>(() => JsonText.Parse(json)).Message);
        Assert.Equal(message, Assert.Throws<FormatException>(() => JsonText.ParseDocument(json)).Message);
    }

    [Theory]
    [InlineData(64, false, true)]
    [InlineData(65, false, false)]
    [InlineData(64, true, true)]
    [InlineData(65, true, false)]
    [InlineData(1000, false, false)]
    [InlineData(1000, true, false)]
    [InlineData(1001, false, false)]
    [InlineData(100_000, false, false)]
    public void Sixty_four_levels_are_read_not_sixty_five(int depth, bool objects, bool read)
    {
        // Avec strings aussi.
        var json = objects
            ? string.Concat(Enumerable.Repeat("{\"a\": ", depth - 1)) + "{}" + new string('}', depth - 1)
            : new string('[', depth) + new string(']', depth);
        foreach (var strings in new[] { false, true })
        {
            if (read)
            {
                Assert.NotNull(JsonText.Parse(json, strings: strings));
            }
            else
            {
                Assert.Equal("JSON trop imbriqué : plus de 64 niveaux",
                             Assert.Throws<FormatException>(() => JsonText.Parse(json, strings: strings)).Message);
            }
        }
    }

    private const string NotText = "chaîne qui n'est pas du texte : surrogate UTF-16 isolé (\\ud800 à \\udfff sans sa paire)";
    private const string TooDeep = "JSON trop imbriqué : plus de 64 niveaux";
    private const string TooLong = "nombre entier de plus de 4300 chiffres";

    [Fact]
    public void A_string_that_is_not_text_is_refused_only_when_asked()
    {
        // La configuration, l'index, les instantanés, les jeux de questions, l'API HTTP et GET /v1/models le demandent ;
        // les réponses du service IA et les prompts le laissent à leurs contrôles.
        const string json = "{\"a\": \"x\\ud800\"}";
        Assert.NotNull(JsonText.Parse(json));
        JsonText.ParseDocument(json).Dispose();
        // Les chiffres de l'échappement en majuscules aussi (« \uD800 ») : JSON permet les deux casses (RFC 8259).
        foreach (var text in new[] { json, "{\"x\\udc00\": 1}", "[\"\\ud83d\"]", "\"\\ude00\"", "[\"\\uD800\"]", "{\"\\uDFFF\": 1}",
                                     "[\"a\\uDc00\"]" })
        {
            Assert.Equal(NotText, Assert.Throws<FormatException>(() => JsonText.Parse(text, strings: true)).Message);
            Assert.Equal(NotText, Assert.Throws<FormatException>(() => JsonText.ParseDocument(text, strings: true)).Message);
        }
        Assert.Equal("\U0001F600", JsonText.Parse("[\"\\ud83d\\ude00\"]", strings: true)![0]!.GetValue<string>());   // une paire complète
    }

    [Fact]
    public void An_integer_of_more_than_4300_digits_is_refused_only_when_asked()
    {
        // La configuration et GET /v1/models le demandent (maxDigits). 4300 chiffres, signe non compté, passent ; un réel
        // n'est pas un entier.
        foreach (var json in new[] { "[" + new string('1', 4301) + "]", "[-" + new string('1', 4301) + "]", "{\"a\": " + new string('9', 5000) + "}" })
        {
            Assert.NotNull(JsonText.Parse(json));
            Assert.Equal(TooLong, Assert.Throws<FormatException>(() => JsonText.Parse(json, maxDigits: JsonText.MaxDigits)).Message);
            Assert.Equal(TooLong, Assert.Throws<FormatException>(() => JsonText.ParseDocument(json, maxDigits: JsonText.MaxDigits)).Message);
        }
        foreach (var json in new[] { "[" + new string('1', 4300) + "]", "[-" + new string('1', 4300) + "]", "[" + new string('1', 5000) + ".5]" })
        {
            using var document = JsonText.ParseDocument(json, maxDigits: JsonText.MaxDigits);
            Assert.Equal(1, document.RootElement.GetArrayLength());
        }
    }

    /// <summary>
    /// Une syntaxe invalide, NaN et Infinity compris (ce n'est pas du JSON) : <see cref="JsonException"/>, avec le message
    /// de System.Text.Json. Une chaîne non fermée court jusqu'à la fin du texte : la pré-lecture n'en compte pas les
    /// crochets.
    /// </summary>
    public static readonly TheoryData<string> InvalidSyntax = new()
    {
        "{pas json",
        "[1,]",
        "[\"" + new string('[', 1001),
        "NaN",
        "[1 , Infinity]",
        """{"a": [-Infinity]}""",
    };

    [Theory]
    [MemberData(nameof(InvalidSyntax))]
    public void Invalid_syntax_is_a_json_exception_as_before(string json) =>
        Assert.Equal(Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(json)).Message,
                     Assert.ThrowsAny<JsonException>(() => JsonText.Parse(json)).Message);

    /// <summary>
    /// Au-delà de 900 niveaux, le texte est refusé d'emblée, avant la lecture, même si un autre défaut vient avant ; les
    /// crochets et les accolades d'une chaîne n'y comptent pas (un antislash y garde l'octet qui le suit, quel qu'il soit ;
    /// hors d'une chaîne, il ne garde rien). À 900 niveaux et moins, la lecture s'arrête au premier défaut qu'elle
    /// rencontre : un défaut placé au-delà du 64e niveau n'est pas atteint (plus de 64 niveaux).
    /// </summary>
    public static readonly TheoryData<string, string> FirstDefect = new()
    {
        { """{"a": 1, "a": 2, "x": """ + Bytes.Nested(100_000) + "}", TooDeep },
        { """[{"a": 1, "a": 2}, """ + Bytes.Nested(900) + "]", TooDeep },   // 901 niveaux
        { """[{"a": 1, "a": 2}, """ + Bytes.Nested(899) + "]", "clé « a » en double" },   // 900 niveaux
        { new string('[', 900) + "NaN" + new string(']', 900), TooDeep },   // au fond de 900 niveaux : pas atteint
        { new string('[', 901) + "NaN" + new string(']', 901), TooDeep },   // au fond de 901 niveaux
        { new string('[', 990) + "NaN" + new string(']', 990), TooDeep },
        { "[" + Bytes.Nested(899) + ", NaN]", TooDeep },   // après 900 niveaux : pas atteint
        { "[" + Bytes.Nested(900) + ", NaN]", TooDeep },   // après 901 niveaux
        { "[NaN, " + Bytes.Nested(1000) + "]", TooDeep },
        { "[1 2, " + Bytes.Nested(1000) + "]", TooDeep },   // même une erreur de syntaxe
        { """{"x": """ + Bytes.Nested(2000) + """, "a": 1, "a": 2}""", TooDeep },
        { "[\"" + new string('[', 1001) + "\", {\"a\": 1, \"a\": 2}]", "clé « a » en double" },   // des crochets dans une chaîne
        { "[\"\\\"" + new string('[', 1001) + "\", {\"a\": 1, \"a\": 2}]", "clé « a » en double" },   // après un guillemet échappé
        { "[\"\\\\\", " + Bytes.Nested(1000) + ", {\"a\": 1, \"a\": 2}]", TooDeep },   // « \\ » ferme la chaîne
        { "[\"]}\", NaN, " + Bytes.Nested(1000) + "]", TooDeep },   // des fermants dans une chaîne
        { "[\"\\\n\", " + Bytes.Nested(1000) + "]", TooDeep },   // « \ » garde même un saut de ligne
        { "\\" + Bytes.Nested(1001), TooDeep },   // hors chaîne, « \ » ne garde rien
    };

    [Theory]
    [MemberData(nameof(FirstDefect))]
    public void The_defect_said_is_the_first_met_after_the_900_level_check(string json, string message)
    {
        Assert.Equal(message, Assert.Throws<FormatException>(() => JsonText.Parse(json)).Message);
        Assert.Equal(message, Assert.Throws<FormatException>(() => JsonText.Parse(json, strings: true, maxDigits: JsonText.MaxDigits)).Message);
    }
}
