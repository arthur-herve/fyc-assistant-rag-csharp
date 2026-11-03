// Les adaptateurs. FakeGenerator fonctionne sans réseau. HttpGenerator appelle en HTTP le service
// IA du fil rouge, lancé par python -m ai_service sur le port 8100, selon le contrat de
// docs/contrat-http.md. On passe de l'un à l'autre sans toucher au domaine : c'est la promesse du
// transfert naïf.

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

internal static class Http
{
    /// <summary>
    /// Le corps part avec un Content-Length, car le service IA, écrit avec la seule bibliothèque
    /// standard de Python, refuse les envois en morceaux.
    /// </summary>
    public static StringContent Json(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    /// <summary>
    /// Le message d'erreur du contrat, lu dans error.message, ou le code HTTP si le corps n'a pas
    /// cette forme.
    /// </summary>
    public static string ErrorMessage(JsonElement body, HttpResponseMessage response) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message)
            ? message.GetString() ?? response.StatusCode.ToString()
            : $"HTTP {(int)response.StatusCode}";
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
            throw new InvalidOperationException($"service IA : {Http.ErrorMessage(body, response)}");
        }
        return body.GetProperty("text").GetString() ?? "";
    }
}
