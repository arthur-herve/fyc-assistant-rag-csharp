// L'API HTTP de l'application, contre un Container monté sur les doubles : aucun service IA.

using System.Net;
using System.Text;
using System.Text.Json;
using Assistant.Application;
using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

public sealed class HttpApiTests : IDisposable
{
    private readonly HttpApi _api;
    private readonly HttpClient _client = new();
    private readonly FakeIndex _index;
    private readonly ScriptedGenerator _generator = new("Deux jours par semaine [1].");

    public HttpApiTests()
    {
        var config = AppConfig.Load();
        var embedder = new KeywordEmbedder();
        _index = new FakeIndex();
        var source = new ListSource(Fakes.Doc("teletravail", "Deux jours de télétravail par semaine."), Fakes.Doc("grille", "Salaire senior.", "rh"));
        var indexCorpus = new IndexCorpus(source, new WholeDocumentSplitter(), embedder, _index, new FixedClock());
        var settings = new AskSettings(MinScore: 0.5);
        var ask = new AskQuestion(embedder, _index, _generator, new StaticPrompts(), settings);
        var container = new Container(config, indexCorpus, ask, new SearchPassages(embedder, _index),
            new CheckStatus(source, new WholeDocumentSplitter(), embedder, _index, new StaticPrompts()),
            new RecordSnapshot(ask, new MemorySnapshotStore(), new FixedClock(), new Dictionary<string, object?>()),
            new MemorySnapshotStore(), _index, embedder, new StaticPrompts(), settings, "fake-keywords", "fake-llm");

        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _api = new HttpApi(container, "127.0.0.1", port, quiet: true);
        _api.Start();
    }

    public void Dispose()
    {
        _api.Dispose();
        _client.Dispose();
    }

    private (HttpStatusCode Status, JsonElement Body) Get(string path)
    {
        var response = _client.GetAsync(_api.Url + path).Result;
        return (response.StatusCode, JsonSerializer.Deserialize<JsonElement>(response.Content.ReadAsStringAsync().Result));
    }

    private (HttpStatusCode Status, JsonElement Body) Post(string path, string json)
    {
        var response = _client.PostAsync(_api.Url + path, new StringContent(json, Encoding.UTF8, "application/json")).Result;
        return (response.StatusCode, JsonSerializer.Deserialize<JsonElement>(response.Content.ReadAsStringAsync().Result));
    }

    [Fact]
    public void Health_then_index_then_ask()
    {
        var (status, body) = Get("/health");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("index").ValueKind);   // pas encore d'index

        (status, body) = Post("/v1/ask", """{"user": "alice", "question": "télétravail"}""");
        Assert.Equal(HttpStatusCode.Conflict, status);                            // 409 : index absent
        Assert.Equal("index_unusable", body.GetProperty("error").GetProperty("code").GetString());

        (status, body) = Post("/v1/index", "");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, body.GetProperty("chunk_count").GetInt32());

        (status, body) = Post("/v1/ask", """{"user": "alice", "question": "Combien de jours de télétravail ?"}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("answered", body.GetProperty("status").GetString());
        Assert.Equal("teletravail", body.GetProperty("sources")[0].GetProperty("document_id").GetString());
        Assert.Equal("test-v1", body.GetProperty("trace").GetProperty("prompt_version").GetString());

        (status, _) = Get("/v1/status");
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public void Errors_have_the_same_codes_as_the_python_version()
    {
        Post("/v1/index", "");
        Assert.Equal(HttpStatusCode.Forbidden, Post("/v1/ask", """{"user": "mallory", "question": "Q ?"}""").Status);
        var (status, body) = Post("/v1/ask", """{"user": "alice", "question": "   "}""");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_question", body.GetProperty("error").GetProperty("code").GetString());
        (status, body) = Post("/v1/ask", "{pas du json");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_json", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, Get("/v1/inconnu").Status);
        Assert.Equal(HttpStatusCode.NotFound, Post("/v1/inconnu", "{}").Status);
    }

    [Fact]
    public void Status_is_409_when_the_index_is_stale()
    {
        Post("/v1/index", "");
        // Un index construit avec un autre modèle : status doit dire « à refaire ».
        var manifest = _index.Manifest()!;
        _index.Replace(manifest with { EmbeddingModel = "autre-modele" }, _index.Chunks.ToList(), _index.Vectors.ToList());
        var (status, body) = Get("/v1/status");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.False(body.GetProperty("up_to_date").GetBoolean());
    }
}
