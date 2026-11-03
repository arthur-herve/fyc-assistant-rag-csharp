// La commande serve : ses options, l'API qu'elle démarre, l'adresse qu'elle annonce et son arrêt.

using System.Net;
using System.Text.Json.Nodes;
using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

[Collection("Console")]
public sealed class CliServeTests : CliTestBase
{
    [Theory]
    [InlineData("option(s) inconnue(s) pour serve : --json", "serve", "--json")]
    public void A_mistyped_abbreviated_or_foreign_option_is_refused_before_any_work(string message, params string[] argv) =>
        AssertRefused(message, argv);

    [Theory]
    [InlineData("argument(s) en trop pour serve : 8000", "serve", "8000")]
    public void An_extra_argument_is_refused(string message, params string[] argv) => AssertRefused(message, argv);

    [Theory]
    [InlineData("l'option --port attend un port entre 1 et 65535, pas « 99999 »", "serve", "--port", "99999")]
    [InlineData("l'option --port attend un port entre 1 et 65535, pas « -1 »", "serve", "--port", "-1")]
    [InlineData("l'option --port attend un port entre 1 et 65535, pas « 0 »", "serve", "--port", "0")]
    [InlineData("l'option --port attend un port entre 1 et 65535, pas « 65536 »", "serve", "--port=65536")]
    public void A_wrong_value_or_an_unknown_subcommand_is_refused_before_any_work(string message, params string[] argv) =>
        AssertRefused(message, argv);

    // Une valeur qui n'est pas un entier est refusée au démarrage, avant même de lire la configuration.
    [Theory]
    [InlineData("l'option --port attend un entier, pas « abc »\n", "serve", "--port", "abc")]
    public void A_non_integer_value_is_refused_before_the_configuration_even_if_repeated(string message, params string[] argv) =>
        AssertRefused(message, argv);

    [Theory]
    [InlineData("serve", "--host", "127.0.0.1", "--port", "1", "--quiet")]
    // La borne : le port 65535.
    [InlineData("serve", "--port", "65535")]
    public void The_options_of_each_command_reach_the_configuration(params string[] argv)
    {
        var (code, _, errors) = Run(argv.Append("--config").Append(_missing).ToArray());
        Assert.Equal(1, code);
        Assert.Contains("absente.json", errors);   // la saisie est acceptée : c'est le fichier qui manque
        Assert.DoesNotContain("inconnue", errors);
    }

    private Container BuildContainer() => Composition.Build(AppConfig.Load() with { AiBaseUrl = "http://127.0.0.1:1" },
                                                       new Overrides(IndexPath: Path.Combine(_dir, "index.json")));

    [Theory]
    [InlineData("localhost", "http://localhost:8123", false)]
    [InlineData("0.0.0.0", "http://127.0.0.1:8123", true)]
    [InlineData("*", "http://127.0.0.1:8123", true)]
    [InlineData("+", "http://127.0.0.1:8123", true)]
    public void The_api_announces_an_address_to_connect_to(string host, string url, bool allInterfaces)
    {
        // Serve annonce Url, une adresse où l'on peut se connecter : l'hôte demandé, sauf quand il désigne toutes les
        // interfaces (0.0.0.0, « * », « + »). On ne s'y connecte pas, donc Url donne 127.0.0.1, et Serve le précise.
        // Le test n'appelle pas Start, pour ne pas écouter sur toutes les interfaces.
        using var api = new HttpApi(BuildContainer(), host, 8123, quiet: true);
        Assert.Equal((url, allInterfaces), (api.Url, api.AllInterfaces));
    }

    [Fact]
    public async Task An_unknown_user_names_the_known_ones_without_brackets()
    {
        // Même texte en ligne de commande et dans le 403 de l'API HTTP.
        const string expected = "utilisateur inconnu : mallory (connus : alice, bruno, claire)";
        Assert.Equal(expected, Assert.Throws<UnknownUserException>(() => AppConfig.Load().User("mallory")).Message);
        using var api = TestPorts.StartOnAFreePort(
            port => new HttpApi(BuildContainer(), "127.0.0.1", port, quiet: true), server => server.Start());
        using var http = new HttpClient();
        using var response = await http.PostAsync(api.Url + "/v1/ask",
            new StringContent("""{"user": "mallory", "question": "?"}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!;
        Assert.Equal(expected, error["message"]!.GetValue<string>());
    }

    [Fact]
    public void Serve_announces_where_it_listens_and_when_it_stops()
    {
        using var stopped = new ManualResetEvent(true);   // Ctrl+C déjà reçu : Serve rend la main aussitôt
        var (previous, output) = (Console.Out, new StringWriter());
        var port = 0;
        Console.SetOut(output);
        try
        {
            // Serve démarre l'API lui-même, sur un port libre. TestPorts fait un autre essai si ce port est pris
            // entre-temps.
            using var api = TestPorts.StartOnAFreePort(
                free =>
                {
                    port = free;
                    return new HttpApi(BuildContainer(), "127.0.0.1", free, quiet: true);
                },
                server => Assert.Equal(0, Program.Serve(server, "http://ia:8100", stopped)));
        }
        finally
        {
            Console.SetOut(previous);
        }
        Assert.Equal($"Application sur http://127.0.0.1:{port} (service IA : http://ia:8100) — Ctrl+C pour arrêter\n\nArrêt de l'application.\n",
                     output.ToString().Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Serve_on_a_taken_port_says_so()
    {
        var taken = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        try
        {
            var port = ((IPEndPoint)taken.LocalEndpoint).Port;
            var config = WriteConfig("http://127.0.0.1:1");
            // Sur un port libre, serve attendrait Ctrl+C : le délai fait échouer le test au lieu de le bloquer.
            var run = Task.Run(() => Run("serve", "--quiet", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--config", config));
            Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30))));
            var (code, output, errors) = await run;
            Assert.Equal((1, ""), (code, output));
            Assert.Contains("Erreur : impossible d'écouter (port déjà pris, adresse inconnue ou non autorisée) — ", errors);
        }
        finally
        {
            taken.Stop();
        }
    }
}
