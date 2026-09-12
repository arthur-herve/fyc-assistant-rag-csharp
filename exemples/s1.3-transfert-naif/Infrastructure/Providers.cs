// Les adaptateurs : un faux fournisseur (aucun réseau) et un fournisseur HTTP qui parle
// au service IA du fil rouge (python -m ai_service, port 8100, contrat docs/contrat-http.md).
// Substituer l'un à l'autre ne touche pas au domaine : c'est la promesse du transfert naïf.

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TransfertNaif.Domaine;

namespace TransfertNaif.Infrastructure;

public sealed class FakeGenerator : ITextGenerator
{
    public string Generate(string instructions, string prompt) =>
        "Réponse du faux générateur : je n'ai rien lu, mais je cite [1].";
}

/// <summary>Sac de mots minuscule : un « modèle » d'embeddings sans réseau.</summary>
public sealed class KeywordEmbeddings : IEmbeddingProvider
{
    private static readonly string[] Vocabulary = { "télétravail", "jours", "frais", "repas", "salaire", "congés", "badge", "mot de passe" };

    public double[] Embed(string text)
    {
        var lower = text.ToLowerInvariant();
        return Vocabulary.Select(word => lower.Contains(word, StringComparison.Ordinal) ? 1.0 : 0.0).ToArray();
    }
}

internal static class Http
{
    /// <summary>Corps avec Content-Length : le service IA (bibliothèque standard Python) ne lit pas les envois en morceaux.</summary>
    public static StringContent Json(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
}

public sealed class HttpGenerator : ITextGenerator
{
    private readonly HttpClient _http;
    private readonly string _model;

    public HttpGenerator(string baseUrl, string model)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(5) };
        _model = model;
    }

    public string Generate(string instructions, string prompt)
    {
        var response = _http.PostAsync("/v1/generate", Http.Json(new { model = _model, system = instructions, prompt, temperature = 0.2, max_tokens = 300 })).Result;
        var body = response.Content.ReadFromJsonAsync<JsonElement>().Result;
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"service IA : {body.GetProperty("error").GetProperty("message").GetString()}");
        }
        return body.GetProperty("text").GetString() ?? "";
    }
}

public sealed class HttpEmbeddings : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly string _model;

    public HttpEmbeddings(string baseUrl, string model)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(5) };
        _model = model;
    }

    public double[] Embed(string text)
    {
        var response = _http.PostAsync("/v1/embeddings", Http.Json(new { model = _model, input_type = "document", inputs = new[] { text } })).Result;
        var body = response.Content.ReadFromJsonAsync<JsonElement>().Result;
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"service IA : {body.GetProperty("error").GetProperty("message").GetString()}");
        }
        // Le service renvoie aussi "model" (identifiant concret) et "dimension" : ce jouet les ignore.
        // C'est précisément l'erreur que le fil rouge ne commet pas (IndexManifest, IndexModelMismatchException).
        return body.GetProperty("vectors")[0].EnumerateArray().Select(v => v.GetDouble()).ToArray();
    }
}
