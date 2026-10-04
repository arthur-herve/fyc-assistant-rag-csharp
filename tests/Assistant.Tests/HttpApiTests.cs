// L'API HTTP de l'application, contre un Container monté sur les doubles : aucun service IA.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Assistant.Application;
using Assistant.Cli;
using Assistant.Domain;
using Xunit;

namespace Assistant.Tests;

public sealed class HttpApiTests : IDisposable
{
    private const string Question = """{"user": "alice", "question": "Combien de jours de télétravail ?"}""";
    private readonly HttpApi _api;
    private readonly HttpClient _client = new();
    private readonly FakeIndex _index = new();

    public HttpApiTests() => _api = StartOnAFreePort(MakeContainer(_index));

    /// <summary>Le Container de <c>serve</c>, monté sur les doubles.</summary>
    private static Container MakeContainer(IVectorIndex index, IGenerator? generator = null)
    {
        var embedder = new KeywordEmbedder();
        var source = new ListSource(Fakes.Doc("teletravail", "Deux jours de télétravail par semaine."), Fakes.Doc("grille", "Salaire senior.", "rh"));
        var settings = new AskSettings(MinScore: 0.5);
        var ask = new AskQuestion(embedder, index, generator ?? new ScriptedGenerator("Deux jours par semaine [1]."), new StaticPrompts(), settings);
        return new Container(AppConfig.Load(), new IndexCorpus(source, new WholeDocumentSplitter(), embedder, index, new FixedClock()), ask,
            new SearchPassages(embedder, index), new CheckStatus(source, new WholeDocumentSplitter(), embedder, index, new StaticPrompts()),
            new RecordSnapshot(ask, new MemorySnapshotStore(), new FixedClock(), new Dictionary<string, object?>()),
            new MemorySnapshotStore(), index, new StaticPrompts(), settings, "fake-keywords");
    }

    private static HttpApi StartOnAFreePort(Container container, Action<string>? log = null) =>
        TestPorts.StartOnAFreePort(port => new HttpApi(container, "127.0.0.1", port, quiet: log is null, log), api => api.Start());

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

    private (HttpStatusCode Status, JsonElement Body) Post(string path, string json, HttpApi? api = null)
    {
        var response = _client.PostAsync((api ?? _api).Url + path, new StringContent(json, Encoding.UTF8, "application/json")).Result;
        return (response.StatusCode, JsonSerializer.Deserialize<JsonElement>(response.Content.ReadAsStringAsync().Result));
    }

    private static string? Code(JsonElement body) => body.GetProperty("error").GetProperty("code").GetString();

    /// <summary>Requête en octets bruts (ce que HttpClient n'enverrait pas) : statut, en-têtes et corps reçus.</summary>
    private static (int Status, string Head, string Body) Raw(HttpApi api, string request)
    {
        using var tcp = new TcpClient("127.0.0.1", new Uri(api.Url).Port) { ReceiveTimeout = 5000 };
        var stream = tcp.GetStream();
        stream.Write(Encoding.UTF8.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var response = reader.ReadToEnd();   // « Connection: close » : la connexion se ferme après la réponse
        var end = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return (int.Parse(response.Split(' ')[1]), response[..end], response[(end + 4)..]);
    }

    private static string Request(string method, string target, string body = "") =>
        $"{method} {target} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";

    [Fact]
    public void Health_then_index_then_ask()
    {
        var (status, body) = Get("/health");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("index").ValueKind);   // pas encore d'index

        (status, body) = Post("/v1/ask", """{"user": "alice", "question": "télétravail"}""");
        Assert.Equal(HttpStatusCode.Conflict, status);                            // 409 : index absent
        Assert.Equal("index_unusable", Code(body));

        (status, body) = Post("/v1/index", "");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, body.GetProperty("chunk_count").GetInt32());

        (status, body) = Post("/v1/ask", Question);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("answered", body.GetProperty("status").GetString());
        Assert.Equal("teletravail", body.GetProperty("sources")[0].GetProperty("document_id").GetString());
        Assert.Equal("test-v1", body.GetProperty("trace").GetProperty("prompt_version").GetString());

        (status, _) = Get("/v1/status");
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public void A_body_is_indented_json_with_accents_as_they_are()
    {
        // Le JSON de ask --json (Presenter.ToJson) : indenté, accents et guillemets français tels quels, en UTF-8 comme
        // l'annonce l'en-tête ; relu, les bons champs.
        var (status, head, body) = Raw(_api, Request("POST", "/v1/ask", """{"user": "a", "user": "b"}"""));
        Assert.Equal(400, status);
        Assert.Contains("\r\nContent-Type: application/json; charset=utf-8", head);
        Assert.StartsWith("{" + Environment.NewLine + "  \"error\": {", body);
        Assert.Contains("\"message\": \"clé « user » en double\"", body);
        using var json = JsonDocument.Parse(body);
        var error = json.RootElement.GetProperty("error");
        Assert.Equal(("invalid_json", "clé « user » en double"), (error.GetProperty("code").GetString(), error.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task Errors_have_their_http_status_and_json_code()
    {
        Post("/v1/index", "");
        Assert.Equal(HttpStatusCode.Forbidden, Post("/v1/ask", """{"user": "mallory", "question": "Q ?"}""").Status);
        var (status, body) = Post("/v1/ask", """{"user": "alice", "question": "   "}""");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_question", Code(body));
        (status, body) = Post("/v1/ask", "{pas du json");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_json", Code(body));
        Assert.Equal(HttpStatusCode.NotFound, Get("/v1/inconnu").Status);
        Assert.Equal(HttpStatusCode.NotFound, Post("/v1/inconnu", "{}").Status);
        (status, body) = Post("/v1/ask", "[]");
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_json"), (status, Code(body)));
        (status, body) = Post("/v1/ask", "   ");   // des espaces ne sont pas du JSON
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_json"), (status, Code(body)));
        (status, body) = Post("/v1/ask", """{"user": "alice", "question": 42}""");
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_request"), (status, Code(body)));
        (status, body) = Post("/v1/ask", """{"user": "alice", "question": null}""");
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_question"), (status, Code(body)));
        // JSON valide, mais encodé en latin-1 : c'est le décodage UTF-8 strict qui doit le refuser.
        var latin1 = await _client.PostAsync(_api.Url + "/v1/ask", new ByteArrayContent(Encoding.Latin1.GetBytes("""{"user": "alice", "question": "télétravail"}""")));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_json"),
                     (latin1.StatusCode, Code(JsonSerializer.Deserialize<JsonElement>(await latin1.Content.ReadAsStringAsync()))));
    }

    // Le corps est du JSON en UTF-8 : la marque d'ordre des octets UTF-8 est acceptée ; un autre encodage, même annoncé
    // par sa propre marque (UTF-16, UTF-32), est refusé comme le latin-1, avec la position de l'octet fautif.
    [Theory]
    [InlineData("utf-8", HttpStatusCode.OK, null)]
    [InlineData("utf-16", HttpStatusCode.BadRequest, "pas en UTF-8 (octet 0xff à la position 0)")]
    [InlineData("utf-32", HttpStatusCode.BadRequest, "pas en UTF-8 (octet 0xff à la position 0)")]
    public async Task A_body_is_utf8_its_byte_order_mark_accepted_other_encodings_refused(string name, HttpStatusCode expected, string? message)
    {
        Post("/v1/index", "");
        var encoding = Encoding.GetEncoding(name);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(Question)).ToArray();
        var response = await _client.PostAsync(_api.Url + "/v1/ask", new ByteArrayContent(bytes));
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, response.StatusCode);
        if (message is not null)
        {
            Assert.Equal(("invalid_json", message), (Code(body), body.GetProperty("error").GetProperty("message").GetString()));
        }
    }

    [Fact]
    public void A_separator_alone_is_a_question()
    {
        // U+001C n'est pas un blanc pour Trim() : la question est cherchée, pas refusée comme vide (400).
        Post("/v1/index", "");
        var (status, body) = Post("/v1/ask", """{"user": "alice", "question": "\u001c"}""");
        Assert.True(status == HttpStatusCode.OK, body.ToString());
        Assert.Equal(("\u001c", "no_relevant_source"), (body.GetProperty("question").GetString(), body.GetProperty("status").GetString()));
    }

    public static TheoryData<string, string> NotStrictJson => new()
    {
        { """{"user": "zoe", "user": "alice", "question": "jours"}""", "clé « user » en double" },
        { """{"x": {"a": 1, "a": 2}, "user": "alice", "question": "jours"}""", "clé « a » en double" },
        { """{"user": "alice", "question": "\ud800"}""", "surrogate UTF-16 isolé" },
        { """{"\udc00": 1, "user": "alice"}""", "surrogate UTF-16 isolé" },
        { """{"user": "alice", "x": ["\ud83d"]}""", "surrogate UTF-16 isolé" },
        { Nested(65), "JSON trop imbriqué : plus de 64 niveaux" },
        { Nested(65, objects: true), "JSON trop imbriqué : plus de 64 niveaux" },
        { new string('[', 100_000) + new string(']', 100_000), "JSON trop imbriqué : plus de 64 niveaux" },
    };

    private static string WithX(string value) => """{"user": "alice", "question": "jours", "x": """ + value + "}";

    /// <summary>Le corps, puis levels - 1 listes (ou objets) imbriquées dans « x » : levels niveaux en tout.</summary>
    private static string Nested(int levels, bool objects = false) => WithX(objects
        ? string.Concat(Enumerable.Repeat("""{"a": """, levels - 2)) + "{}" + new string('}', levels - 2)
        : new string('[', levels - 1) + new string(']', levels - 1));

    [Theory]
    [MemberData(nameof(NotStrictJson))]
    public void A_json_body_is_strict(string json, string message)
    {
        // Sans ce contrôle : 500 et une pile dans le journal (clé en double, surrogate isolé).
        Post("/v1/index", "");
        var (status, body) = Post("/v1/ask", json);
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_json"), (status, Code(body)));
        Assert.Contains(message, body.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void Nan_and_infinity_are_not_json(string literal)
    {
        // Ce n'est pas du JSON : une erreur de syntaxe, avec le message de System.Text.Json, comme toute autre.
        Post("/v1/index", "");
        var (status, body) = Post("/v1/ask", WithX(literal));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_json"), (status, Code(body)));
        Assert.Equal(Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(WithX(literal))).Message,
                     body.GetProperty("error").GetProperty("message").GetString());
    }

    /// <summary>
    /// Plus de 900 niveaux : le corps est refusé d'emblée, avant la lecture, même si un autre défaut vient avant. À 900
    /// niveaux et moins, la lecture s'arrête au premier défaut rencontré : un « NaN » placé au-delà du 64e niveau n'est pas
    /// atteint.
    /// </summary>
    public static TheoryData<string, string> FirstDefect => new()
    {
        { """{"x": {"a": 1, "a": 2}, "y": """ + Bytes.Nested(900) + """, "user": "alice", "question": "jours"}""",
          "JSON trop imbriqué : plus de 64 niveaux" },
        { """{"user": "alice", "question": """ + new string('[', 899) + "NaN" + new string(']', 899) + "}",
          "JSON trop imbriqué : plus de 64 niveaux" },
        { """{"user": "alice", "question": """ + new string('[', 900) + "NaN" + new string(']', 900) + "}",
          "JSON trop imbriqué : plus de 64 niveaux" },
    };

    [Theory]
    [MemberData(nameof(FirstDefect))]
    public void The_defect_said_is_the_first_met_after_the_900_level_check(string json, string message)
    {
        var (status, body) = Post("/v1/ask", json);
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_json"), (status, Code(body)));
        Assert.Equal(message, body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void Strict_json_still_accepts_valid_bodies_up_to_64_levels()
    {
        Post("/v1/index", "");
        var accepted = new[]
        {
            Nested(64), Nested(64, objects: true),
            """{"a": {"user": 1}, "user": "alice", "question": "jours"}""",   // une même clé dans deux objets
            WithX(new string('1', 4301)), WithX("-" + new string('1', 4301)), WithX(new string('1', 5000) + ".5"),   // pas de borne
            """{"user": "alice", "question": "😀 jours"}""",                 // une paire complète est du texte
        };
        Assert.All(accepted, json => Assert.Equal(HttpStatusCode.OK, Post("/v1/ask", json).Status));
    }

    [Theory]
    [InlineData("GET", "/v1/ask", "POST")]
    [InlineData("POST", "/health", "GET, HEAD")]
    [InlineData("PUT", "/v1/index", "POST")]
    [InlineData("DELETE", "/v1/status", "GET, HEAD")]
    [InlineData("TRACE", "/health", "GET, HEAD")]
    public void A_known_route_with_another_method_is_405_with_allow(string method, string path, string allowed)
    {
        // Un TRACE avec un corps, http.sys le refuse lui-même (400 en HTML) : il part sans corps.
        var (status, head, body) = Raw(_api, Request(method, path, method == "TRACE" ? "" : "{}"));
        Assert.Equal((405, "method_not_allowed"), (status, Code(JsonSerializer.Deserialize<JsonElement>(body))));
        Assert.Contains($"\r\nAllow: {allowed}", head);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("TRACE")]
    public void An_unknown_route_is_404_whatever_the_method(string method)
    {
        var (status, head, body) = Raw(_api, Request(method, "/v1/inconnu"));
        Assert.Equal((404, "not_found"), (status, Code(JsonSerializer.Deserialize<JsonElement>(body))));
        Assert.DoesNotContain("Allow:", head);
    }

    [Fact]
    public void Head_is_accepted_wherever_get_is_without_the_body_nor_an_error_in_the_log()
    {
        // RFC 9110 : mêmes statut et en-têtes que GET (Date mise à part), sans corps ;
        // ailleurs, 405 ou 404, sans corps non plus. HttpListener refuse d'écrire un corps pour HEAD : la
        // réponse partirait quand même, mais chaque HEAD laisserait « erreur inattendue » et sa pile dans le journal.
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var api = StartOnAFreePort(MakeContainer(new FakeIndex()), log.Enqueue);
        try
        {
            Post("/v1/index", "", api);
            foreach (var path in new[] { "/health", "/v1/status" })
            {
                var head = Raw(api, $"HEAD {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
                var get = Raw(api, Request("GET", path));
                Assert.Equal((200, ""), (head.Status, head.Body));
                Assert.Equal(WithoutDate(get.Head), WithoutDate(head.Head));
                Assert.Contains($"\r\nContent-Length: {Encoding.UTF8.GetByteCount(get.Body)}\r\n", head.Head + "\r\n");
            }
            foreach (var (path, expected, allowed) in new (string, int, string?)[] { ("/v1/ask", 405, "POST"), ("/v1/inconnu", 404, null) })
            {
                var (status, head, body) = Raw(api, $"HEAD {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
                Assert.Equal((expected, ""), (status, body));
                Assert.Equal(allowed, head.Split("\r\n").SingleOrDefault(line => line.StartsWith("Allow: "))?["Allow: ".Length..]);
            }
        }
        finally
        {
            api.Dispose();   // attend la fin du fil du serveur : le journal est complet
        }
        Assert.DoesNotContain(log, line => line.Contains("erreur inattendue"));
        Assert.Contains(log, line => line.EndsWith("\"HEAD /health\" 200"));
    }

    private static string WithoutDate(string head) => string.Join("\r\n", head.Split("\r\n").Where(line => !line.StartsWith("Date: ")));

    private const string TooLarge = "corps de requête trop volumineux : 16777216 octets au plus (16 Mio)";

    /// <summary>Une question qui annonce <paramref name="length"/> octets de corps, jamais envoyés.</summary>
    private static string Announcing(string length) =>
        $"POST /v1/ask HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\nContent-Length: {length}\r\n\r\n";

    [Theory]
    [InlineData("16777217")]
    [InlineData("9223372036854775807")]
    public void A_body_announced_beyond_16_mib_is_413_without_being_read(string length)
    {
        // La réponse part sans attendre le corps (jamais envoyé ici).
        var (status, _, body) = Raw(_api, Announcing(length));
        var error = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("error");
        Assert.Equal((413, "payload_too_large", TooLarge), (status, error.GetProperty("code").GetString(), error.GetProperty("message").GetString()));
    }

    [WindowsFact]
    public void With_http_sys_a_length_of_2_to_the_63_or_more_is_413_too()
    {
        // De 2^63 à 2^64 − 1, http.sys transmet la requête comme sans corps (ContentLength64 vaut 0) : sans la
        // taille relue dans l'en-tête, /v1/ask répondrait 403 (utilisateur vide). Au-delà, http.sys répond 413
        // lui-même, en HTML. Sous Linux, HttpListener est une autre implémentation : cas propres à Windows.
        foreach (var length in new[] { "9223372036854775808", "18446744073709551615" })
        {
            var (status, _, body) = Raw(_api, Announcing(length));
            Assert.Equal((413, "payload_too_large"), (status, Code(JsonSerializer.Deserialize<JsonElement>(body))));
        }
        var (beyond, head, _) = Raw(_api, Announcing("18446744073709551616"));
        Assert.Equal(413, beyond);
        Assert.Contains("Content-Type: text/html", head);
    }

    [Theory]
    [InlineData(16 * 1024 * 1024, false, 200)]
    [InlineData(16 * 1024 * 1024 + 1, false, 413)]
    [InlineData(16 * 1024 * 1024, true, 200)]
    [InlineData(16 * 1024 * 1024 + 1, true, 413)]
    public void A_body_of_16_mib_passes_one_more_byte_is_413(int size, bool chunked, int expected)
    {
        // En morceaux, la taille n'est connue qu'à la lecture : refusé dès que la limite est franchie. La réponse est lue
        // pendant l'écriture : un 413 part avant la fin du corps, dont le HttpListener géré, sous Linux, ne lit pas le
        // reste (voir StatusReadWhileWriting).
        Post("/v1/index", "");
        const string start = "{\"user\": \"alice\", \"question\": \"jours\", \"x\": \"";   // « x » sert de remplissage
        var body = start + new string('a', size - start.Length - 2) + "\"}";
        var request = chunked
            ? "POST /v1/ask HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\nTransfer-Encoding: chunked\r\n\r\n" + InChunks(body)
            : Request("POST", "/v1/ask", body);
        Assert.Equal(expected, StatusReadWhileWriting(_api, request));
    }

    /// <summary>
    /// Le statut de la réponse, lue pendant que la requête s'écrit, comme le fait un client qui lit une réponse
    /// anticipée. Le 413 de l'application part avant la fin du corps (dès l'en-tête quand le Content-Length l'annonce :
    /// voir HttpApi.ReadBody) ; sous Linux, le HttpListener géré ferme ensuite la connexion sans lire le reste du corps :
    /// un client qui écrit tout avant de lire (<see cref="Raw"/>) n'y verrait que l'échec de son écriture. Une fois la
    /// réponse arrivée, l'écriture peut donc échouer sans que cela compte.
    /// </summary>
    private static int StatusReadWhileWriting(HttpApi api, string request)
    {
        using var tcp = new TcpClient("127.0.0.1", new Uri(api.Url).Port) { ReceiveTimeout = 5000 };
        var stream = tcp.GetStream();
        Exception? failure = null;
        var writer = new Thread(() =>
        {
            try
            {
                stream.Write(Encoding.UTF8.GetBytes(request));
            }
            catch (Exception error)   // toute exception : sortie du fil, elle arrêterait l'hôte de test
            {
                failure = error;
            }
        }) { IsBackground = true };
        writer.Start();
        var line = new StreamReader(stream, Encoding.Latin1).ReadLine();
        writer.Join(TimeSpan.FromSeconds(10));
        return line is null ? throw new IOException("connexion fermée sans réponse", failure) : int.Parse(line.Split(' ')[1]);
    }

    [Fact]
    public void A_chunked_body_is_not_judged_by_a_content_length_sent_with_it()
    {
        // Avec Transfer-Encoding: chunked, Content-Length ne compte pas (RFC 9112), même si http.sys le transmet :
        // 20 000 000 annoncés à côté d'un petit envoi en morceaux ne valent pas 413.
        Post("/v1/index", "");
        var request = "POST /v1/ask HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\nTransfer-Encoding: chunked\r\n"
            + "Content-Length: 20000000\r\n\r\n" + InChunks(Question);
        var (status, _, body) = Raw(_api, request);
        Assert.Equal((200, "answered"), (status, JsonSerializer.Deserialize<JsonElement>(body).GetProperty("status").GetString()));
    }

    /// <summary>Le corps envoyé en morceaux de 1 Mi caractères (Transfer-Encoding: chunked), tailles en octets.</summary>
    private static string InChunks(string body)
    {
        var chunks = new StringBuilder();
        for (var start = 0; start < body.Length; start += 1 << 20)
        {
            var piece = body.Substring(start, Math.Min(1 << 20, body.Length - start));
            chunks.Append($"{Encoding.UTF8.GetByteCount(piece):x}\r\n{piece}\r\n");
        }
        return chunks.Append("0\r\n\r\n").ToString();
    }

    [Fact]
    public void Gzip_then_chunked_is_refused_on_both_platforms()
    {
        // Un codage que l'application ne décode pas. Sous Windows, http.sys transmet la requête comme sans corps :
        // l'application la refuse (400 invalid_request). Sous Linux, le HttpListener géré la refuse lui-même, comme tout
        // codage autre que chunked seul (501, en HTML).
        const string payload = """{"user": "alice", "question": "Combien de jours ?"}""";
        var (status, head, body) = Raw(_api, "POST /v1/ask HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\nTransfer-Encoding: gzip, chunked\r\n\r\n"
                                             + $"{Encoding.UTF8.GetByteCount(payload):x}\r\n{payload}\r\n0\r\n\r\n");
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal((400, "invalid_request"), (status, Code(JsonSerializer.Deserialize<JsonElement>(body))));
        }
        else
        {
            Assert.Equal((501, true), (status, head.Contains("\r\nContent-Type: text/html")));
        }
    }

    /// <summary>
    /// Une réponse, et elle seule : ligne de statut, en-têtes, et corps selon Content-Length ; la connexion peut rester
    /// ouverte.
    /// </summary>
    private static (string StatusLine, string Head, string Body) ReadResponse(Stream stream)
    {
        var head = new List<byte>();
        while (head.Count < 4 || head[^4] != '\r' || head[^3] != '\n' || head[^2] != '\r' || head[^1] != '\n')
        {
            var next = stream.ReadByte();
            Assert.True(next >= 0, "connexion fermée sans réponse");
            head.Add((byte)next);
        }
        var text = Encoding.Latin1.GetString(head.ToArray())[..^4];
        var lines = text.Split("\r\n");
        var body = new byte[int.Parse(lines.Single(line => line.StartsWith("Content-Length: "))["Content-Length: ".Length..])];
        stream.ReadExactly(body);
        return (lines[0], text, Encoding.UTF8.GetString(body));
    }

    private const string Health = "GET /health HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n";

    [Fact]
    public void Transfer_encoding_with_a_content_length_closes_the_connection()
    {
        // Avec un Content-Length à côté, la fin d'un envoi en morceaux est douteuse (contrebande de requêtes ; en HTTP/1.0,
        // HttpListener lit même le corps selon ce Content-Length) : la réponse annonce la fermeture, puis la connexion se
        // ferme (RFC 9112, § 6.1) ; HttpListener la garderait. Seul, en HTTP/1.1, il la laisse servir la requête
        // suivante (en HTTP/1.0, HttpListener le refuse lui-même : 411).
        Post("/v1/index", "");
        const string start = "POST /v1/ask HTTP/1.1\r\nHost: 127.0.0.1\r\n";
        const string empty = "2\r\n{}\r\n0\r\n\r\n";   // 12 octets : un corps de 12 octets aussi, lu par sa longueur
        var cases = new (string Request, bool Closes)[]
        {
            (start + "Transfer-Encoding: chunked\r\nContent-Length: 7\r\n\r\n" + InChunks(Question), true),
            (start + "Content-Length: 7\r\nTransfer-Encoding: chunked\r\n\r\n" + InChunks(Question), true),
            ("POST /v1/ask HTTP/1.0\r\nHost: 127.0.0.1\r\nConnection: keep-alive\r\nTransfer-Encoding: chunked\r\nContent-Length: 12\r\n\r\n" + empty, true),
            (start + "Transfer-Encoding: chunked\r\n\r\n" + InChunks(Question), false),
        };
        foreach (var (request, closes) in cases)
        {
            using var tcp = new TcpClient("127.0.0.1", new Uri(_api.Url).Port) { ReceiveTimeout = 5000 };
            var stream = tcp.GetStream();
            stream.Write(Encoding.UTF8.GetBytes(request));
            var (_, head, _) = ReadResponse(stream);
            Assert.Equal(closes, head.Contains("\r\nConnection: close"));
            if (closes)
            {
                Assert.Equal(-1, stream.ReadByte());   // puis fermée
            }
            else
            {
                stream.Write(Encoding.UTF8.GetBytes(Health));   // gardée : elle sert la requête suivante
                Assert.Equal("HTTP/1.1 200 OK", ReadResponse(stream).StatusLine);
            }
        }
    }

    [Fact]
    public void A_body_cut_short_by_the_client_is_400()
    {
        // Le client ferme son envoi avant la fin que son Content-Length annonce, aussitôt ou un peu plus tard : 400, sans
        // traiter le début du corps (« {} » : 403, utilisateur vide), et au journal une ligne au plus par requête, sans
        // pile. Sous Windows, http.sys répond lui-même (en HTML) ; sous Linux, c'est l'application, avec un message qui
        // donne les octets reçus et annoncés.
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var api = StartOnAFreePort(MakeContainer(new FakeIndex()), log.Enqueue);
        var cases = new (string Content, int Delay)[] { ("{}", 0), ("{}", 200), ("", 0) };
        try
        {
            foreach (var (content, delay) in cases)
            {
                using var tcp = new TcpClient("127.0.0.1", new Uri(api.Url).Port) { ReceiveTimeout = 5000 };
                var stream = tcp.GetStream();
                stream.Write(Encoding.UTF8.GetBytes("POST /v1/ask HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 100\r\n\r\n" + content));
                Thread.Sleep(delay);
                tcp.Client.Shutdown(SocketShutdown.Send);
                string response;
                try
                {
                    response = new StreamReader(stream, Encoding.UTF8).ReadToEnd();
                }
                catch (IOException reset) when (OperatingSystem.IsWindows()
                                                 && reset.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
                {
                    // Sous Windows, sur une machine chargée, la connexion est parfois réinitialisée (RST) avant que le client
                    // ait lu la réponse, et Windows jette alors ce qu'il avait reçu. Le journal, plus bas, vérifie que la
                    // requête n'a pas été traitée.
                    continue;
                }
                Assert.StartsWith("HTTP/1.1 400 ", response);
                if (!OperatingSystem.IsWindows())
                {
                    var error = JsonSerializer.Deserialize<JsonElement>(response[(response.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]).GetProperty("error");
                    var message = $"corps de requête incomplet : {content.Length} octets reçus sur 100 annoncés (Content-Length)";
                    Assert.Equal(("invalid_request", message), (error.GetProperty("code").GetString(), error.GetProperty("message").GetString()));
                }
            }
            // Une dernière requête : le serveur les traite une à une, dans l'ordre, et sa réponse dit qu'il a fini les
            // précédentes. Sous Windows, http.sys répond au client avant que l'application ait fini de traiter la requête :
            // sans elle, quand l'application tardait (machine chargée), Dispose pouvait arrêter le serveur au milieu
            // (« erreur inattendue » et sa pile au journal).
            Assert.Equal(200, Raw(api, Request("GET", "/health")).Status);
        }
        finally
        {
            api.Dispose();
        }
        var lines = log.ToArray();
        Assert.EndsWith("\"GET /health\" 200", lines[^1]);
        Assert.InRange(lines.Length - 1, 0, cases.Length);
        Assert.All(lines[..^1], line => Assert.EndsWith("\"POST /v1/ask\" 400", line));
    }

    [Fact]
    public void A_malformed_chunked_body_is_400_without_a_stack_in_the_log()
    {
        // HttpListener refuse lui-même une taille de morceau illisible (400, en HTML). Sous Linux, le HttpListener géré
        // transmet pourtant la requête, puis fait échouer la lecture du corps, sa réponse déjà partie : au journal, une
        // ligne au plus, sans pile.
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var api = StartOnAFreePort(MakeContainer(new FakeIndex()), log.Enqueue);
        int status;
        try
        {
            (status, _, _) = Raw(api, "POST /v1/ask HTTP/1.1\r\nHost: 127.0.0.1\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\n{}\r\n0\r\n\r\n");
        }
        finally
        {
            api.Dispose();   // attend la fin du fil du serveur : le journal est complet
        }
        Assert.Equal(400, status);
        Assert.InRange(log.Count, 0, 1);
        Assert.All(log, line => Assert.EndsWith("\"POST /v1/ask\" 400", line));
    }

    [Fact]
    public void An_index_rebuilt_during_every_search_is_409()
    {
        var container = MakeContainer(new RebuiltAtEverySearch());
        container.IndexCorpus.Execute();
        using var api = StartOnAFreePort(container);
        var (status, body) = Post("/v1/ask", Question, api);
        Assert.Equal((HttpStatusCode.Conflict, "index_unusable"), (status, Code(body)));
        Assert.Contains("reconstruit pendant la recherche", body.GetProperty("error").GetProperty("message").GetString());
    }

    /// <summary>Un autre processus reconstruit l'index pendant chaque recherche : la question ne peut aboutir.</summary>
    private sealed class RebuiltAtEverySearch : IVectorIndex
    {
        private readonly FakeIndex _index = new();

        public IndexManifest? Manifest() => _index.Manifest();

        public void Replace(IndexManifest manifest, IReadOnlyList<Chunk> chunks, IReadOnlyList<double[]> vectors) =>
            _index.Replace(manifest, chunks, vectors);

        public IReadOnlyList<Passage> Search(double[] vector, int topK, Func<Chunk, bool> predicate, string? indexId = null)
        {
            var manifest = _index.Manifest()!;
            _index.Replace(manifest with { IndexId = manifest.IndexId + "+" }, _index.Chunks.ToList(), _index.Vectors.ToList());
            return _index.Search(vector, topK, predicate, indexId);
        }
    }

    [Fact]
    public void A_lone_surrogate_in_the_answer_is_replaced()
    {
        // System.Text.Json écrit U+FFFD à la place, et la réponse part.
        using var api = StartOnAFreePort(MakeContainer(new FakeIndex(), new ScriptedGenerator("Deux jours\ud800 par semaine [1].")));
        Post("/v1/index", "", api);
        var (status, body) = Post("/v1/ask", Question, api);
        Assert.Equal((HttpStatusCode.OK, "Deux jours\ufffd par semaine [1]."), (status, body.GetProperty("text").GetString()));
    }

    [Fact]
    public async Task An_unreadable_index_is_a_server_error_not_a_client_error()
    {
        // Un fichier d'index abîmé n'est pas la faute de l'appelant : 500, pas « 400 : votre JSON est invalide ».
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "index.json");
            File.WriteAllText(path, "{ pas du json");
            using var api = StartOnAFreePort(MakeContainer(new Assistant.Infrastructure.JsonVectorIndex(path)));
            var response = await _client.PostAsync(api.Url + "/v1/ask", new StringContent("""{"user": "alice", "question": "télétravail"}""", Encoding.UTF8, "application/json"));
            var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            Assert.Equal((HttpStatusCode.InternalServerError, "unreadable_state"), (response.StatusCode, Code(body)));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void An_index_that_cannot_be_written_has_its_own_code()
    {
        // Pas « unreadable_state » : rien n'est illisible, l'écriture a échoué (l'index précédent reste).
        using var api = StartOnAFreePort(MakeContainer(new UnwritableIndex()));
        var (status, body) = Post("/v1/index", "", api);
        Assert.Equal((HttpStatusCode.InternalServerError, "index_write_failed"), (status, Code(body)));
        Assert.Equal("écriture impossible de l'index (data/index.json) : disque plein", body.GetProperty("error").GetProperty("message").GetString());
    }

    /// <summary>Un index qui ne peut pas être écrit (disque plein…) : IndexWriteException, comme le prévoit le port.</summary>
    private sealed class UnwritableIndex : IVectorIndex
    {
        public IndexManifest? Manifest() => null;

        public void Replace(IndexManifest manifest, IReadOnlyList<Chunk> chunks, IReadOnlyList<double[]> vectors) =>
            throw new IndexWriteException("data/index.json", "disque plein");

        public IReadOnlyList<Passage> Search(double[] vector, int topK, Func<Chunk, bool> predicate, string? indexId = null) => [];
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

/// <summary>Un test propre à http.sys, le HttpListener de Windows : ignoré ailleurs.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "propre à http.sys (Windows)";
        }
    }
}
