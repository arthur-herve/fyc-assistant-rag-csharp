// Ce que l'application vérifie auprès du service IA avant tout travail : il répond.

using System.Net;
using System.Text;
using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

public class AiServiceTests
{
    /// <summary>
    /// Répond à la première requête reçue avec ce statut et <paramref name="body"/>, un texte envoyé tel quel. Sert de
    /// faux /health.
    /// </summary>
    private static Task AnswerOnce(HttpListener listener, string body, int status = 200) => Task.Run(async () =>
    {
        var context = await listener.GetContextAsync();
        context.Response.StatusCode = status;
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    });

    [Fact]
    public async Task An_unreachable_or_failing_ai_service_is_said_with_its_address()
    {
        // Deux messages possibles. Si le service est injoignable, on demande de le lancer. S'il répond par une erreur,
        // on donne l'adresse interrogée, sans « //health » quand l'URL finit par une barre. Une adresse invalide ou un
        // autre protocole que HTTP donne le message « injoignable », plutôt qu'un « Invalid URI » en anglais ou une
        // trace. Une adresse file:// n'est pas non plus le service IA, même si le dossier contient un fichier
        // « health ».
        var folder = Directory.CreateTempSubdirectory("fyc-health-");
        try
        {
            File.WriteAllText(Path.Combine(folder.FullName, "health"), "ok");
            foreach (var baseUrl in new[] { "http://127.0.0.1:9", "http://127.0.0.1:99999", "http://[::1", "ftp://127.0.0.1:9", "file:///c:/x",
                                            new Uri(folder.FullName).AbsoluteUri })
            {
                var unreachable = Assert.Throws<Application.AiServiceException>(() => AiService.Require(baseUrl));
                Assert.Equal($"service IA injoignable ({baseUrl}). Lancez-le d'abord : python -m ai_service", unreachable.Message);
            }
        }
        finally
        {
            folder.Delete(true);
        }
        using var listener = TestPorts.Listener();
        var url = listener.Prefixes.Single();   // avec sa barre finale
        var served = AnswerOnce(listener, """{"error": "en panne"}""", status: 500);
        var failing = Assert.Throws<Application.AiServiceException>(() => AiService.Require(url));
        Assert.Equal($"le service IA répond HTTP 500 sur {url.TrimEnd('/')}/health", failing.Message);
        await served;
    }

    [Fact]
    public async Task A_successful_health_answer_is_enough_whatever_its_body()
    {
        // Quand le service répond, son corps n'est pas interprété : « ok » n'est pas du JSON et suffit.
        using var listener = TestPorts.Listener();
        var served = AnswerOnce(listener, "ok");
        AiService.Require(listener.Prefixes.Single());
        await served;
    }

    [Fact]
    public async Task A_health_answer_cut_short_is_unreachable()
    {
        // Le service annonce la taille de son corps, en envoie le début, puis coupe la connexion. Avec un 200 comme
        // avec une erreur, c'est « injoignable », car GetAsync lit tout le corps avant de rendre la réponse. Le service
        // qui se tait n'est pas testé ici : son délai de 5 s ne se raccourcit pas.
        foreach (var status in new[] { 200, 500 })
        {
            using var listener = TestPorts.Listener();
            var baseUrl = listener.Prefixes.Single().TrimEnd('/');
            var served = AnswerCut(listener, status);
            var unreachable = Assert.Throws<Application.AiServiceException>(() => AiService.Require(baseUrl));
            Assert.Equal($"service IA injoignable ({baseUrl}). Lancez-le d'abord : python -m ai_service", unreachable.Message);
            await served;
        }
    }

    /// <summary>
    /// À la première requête reçue, répond ce statut et annonce 100 octets, mais n'en envoie qu'un avant de couper la
    /// connexion.
    /// </summary>
    private static Task AnswerCut(HttpListener listener, int status) => Task.Run(async () =>
    {
        var context = await listener.GetContextAsync();
        context.Response.StatusCode = status;
        context.Response.ContentLength64 = 100;
        await context.Response.OutputStream.WriteAsync("{"u8.ToArray());
        await context.Response.OutputStream.FlushAsync();
        context.Response.Abort();
    });
}
