// Adaptateurs : le modèle est derrière une frontière réseau.
// Le service IA (ai_service/, en Python) est un déployable séparé ; ces adaptateurs
// traduisent les ports IEmbedder et IGenerator en appels HTTP selon docs/contrat-http.md.
// Le service peut être écrit dans n'importe quel langage : seul le contrat compte.

using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Application;

namespace Assistant.Infrastructure;

/// <summary>Une fonction (url, charge utile) → réponse JSON. Remplaçable dans les tests.</summary>
public delegate JsonElement JsonTransport(string url, JsonObject payload);

public static class HttpTransport
{
    // Le JSON de System.Text.Json, accents tels quels (en UTF-8).
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonTransport Create(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        return (url, payload) =>
        {
            HttpResponseMessage response;
            byte[] body;
            try
            {
                // Corps sérialisé d'un bloc, avec Content-Length : le service (bibliothèque standard
                // Python) ne lit pas les envois en morceaux (chunked).
                var json = payload.ToJsonString(Options);
                using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                response = client.PostAsync(url, content).GetAwaiter().GetResult();
                body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
            {
                throw new AiServiceException($"Service IA injoignable ({url}) : {error.Message}");
            }
            catch (Exception error) when (error is NotSupportedException or UriFormatException or InvalidOperationException)
            {
                throw new AiServiceException($"Adresse du service IA invalide ({url}) : {error.Message}", transient: false);
            }
            using var _ = response;
            if (!response.IsSuccessStatusCode)
            {
                // Octets invalides remplacés ; marque d'ordre des octets retirée, comme pour une réponse 200.
                var text = System.Text.Encoding.UTF8.GetString(TextFiles.WithoutBom(body));
                var detail = text;
                var retryable = true;
                try
                {
                    // Une erreur au format du contrat n'est lue que si son message est un texte :
                    // tout autre corps est cité tel quel, JSON qui n'est pas strict compris (clé en double…).
                    using var error = JsonText.ParseDocument(text);
                    var problem = error.RootElement.GetProperty("error");
                    if (problem.GetProperty("message") is { ValueKind: JsonValueKind.String } message)
                    {
                        detail = message.GetString()!;
                        retryable = !(problem.TryGetProperty("retryable", out var flag) && flag.ValueKind == JsonValueKind.False);
                    }
                }
                catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
                {
                    // corps non JSON, ou message qui n'est pas un texte Unicode (« \ud800 ») : on garde le texte brut
                }
                // 5xx : passager, sauf si le service dit que réessayer ne changera rien (modèle absent…).
                throw new AiServiceException($"Service IA : HTTP {(int)response.StatusCode} — {detail}",
                                             transient: (int)response.StatusCode >= 500 && retryable);
            }
            try
            {
                // UTF-8 strict, marque d'ordre des octets acceptée, JSON strict.
                using var document = JsonText.ParseDocument(TextFiles.DecodeUtf8(body));
                return document.RootElement.Clone();
            }
            catch (Exception error) when (error is JsonException or FormatException)
            {
                // JSON invalide ou qui n'est pas strict (clé en double, plus de 64 niveaux), octets non UTF-8 :
                // ce n'est pas le contrat.
                throw new AiServiceException($"Réponse du service IA illisible ({url}) : {error.Message}", transient: false);
            }
        };
    }

    // Une réponse d'une autre forme que celle du contrat (null, nombre, champ mal typé…) est une
    // AiServiceException non passagère : réessayer ne changerait rien.

    public static void Require(JsonElement payload, params string[] keys)
    {
        var missing = keys.Where(k => payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(k, out _)).ToList();
        if (missing.Count > 0)
        {
            throw new AiServiceException($"Réponse du service IA incomplète, champs manquants : [{string.Join(", ", missing)}]",
                                         transient: false);
        }
    }

    internal static AiServiceException Incoherent(string problem) =>
        new($"Réponse du service IA incohérente : {problem}", transient: false);

    /// <summary>Le champ texte <paramref name="key"/> d'une réponse dont <see cref="Require"/> a vérifié les champs.</summary>
    internal static string Text(JsonElement payload, string key)
    {
        var value = payload.GetProperty(key);
        try
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString()!;
            }
        }
        catch (InvalidOperationException)
        {
            // Demi-paire de substitution seule (« \ud800 ») : pas un texte Unicode, GetString la refuse.
        }
        throw Incoherent($"« {key} » doit être un texte");
    }
}

public sealed class HttpEmbedder : IEmbedder
{
    private readonly string _url;
    private readonly string _model;
    private readonly JsonTransport _post;

    public HttpEmbedder(string baseUrl, string model, TimeSpan? timeout = null, JsonTransport? transport = null)
    {
        _url = baseUrl.TrimEnd('/') + "/v1/embeddings";
        _model = model;
        _post = transport ?? HttpTransport.Create(timeout ?? TimeSpan.FromSeconds(120));
    }

    public EmbeddingBatch EmbedDocuments(IReadOnlyList<string> texts) => Embed(texts, "document");

    public EmbeddingBatch EmbedQuery(string text) => Embed(new[] { text }, "query");

    private EmbeddingBatch Embed(IReadOnlyList<string> texts, string inputType)
    {
        var payload = _post(_url, new JsonObject
        {
            ["model"] = _model, ["input_type"] = inputType, ["inputs"] = new JsonArray(texts.Select(t => (JsonNode?)t).ToArray()),
        });
        HttpTransport.Require(payload, "model", "dimension", "vectors");
        var model = HttpTransport.Text(payload, "model");
        var dimension = payload.GetProperty("dimension") is { ValueKind: JsonValueKind.Number } announced && announced.TryGetInt32(out var size)
            ? size
            : throw HttpTransport.Incoherent("« dimension » doit être un entier");
        var vectors = Vectors(payload.GetProperty("vectors"))
            ?? throw HttpTransport.Incoherent("« vectors » doit être une liste de listes de nombres");
        if (vectors.Count != texts.Count || vectors.Any(v => v.Length != dimension))
        {
            throw new AiServiceException(
                $"Réponse du service IA incohérente : {texts.Count} vecteurs de {dimension} dimensions attendus, reçu {vectors.Count} "
                + $"de [{string.Join(", ", vectors.Select(v => v.Length).Distinct().Order())}] dimensions", transient: false);
        }
        return new EmbeddingBatch(model, dimension, vectors);
    }

    /// <summary>Des listes de nombres finis (« 1e400 » est l'infini), ou null.</summary>
    private static List<double[]>? Vectors(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var vectors = new List<double[]>();
        foreach (var vector in element.EnumerateArray())
        {
            if (vector.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var values = new List<double>();
            foreach (var x in vector.EnumerateArray())
            {
                if (x.ValueKind != JsonValueKind.Number || !x.TryGetDouble(out var value) || !double.IsFinite(value))
                {
                    return null;
                }
                values.Add(value);
            }
            vectors.Add(values.ToArray());
        }
        return vectors;
    }
}

public sealed class HttpGenerator : IGenerator
{
    private readonly string _url;
    private readonly string _model;
    private readonly JsonTransport _post;

    public HttpGenerator(string baseUrl, string model, TimeSpan? timeout = null, JsonTransport? transport = null)
    {
        _url = baseUrl.TrimEnd('/') + "/v1/generate";
        _model = model;
        _post = transport ?? HttpTransport.Create(timeout ?? TimeSpan.FromSeconds(300));
    }

    public Generation Generate(GenerationRequest request)
    {
        // seed vaut null s'il n'est pas fixé.
        var payload = _post(_url, new JsonObject
        {
            ["model"] = _model,
            ["system"] = request.System,
            ["prompt"] = request.Prompt,
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxTokens,
            ["seed"] = request.Seed,
        });
        HttpTransport.Require(payload, "model", "text");
        return new Generation(HttpTransport.Text(payload, "model"), HttpTransport.Text(payload, "text"));
    }
}
