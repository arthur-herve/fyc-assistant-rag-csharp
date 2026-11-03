// Les réponses du service IA arrivent par le réseau, et rien ne garantit qu'elles suivent le contrat. Une autre
// forme, du JSON qui n'est pas strict ou un autre encodage donnent une erreur qui le dit, jamais un plantage.

using System.Net;
using System.Text;
using System.Text.Json;
using Assistant.Application;
using Assistant.Infrastructure;
using Xunit;

namespace Assistant.Tests;

public class AiServiceResponseTests
{
    private const string Missing = "Réponse du service IA incomplète, champs manquants : ";
    private const string Incoherent = "Réponse du service IA incohérente : ";
    private const string NotVectors = Incoherent + "« vectors » doit être une liste de listes de nombres";

    private static JsonTransport Answering(string json) => (_, _) =>
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    };

    /// <summary>La réponse JSON du service et le message d'erreur attendu.</summary>
    public static TheoryData<string, string> MalformedEmbeddings => new()
    {
        { "null", Missing + "[model, dimension, vectors]" },
        { "5", Missing + "[model, dimension, vectors]" },
        { "\"model dimension vectors\"", Missing + "[model, dimension, vectors]" },
        { "[]", Missing + "[model, dimension, vectors]" },
        { """{"vectors": [[1.0]]}""", Missing + "[model, dimension]" },
        { """{"model": 5, "dimension": 2, "vectors": [[0.1, 0.2]]}""", Incoherent + "« model » doit être un texte" },
        { """{"model": null, "dimension": 2, "vectors": [[0.1, 0.2]]}""", Incoherent + "« model » doit être un texte" },
        // Une demi-paire de substitution isolée n'est pas du texte : GetString lèverait InvalidOperationException.
        { """{"model": "\ud800", "dimension": 2, "vectors": [[0.1, 0.2]]}""", Incoherent + "« model » doit être un texte" },
        { """{"model": "m", "dimension": "abc", "vectors": [[0.1, 0.2]]}""", Incoherent + "« dimension » doit être un entier" },
        { """{"model": "m", "dimension": true, "vectors": [[0.1, 0.2]]}""", Incoherent + "« dimension » doit être un entier" },
        { """{"model": "m", "dimension": 2.0, "vectors": [[0.1, 0.2]]}""", Incoherent + "« dimension » doit être un entier" },
        // Un int va de int.MinValue à int.MaxValue : les deux premières en sortent, la troisième est la borne haute.
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
        // 10^400, écrit avec 401 chiffres : trop grand pour un double.
        { """{"model": "m", "dimension": 2, "vectors": [[0.1, 1""" + new string('0', 400) + "]]}", NotVectors },
        { """{"model": "m", "dimension": 2, "vectors": [[0.1]]}""", Incoherent + "1 vecteurs de 2 dimensions attendus, reçu 1 de [1] dimensions" },
        { """{"model": "m", "dimension": 2, "vectors": [[0.1, 0.2], [0.3, 0.4]]}""", Incoherent + "1 vecteurs de 2 dimensions attendus, reçu 2 de [2] dimensions" },
    };

    private const string Problem = """{"error": {"code": "backend_error", "message": "modele absent", "retryable": false}}""";

    /// <summary>
    /// Pour chaque cas : le corps d'une réponse 502, s'il est précédé d'une marque d'ordre des octets, le message
    /// attendu, et si l'erreur est passagère. Seule une erreur au format du contrat, avec un message textuel, est
    /// lue, avec ou sans marque d'ordre des octets. Son retryable compte s'il vaut false.
    /// </summary>
    public static TheoryData<string, bool, string, bool> ContractErrors => new()
    {
        { Problem, false, "modele absent", false },
        { Problem, true, "modele absent", false },
        { """{"error": {"message": "surcharge"}}""", false, "surcharge", true },
    };

    /// <summary>Des corps de réponse 502 qui ne suivent pas le format d'erreur du contrat.</summary>
    public static TheoryData<string> OtherErrorBodies => new()
    {
        """{"error": {"message": null, "retryable": false}}""",
        """{"error": {"message": 5, "retryable": false}}""",
        """{"error": {"message": "\ud800", "retryable": false}}""",
        """{"error": {"message": "x", "retryable": NaN}}""",
        """{"error": "x"}""",
        "<html>proxy</html>",
        Bytes.Deep,
        // Du JSON qui n'est pas strict, clé en double ou 65 niveaux, n'est pas une erreur au format du contrat.
        """{"error": {"message": "a", "message": "b"}}""",
        """{"error": {"message": "x"}, "x": """ + Bytes.Nested(64) + "}",
    };

    [Theory]
    [MemberData(nameof(MalformedEmbeddings))]
    public void An_embeddings_response_of_another_shape_is_a_non_transient_error_not_a_crash(string json, string message)
    {
        var error = Assert.Throws<AiServiceException>(() => new HttpEmbedder("http://ia", "m", transport: Answering(json)).EmbedQuery("x"));
        Assert.Equal(message, error.Message);
        Assert.False(error.Transient);   // réessayer ne changerait rien
    }

    [Theory]
    [MemberData(nameof(ContractErrors))]
    public void An_error_in_the_contract_format_is_read(string body, bool bom, string message, bool transient)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        using var service = new FixedAnswerService(bom ? Bytes.WithBom(bytes) : bytes, HttpStatusCode.BadGateway);
        var error = Assert.Throws<AiServiceException>(() => new HttpEmbedder(service.Url, "m", TimeSpan.FromSeconds(5)).EmbedQuery("x"));
        Assert.Equal($"Service IA : HTTP 502 — {message}", error.Message);
        Assert.Equal(transient, error.Transient);
    }

    [Theory]
    [MemberData(nameof(OtherErrorBodies))]
    public void An_error_body_of_another_shape_is_quoted_as_is_and_stays_transient(string body)
    {
        using var service = new FixedAnswerService(Encoding.UTF8.GetBytes(body), HttpStatusCode.BadGateway);
        var error = Assert.Throws<AiServiceException>(() => new HttpEmbedder(service.Url, "m", TimeSpan.FromSeconds(5)).EmbedQuery("x"));
        Assert.Equal($"Service IA : HTTP 502 — {body}", error.Message);
        Assert.True(error.Transient);
    }

    /// <summary>
    /// Des corps que System.Text.Json ne lit pas comme du JSON : une page de proxy, NaN, -Infinity, et des listes
    /// imbriquées sur 100 000 niveaux.
    /// </summary>
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

    /// <summary>
    /// Une clé en double, dont System.Text.Json garderait la dernière valeur sans rien dire, et plus de 64 niveaux
    /// sont refusés avec un message en français.
    /// </summary>
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
    }

    [Fact]
    public void A_response_with_a_byte_order_mark_is_read()
    {
        var body = Bytes.WithBom(Encoding.UTF8.GetBytes("""{"model": "m", "dimension": 1, "vectors": [[1.0]]}"""));
        using var service = new FixedAnswerService(body);
        Assert.Equal(new[] { 1.0 }, new HttpEmbedder(service.Url, "m", TimeSpan.FromSeconds(5)).EmbedQuery("x"));
    }

    /// <summary>
    /// Le corps des requêtes part en UTF-8, avec les accents tels quels. Relu, chaque texte revient à l'identique,
    /// même avec des guillemets, des antislashs, des caractères de contrôle ou un emoji. Les autres champs sont
    /// vérifiés dans HttpContractTests.
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
    }

    [Theory]
    [InlineData("vectors", NotVectors)]
    [InlineData("dimension", Incoherent + "« dimension » doit être un entier")]
    public void An_integer_of_thousands_of_digits_is_a_number_out_of_range(string field, string message)
    {
        // Pour System.Text.Json, c'est un nombre comme un autre, juste trop grand pour un double fini ou un int.
        var huge = "1" + new string('0', 5000);
        var body = field == "vectors"
            ? $$"""{"model": "m", "dimension": 2, "vectors": [[0.1, {{huge}}]]}"""
            : $$"""{"model": "m", "dimension": {{huge}}, "vectors": [[0.1, 0.2]]}""";
        using var service = new FixedAnswerService(Encoding.UTF8.GetBytes(body));
        var error = Assert.Throws<AiServiceException>(() => new HttpEmbedder(service.Url, "m", TimeSpan.FromSeconds(5)).EmbedQuery("x"));
        Assert.Equal(message, error.Message);
    }

    /// <summary>
    /// Un faux service IA qui répond toujours les mêmes octets, avec le même statut, sur un port libre, et garde les corps
    /// des requêtes reçues.
    /// </summary>
    private sealed class FixedAnswerService : IDisposable
    {
        private readonly HttpListener _listener;

        public string Url { get; }

        /// <summary>
        /// Les corps reçus, dans l'ordre. Chacun est enregistré avant l'envoi de la réponse : quand l'appel revient,
        /// le test le trouve déjà là.
        /// </summary>
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
