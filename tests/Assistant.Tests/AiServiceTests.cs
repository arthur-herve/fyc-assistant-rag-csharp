// Ce que l'application vérifie auprès du service IA avant tout travail : il répond, et il sert le modèle demandé.

using System.Net;
using System.Text;
using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

public class AiServiceTests
{
    /// <summary>
    /// Répond à la première requête reçue avec ce statut et <paramref name="models"/>, un texte JSON envoyé tel quel.
    /// Sert de faux GET /v1/models ou de faux /health.
    /// </summary>
    private static Task AnswerOnce(HttpListener listener, string models, int status = 200) => Task.Run(async () =>
    {
        var context = await listener.GetContextAsync();
        context.Response.StatusCode = status;
        var bytes = Encoding.UTF8.GetBytes(models);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    });

    /// <summary>Répond ces octets tels quels à la première requête reçue : un faux GET /v1/models hors UTF-8.</summary>
    private static Task AnswerOnce(HttpListener listener, byte[] bytes) => Task.Run(async () =>
    {
        var context = await listener.GetContextAsync();
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    });

    [Fact]
    public async Task The_list_of_served_models_must_be_readable()
    {
        // Que le service soit injoignable ou que sa liste soit hors contrat, le message est le même, et non une
        // exception de lecture JSON. Un alias qui n'est pas un texte rend la liste hors contrat. Une clé en double
        // aussi, au lieu de laisser la dernière valeur l'emporter. La réponse est lue en JSON strict (JsonText), comme
        // un fichier, même dans les champs que personne ne lit : dans ces cas-là, « nomic » est pourtant servi.
        AssertUnreadable("http://127.0.0.1:9/");
        static string Served(string x) => $$"""{"embedding": [{"alias": "nomic", "x": {{x}}}], "generation": []}""";
        foreach (var models in new[]
                 {
                     """{"embedding": [{"alias": 1}], "generation": []}""",
                     """{"embedding": [{"alias": "a"}], "embedding": [{"alias": "nomic"}], "generation": []}""",
                     Served("""{"b": 1, "b": 2}"""),
                     Served(new string('1', 4301)),
                     Served("\"\\ud800\""),
                 })
        {
            using var listener = TestPorts.Listener();
            var served = AnswerOnce(listener, models);
            AssertUnreadable(listener.Prefixes.Single());
            await served;   // la réponse hors contrat a bien été lue
        }
        using (var listener = TestPorts.Listener())
        {
            var served = AnswerOnce(listener, Served("1"));
            AiService.RequireModel(listener.Prefixes.Single(), "embedding", "nomic");   // sans défaut : accepté
            await served;
        }
        // Les octets sont décodés comme toute réponse du service IA (docs/contrat-http.md). Une marque d'ordre des
        // octets est acceptée, mais un octet qui n'est pas de l'UTF-8, ici du latin-1, rend la réponse hors contrat au
        // lieu d'être remplacé en silence.
        using (var listener = TestPorts.Listener())
        {
            var served = AnswerOnce(listener, "\uFEFF" + Served("1"));
            AiService.RequireModel(listener.Prefixes.Single(), "embedding", "nomic");
            await served;
        }
        using (var listener = TestPorts.Listener())
        {
            var served = AnswerOnce(listener, Encoding.Latin1.GetBytes(Served("\"é\"")));
            AssertUnreadable(listener.Prefixes.Single());
            await served;
        }

        static void AssertUnreadable(string baseUrl)
        {
            var error = Assert.Throws<Application.AiServiceException>(() => AiService.RequireModel(baseUrl, "embedding", "nomic"));
            Assert.Equal($"le service IA ne donne pas la liste de ses modèles ({baseUrl.TrimEnd('/')}/v1/models)", error.Message);
        }
    }

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

    [Fact]
    public async Task An_empty_list_of_served_models_says_aucun()
    {
        // « nomic » est servi, mais pour la génération.
        using var listener = TestPorts.Listener();
        var served = AnswerOnce(listener, """{"embedding": [], "generation": [{"alias": "nomic"}]}""");
        var error = Assert.Throws<ArgumentException>(() => AiService.RequireModel(listener.Prefixes.Single(), "embedding", "nomic"));
        Assert.Equal("le service IA ne sert pas « nomic » comme modèle d'embeddings (servis : aucun)", error.Message);
        await served;
    }
}
