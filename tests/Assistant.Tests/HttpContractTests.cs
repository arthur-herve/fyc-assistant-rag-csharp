// Le contrat HTTP, côté application, vérifié contre un faux service IA.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Application;
using Assistant.Infrastructure;
using Xunit;

namespace Assistant.Tests;

/// <summary>Ce que l'application envoie au service IA, et ce qu'elle attend en retour (docs/contrat-http.md).</summary>
public class HttpContractTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _url;
    private readonly List<(string Path, JsonObject Body)> _requests = new();
    private readonly List<long> _contentLengths = new();

    public HttpContractTests()
    {
        _listener = TestPorts.Listener();
        _url = _listener.Prefixes.Single();
        _ = Task.Run(Serve);
    }

    private static readonly TimeSpan Short = TimeSpan.FromSeconds(5);

    private async Task Serve()
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
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
            lock (_requests)
            {
                _requests.Add((context.Request.Url!.AbsolutePath, body));
                _contentLengths.Add(context.Request.ContentLength64);
            }
            string response;
            var status = 200;
            if (context.Request.Url.AbsolutePath == "/v1/embeddings")
            {
                var n = body["inputs"]!.AsArray().Count;
                var dimension = body["model"]!.GetValue<string>() == "dimension-fausse" ? 4 : 3;   // annonce 4, envoie 3
                response = JsonSerializer.Serialize(new { model = "ollama:nomic-embed-text@0a109f422b47", alias = "nomic", dimension,
                                                         vectors = Enumerable.Repeat(new[] { 0.1, 0.2, 0.3 }, n) });
            }
            else if (body["model"]!.GetValue<string>() == "inconnu")
            {
                status = 404;
                response = """{"error": {"code": "unknown_model", "message": "modèle de génération inconnu : inconnu"}}""";
            }
            else if (body["model"]!.GetValue<string>() is "absent" or "surcharge")
            {
                status = 502;
                response = JsonSerializer.Serialize(new
                {
                    error = new { code = "backend_error", message = "moteur en échec", retryable = body["model"]!.GetValue<string>() == "surcharge" },
                });
            }
            else
            {
                response = JsonSerializer.Serialize(new { model = "ollama:llama3.2:3b@a80c", alias = "llama3-2-3b", text = "Deux jours [1].", duration_ms = 5 });
            }
            var bytes = Encoding.UTF8.GetBytes(response);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }

    [Fact]
    public void Embeddings_request_declares_an_intent_and_returns_the_concrete_model()
    {
        var batch = new HttpEmbedder(_url, "nomic", Short).EmbedQuery("Combien de jours ?");
        Assert.Equal("ollama:nomic-embed-text@0a109f422b47", batch.Model);
        Assert.Equal(3, batch.Dimension);
        var (path, body) = _requests.Single();
        Assert.Equal("/v1/embeddings", path);
        Assert.Equal("nomic", body["model"]!.GetValue<string>());
        Assert.Equal("query", body["input_type"]!.GetValue<string>());
    }

    [Fact]
    public void Vectors_that_do_not_match_the_announced_dimension_are_refused()
    {
        var error = Assert.Throws<AiServiceException>(() => new HttpEmbedder(_url, "dimension-fausse", Short).EmbedQuery("a"));
        Assert.Contains("incohérente", error.Message);
    }

    [Fact]
    public void Documents_are_sent_as_documents()
    {
        var batch = new HttpEmbedder(_url, "nomic", Short).EmbedDocuments(new[] { "a", "b" });
        Assert.Equal(2, batch.Vectors.Count);
        Assert.Equal("document", _requests.Single().Body["input_type"]!.GetValue<string>());
    }

    [Fact]
    public void Requests_are_sent_with_a_content_length_not_in_chunks()
    {
        new HttpEmbedder(_url, "nomic", Short).EmbedQuery("Combien de jours ?");
        Assert.True(_contentLengths.Single() > 0);   // le vrai service (Python) refuse les envois en morceaux
    }

    [Fact]
    public void Generation_request_sends_the_prompt_built_by_the_application()
    {
        new HttpGenerator(_url, "llama3-2-3b", Short).Generate(new GenerationRequest("sys", "prompt", 0.2, 150, 42));
        var (path, body) = _requests.Single();
        Assert.Equal("/v1/generate", path);
        Assert.Equal("llama3-2-3b", body["model"]!.GetValue<string>());
        Assert.Equal("sys", body["system"]!.GetValue<string>());
        Assert.Equal("prompt", body["prompt"]!.GetValue<string>());
        Assert.Equal(0.2, body["temperature"]!.GetValue<double>());
        Assert.Equal(150, body["max_tokens"]!.GetValue<int>());
        Assert.Equal(42, body["seed"]!.GetValue<int>());
    }

    [Fact]
    public void Generation_returns_the_concrete_model()
    {
        var generation = new HttpGenerator(_url, "llama3-2-3b", Short).Generate(new GenerationRequest("s", "p", 0.2, 10));
        Assert.Equal("ollama:llama3.2:3b@a80c", generation.Model);
    }

    [Fact]
    public void An_unset_seed_is_sent_as_null()
    {
        new HttpGenerator(_url, "llama3-2-3b", Short).Generate(new GenerationRequest("sys", "prompt", 0.2, 150));
        Assert.True(_requests.Single().Body.TryGetPropertyValue("seed", out var seed));
        Assert.Null(seed);
    }

    [Fact]
    public void A_refused_request_is_not_transient()
    {
        var error = Assert.Throws<AiServiceException>(() => new HttpGenerator(_url, "inconnu", Short).Generate(new GenerationRequest("s", "p", 0.2, 10)));
        Assert.Contains("HTTP 404", error.Message);
        Assert.False(error.Transient);
    }

    [Theory]
    [InlineData("surcharge", true)]
    [InlineData("absent", false)]
    public void A_502_is_transient_unless_the_service_says_retrying_is_useless(string model, bool transient)
    {
        // Modèle absent : le service le signale (retryable = false), le client ne réessaie pas pour rien.
        var error = Assert.Throws<AiServiceException>(() => new HttpGenerator(_url, model, Short).Generate(new GenerationRequest("s", "p", 0.2, 10)));
        Assert.Equal(transient, error.Transient);
    }

    [Fact]
    public void An_unreachable_service_is_transient() =>
        Assert.True(Assert.Throws<AiServiceException>(() => new HttpEmbedder("http://127.0.0.1:1", "x", TimeSpan.FromSeconds(2)).EmbedQuery("a")).Transient);

    [Fact]
    public void An_invalid_address_is_an_explicit_non_transient_error()
    {
        var error = Assert.Throws<AiServiceException>(() => new HttpEmbedder("localhost:8100", "x", Short).EmbedQuery("a"));
        Assert.False(error.Transient);
        Assert.Contains("invalide", error.Message);
    }
}
