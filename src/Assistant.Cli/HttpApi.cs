// API HTTP de l'application (adaptateur entrant, comme Program) : mêmes routes et mêmes
// codes que la version Python (assistant/interface/http_api.py).
//
//     GET  /health
//     GET  /v1/status                     -> l'index est-il cohérent avec le corpus et le modèle servi ?
//     POST /v1/index                      -> reconstruit l'index
//     POST /v1/ask  {"user": "alice", "question": "..."}
//
// Elle ne construit aucun adaptateur : elle reçoit le Container de Composition.Build.
// Bibliothèque de classes seule (HttpListener), aucun paquet.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Application;
using Assistant.Domain;

namespace Assistant.Cli;

public sealed class HttpApi : IDisposable
{
    private readonly Container _container;
    private readonly bool _quiet;
    private readonly HttpListener _listener = new();
    private Thread? _thread;

    public HttpApi(Container container, string host, int port, bool quiet = false)
    {
        _container = container;
        _quiet = quiet;
        // HttpListener veut un nom d'hôte ou « + » ; 0.0.0.0 signifie « toutes les interfaces ».
        var prefixHost = host is "0.0.0.0" or "*" ? "+" : host;
        _listener.Prefixes.Add($"http://{prefixHost}:{port}/");
        Url = $"http://{(host is "0.0.0.0" or "*" ? "127.0.0.1" : host)}:{port}";
    }

    public string Url { get; }

    public void Start()
    {
        _listener.Start();
        _thread = new Thread(Loop) { IsBackground = true, Name = "assistant-http" };
        _thread.Start();
    }

    public void Stop()
    {
        if (_listener.IsListening)
        {
            _listener.Stop();
        }
        _thread?.Join(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        Stop();
        _listener.Close();
    }

    private void Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = _listener.GetContext();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            // Une requête à la fois (les cas d'usage et le cache d'embeddings ne sont pas partagés entre
            // fils) : une génération longue retarde /health. La version Python traite les requêtes en
            // parallèle et ne verrouille que l'indexation ; écart assumé, suffisant pour le cours.
            try
            {
                Handle(context);
            }
            catch (Exception error)
            {
                // Jamais de plantage du serveur pour une requête : on journalise et on continue.
                Log($"erreur inattendue : {error}");
                try { Send(context.Response, 500, Error("internal_error", error.Message)); } catch (Exception) { /* réponse déjà partie */ }
            }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var request = context.Request;
        var path = request.Url?.AbsolutePath ?? "/";
        var client = request.RemoteEndPoint?.ToString() ?? "?";   // avant l'envoi : la requête est libérée après
        int status;
        JsonObject body;
        try
        {
            (status, body) = request.HttpMethod switch
            {
                "GET" => Get(path),
                "POST" => Post(path, ReadBody(request)),
                _ => (405, Error("method_not_allowed", request.HttpMethod)),
            };
        }
        catch (JsonException error)
        {
            (status, body) = (400, Error("invalid_json", error.Message));
        }
        catch (UnknownUserException error)
        {
            (status, body) = (403, Error("unknown_user", error.Message));
        }
        catch (DomainException error)
        {
            (status, body) = (400, Error("invalid_question", error.Message));
        }
        catch (Exception error) when (error is IndexNotBuiltException or IndexModelMismatchException)
        {
            (status, body) = (409, Error("index_unusable", error.Message));
        }
        catch (AiServiceException error)
        {
            (status, body) = (502, Error("ai_service_error", error.Message));
        }
        catch (Exception error) when (error is FormatException or AssistantApplicationException or IOException or UnauthorizedAccessException)
        {
            // corpus mal formé (CorpusFormatException est une FormatException), prompt ou index illisible, corpus vide…
            (status, body) = (500, Error("unreadable_state", error.Message));
        }
        var method = request.HttpMethod;
        Send(context.Response, status, body);
        Log($"{client} \"{method} {path}\" {status}");
    }

    private (int, JsonObject) Get(string path)
    {
        switch (path)
        {
            case "/health":
            {
                var manifest = _container.Index.Manifest();
                return (200, new JsonObject { ["status"] = "ok", ["index"] = manifest is null ? null : Presenter.ManifestToJson(manifest) });
            }
            case "/v1/status":
            {
                var report = _container.CheckStatus.Execute();
                return (report.UpToDate ? 200 : report.Unverified ? 503 : 409, Presenter.StatusToJson(report));
            }
            default:
                return (404, Error("not_found", path));
        }
    }

    private (int, JsonObject) Post(string path, JsonObject payload)
    {
        switch (path)
        {
            case "/v1/index":
            {
                return (200, Presenter.ManifestToJson(_container.IndexCorpus.Execute()));
            }
            case "/v1/ask":
            {
                var user = _container.Config.User(payload["user"]?.ToString() ?? "");
                var answer = _container.AskQuestion.Execute(user, payload["question"]?.ToString() ?? "");
                return (200, Presenter.AnswerToJson(answer));
            }
            default:
                return (404, Error("not_found", path));
        }
    }

    private static JsonObject ReadBody(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        var text = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }
        return JsonNode.Parse(text) as JsonObject ?? throw new JsonException("le corps doit être un objet JSON");
    }

    private static JsonObject Error(string code, string message) =>
        new() { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static void Send(HttpListenerResponse response, int status, JsonObject body)
    {
        var data = Encoding.UTF8.GetBytes(Presenter.ToJson(body));
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = data.Length;
        try
        {
            response.OutputStream.Write(data, 0, data.Length);
        }
        catch (Exception e) when (e is HttpListenerException or IOException)
        {
            // client parti : rien à faire
        }
        finally
        {
            response.Close();
        }
    }

    private void Log(string message)
    {
        if (!_quiet)
        {
            Console.Error.WriteLine($"[application] {message}");
        }
    }
}
