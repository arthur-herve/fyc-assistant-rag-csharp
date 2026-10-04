// API HTTP de l'application (adaptateur entrant, comme Program).
//
//     GET  /health
//     GET  /v1/status                     -> l'index est-il cohérent avec le corpus et le modèle servi ?
//     POST /v1/index                      -> reconstruit l'index
//     POST /v1/ask  {"user": "alice", "question": "..."}
//
// Une réponse JSON à toute requête que HttpListener lui transmet (liste des codes dans le README, sous
// « L'application peut aussi être servie en HTTP ») : route inconnue 404 ; autre méthode 405, avec l'en-tête Allow
// (HEAD est permis partout où GET l'est, sans corps) ; corps qui n'est pas un objet JSON strict 400 (invalid_json) ;
// requête mal formée 400 (invalid_request : champ qui n'est pas une chaîne, codage de transfert autre que chunked,
// corps plus court que son Content-Length…) ; question vide 400 ; utilisateur inconnu 403 ; index absent, d'un autre
// modèle ou remplacé pendant la question 409 ; corps de plus de 16 Mio 413 ; état illisible, index impossible à écrire
// ou erreur imprévue 500 ; service IA en échec 502.
// Ce que HttpListener refuse lui-même reçoit sa propre page HTML (voir le README).
// Elle ne construit aucun adaptateur : elle reçoit le Container de Composition.Build.
// L'utilisateur est celui que déclare l'appelant : pas d'authentification (voir docs/installation.md).
// Bibliothèque de classes seule (HttpListener), aucun paquet.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Application;
using Assistant.Domain;

namespace Assistant.Cli;

/// <summary>Requête illisible ou mal formée, ou corps trop volumineux : la faute est chez l'appelant (400, ou 413).</summary>
public sealed class InvalidRequestException : Exception
{
    public InvalidRequestException(string code, string message, int status = 400) : base(message) => (Code, Status) = (code, status);

    public string Code { get; }

    public int Status { get; }
}

public sealed class HttpApi : IDisposable
{
    private const int MaxBody = 16 * 1024 * 1024;   // corps lu au plus (16 Mio) : au-delà, 413
    private static readonly Dictionary<string, string> Routes = new()
    {
        ["/health"] = "GET", ["/v1/status"] = "GET", ["/v1/index"] = "POST", ["/v1/ask"] = "POST",
    };
    private static readonly Dictionary<string, string[]> Allowed = new()   // HEAD permis partout où GET l'est (RFC 9110)
    {
        ["GET"] = ["GET", "HEAD"], ["POST"] = ["POST"],
    };

    private readonly Container _container;
    private readonly bool _quiet;
    private readonly Action<string> _log;
    private readonly HttpListener _listener = new();
    private Thread? _thread;

    /// <param name="log">Journal des requêtes et des erreurs ; par défaut la sortie d'erreur.</param>
    public HttpApi(Container container, string host, int port, bool quiet = false, Action<string>? log = null)
    {
        _container = container;
        _quiet = quiet;
        _log = log ?? (message => Console.Error.WriteLine($"[application] {message}"));
        // HttpListener veut un nom d'hôte ou « + » ; 0.0.0.0 (comme « * » et « + ») signifie « toutes les interfaces ».
        // Ce n'est pas une adresse où se connecter : l'adresse annoncée est alors 127.0.0.1, la machine elle-même.
        AllInterfaces = host is "0.0.0.0" or "*" or "+";
        _listener.Prefixes.Add($"http://{(AllInterfaces ? "+" : host)}:{port}/");
        Url = $"http://{(AllInterfaces ? "127.0.0.1" : host)}:{port}";
    }

    /// <summary>L'adresse où se connecter : l'hôte demandé, ou 127.0.0.1 pour toutes les interfaces.</summary>
    public string Url { get; }

    /// <summary>Vrai quand on écoute sur toutes les interfaces (0.0.0.0, « * » ou « + »).</summary>
    public bool AllInterfaces { get; }

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
            // fils) : une génération longue retarde /health. Limite assumée, suffisante pour le cours.
            try
            {
                Handle(context);
            }
            catch (Exception error)
            {
                // Jamais de plantage du serveur pour une requête : on journalise et on continue.
                Log($"erreur inattendue : {error}");
                try { Send(context.Response, context.Request.HttpMethod, 500, Error("internal_error", error.Message)); } catch (Exception) { /* réponse déjà partie */ }
            }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var request = context.Request;
        var path = request.Url?.AbsolutePath ?? "/";
        var client = request.RemoteEndPoint?.ToString() ?? "?";   // avant l'envoi : la requête est libérée après
        var method = request.HttpMethod;
        string? allow = null;   // l'en-tête Allow d'un 405
        int status;
        JsonObject body;
        if (request.Headers["Transfer-Encoding"] is not null && request.Headers["Content-Length"] is not null)
        {
            // Avec un Content-Length à côté (qu'un intermédiaire a pu suivre, et que HttpListener suit en HTTP/1.0), la
            // fin du corps est douteuse : la suite pourrait être prise pour une autre requête (contrebande de requêtes).
            // La connexion se ferme donc après la réponse, comme l'exige la RFC 9112 (§ 6.1) ; HttpListener, lui, la
            // garderait. Sans Content-Length, en HTTP/1.0, il refuse lui-même (411).
            context.Response.KeepAlive = false;
        }
        try
        {
            // HttpListener décode « chunked » et refuse lui-même un codage inconnu (501). Sous Windows, http.sys
            // transmet pourtant « gzip, chunked » comme une requête sans corps : refusé ici, plutôt que lu comme un
            // corps vide ; sous Linux, le HttpListener géré le refuse lui-même, comme tout codage autre que chunked
            // seul (501, en HTML).
            if (request.Headers["Transfer-Encoding"] is { } coding && !coding.Trim().Equals("chunked", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidRequestException("invalid_request", $"Transfer-Encoding non pris en charge : {coding} (seul chunked l'est)");
            }
            if (!Routes.TryGetValue(path, out var allowed))
            {
                (status, body) = (404, Error("not_found", path));
            }
            else if (!Allowed[allowed].Contains(method))
            {
                (status, body, allow) = (405, Error("method_not_allowed", method), string.Join(", ", Allowed[allowed]));
            }
            else
            {
                // GET ou HEAD : mêmes en-têtes, sans corps (voir Send).
                (status, body) = allowed == "GET" ? Get(path) : Post(path, ReadBody(request));
            }
        }
        catch (InvalidRequestException error)
        {
            // Seul le corps de la requête est la faute de l'appelant : un index illisible est une erreur 500.
            (status, body) = (error.Status, Error(error.Code, error.Message));
        }
        catch (UnknownUserException error)
        {
            (status, body) = (403, Error("unknown_user", error.Message));
        }
        catch (DomainException error)
        {
            (status, body) = (400, Error("invalid_question", error.Message));
        }
        catch (Exception error) when (error is IndexNotBuiltException or IndexModelMismatchException or IndexReplacedException)
        {
            (status, body) = (409, Error("index_unusable", error.Message));
        }
        catch (AiServiceException error)
        {
            (status, body) = (502, Error("ai_service_error", error.Message));
        }
        catch (IndexWriteException error)
        {
            (status, body) = (500, Error("index_write_failed", error.Message));   // l'index en service reste le précédent
        }
        catch (Exception error) when (error is FormatException or AssistantApplicationException or IOException or UnauthorizedAccessException)
        {
            // corpus mal formé (CorpusFormatException est une FormatException), prompt ou index illisible, corpus vide…
            (status, body) = (500, Error("unreadable_state", error.Message));
        }
        Send(context.Response, method, status, body, allow);
        Log($"{client} \"{method} {path}\" {status}");
    }

    private (int, JsonObject) Get(string path)
    {
        if (path == "/health")
        {
            var manifest = _container.Index.Manifest();
            return (200, new JsonObject { ["status"] = "ok", ["index"] = manifest is null ? null : Presenter.ManifestToJson(manifest) });
        }
        var report = _container.CheckStatus.Execute();   // /v1/status, l'autre route GET
        return (report.UpToDate ? 200 : report.Unverified ? 503 : 409, Presenter.StatusToJson(report));
    }

    private (int, JsonObject) Post(string path, JsonObject payload)
    {
        if (path == "/v1/index")
        {
            return (200, Presenter.ManifestToJson(_container.IndexCorpus.Execute()));
        }
        var user = _container.Config.User(TextField(payload, "user"));   // /v1/ask, l'autre route POST
        var answer = _container.AskQuestion.Execute(user, TextField(payload, "question"));
        return (200, Presenter.AnswerToJson(answer));
    }

    private static string TextField(JsonObject payload, string name) =>
        payload[name] switch
        {
            null => "",
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            _ => throw new InvalidRequestException("invalid_request", $"le champ « {name} » doit être une chaîne"),
        };

    private static JsonObject ReadBody(HttpListenerRequest request)
    {
        // Plus de 16 Mio annoncés : refusé sans rien lire. La taille est lue dans l'en-tête, car sous Windows
        // (http.sys), de 2^63 à 2^64 − 1, HttpListener transmet la requête comme sans corps (ContentLength64
        // vaut 0) ; au-delà, il la refuse lui-même (413, en HTML). Un envoi en morceaux, dont le Content-Length
        // ne compte pas (RFC 9112) même si http.sys le transmet, est arrêté dès que la limite est franchie.
        if (request.Headers["Transfer-Encoding"] is null
            && ulong.TryParse(request.Headers["Content-Length"], NumberStyles.None, CultureInfo.InvariantCulture, out var announced)
            && announced > MaxBody)
        {
            throw TooLarge();
        }
        using var bytes = new MemoryStream();
        var buffer = new byte[64 * 1024];
        try
        {
            for (int read; (read = request.InputStream.Read(buffer)) > 0;)
            {
                bytes.Write(buffer, 0, read);
                if (bytes.Length > MaxBody)
                {
                    throw TooLarge();
                }
            }
        }
        catch (Exception error) when (error is HttpListenerException or IOException)
        {
            // La lecture échoue : envoi fermé avant la fin annoncée, envoi en morceaux mal formé, ou connexion
            // réinitialisée. 400 plutôt qu'une erreur inattendue (500, et sa pile au journal) ; avec un Content-Length,
            // le message donne les octets reçus et annoncés. Sous Windows, http.sys a déjà répondu lui-même (400, en
            // HTML), comme, sous Linux, le HttpListener géré à un envoi en morceaux mal formé : il ne reste que la ligne
            // de la requête au journal.
            throw new InvalidRequestException("invalid_request", request.Headers["Transfer-Encoding"] is null
                ? $"corps de requête incomplet : {bytes.Length} octets reçus sur {request.ContentLength64} annoncés (Content-Length)"
                : "envoi en morceaux mal formé ou interrompu (Transfer-Encoding: chunked)");
        }
        string text;
        try
        {
            // UTF-8 strict, comme les fichiers : la marque d'ordre des octets UTF-8 est acceptée ; tout autre encodage
            // (latin-1, ou UTF-16 et UTF-32 même annoncés par leur marque) est refusé, avec l'octet fautif.
            text = TextFiles.DecodeUtf8(bytes.ToArray());
        }
        catch (FormatException error)
        {
            throw new InvalidRequestException("invalid_json", error.Message);
        }
        if (text.Length == 0)   // pas de corps ; des espaces seuls ne sont pas du JSON (400)
        {
            return new JsonObject();
        }
        try
        {
            CheckJson(text);
            return JsonNode.Parse(text) as JsonObject ?? throw new InvalidRequestException("invalid_json", "le corps doit être un objet JSON");
        }
        catch (JsonException error)
        {
            throw new InvalidRequestException("invalid_json", error.Message);
        }
    }

    private static InvalidRequestException TooLarge() =>
        new("payload_too_large", $"corps de requête trop volumineux : {MaxBody} octets au plus (16 Mio)", 413);

    /// <summary>
    /// JSON strict. Sans ce contrôle, une clé en double ne lèverait une ArgumentException qu'au premier
    /// accès à l'objet, et un « \ud800 » isolé (du JSON valide, mais pas du texte) une
    /// InvalidOperationException à la lecture du champ : deux erreurs 500. Mêmes contrôles que pour les
    /// fichiers (JsonText). Une erreur de syntaxe, NaN compris, est une JsonException (400 aussi).
    /// </summary>
    private static void CheckJson(string text)
    {
        try
        {
            JsonText.Check(text, strings: true);
        }
        catch (FormatException error)
        {
            throw new InvalidRequestException("invalid_json", error.Message);
        }
    }

    private static JsonObject Error(string code, string message) =>
        new() { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static void Send(HttpListenerResponse response, string method, int status, JsonObject body, string? allow = null)
    {
        var data = Encoding.UTF8.GetBytes(Presenter.ToJson(body));
        try
        {
            response.StatusCode = status;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = data.Length;
            if (allow is not null)
            {
                response.Headers["Allow"] = allow;   // obligatoire avec un 405 (RFC 9110)
            }
            if (method != "HEAD")   // HEAD : les en-têtes seuls ; HttpListener refuserait le corps
            {
                response.OutputStream.Write(data, 0, data.Length);
            }
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException)
        {
            // Client parti, ou réponse déjà envoyée par HttpListener lui-même (sous Linux, le HttpListener géré répond
            // 400 à un envoi en morceaux mal formé, puis fait échouer la lecture du corps) : rien à faire.
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
            _log(message);
        }
    }
}
