// Démarrage des serveurs de test sur un port libre. La boucle qui sonde un port, écoute dessus puis réessaie en
// cas de conflit n'est écrite qu'ici.

using System.Net;
using System.Net.Sockets;

namespace Assistant.Tests;

public static class TestPorts
{
    /// <summary>
    /// Crée le serveur sur un port libre, puis le démarre. Sonder un port libre avec le port 0 puis écouter
    /// dessus n'est pas atomique : si un autre processus prend le port entre les deux, on réessaie, cinq fois
    /// au plus.
    /// </summary>
    public static T StartOnAFreePort<T>(Func<int, T> create, Action<T> start) where T : IDisposable
    {
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var server = create(port);
            try
            {
                start(server);
                return server;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                server.Dispose();
            }
        }
    }

    /// <summary>Un HttpListener démarré sur un port libre ; son seul préfixe est http://127.0.0.1:port/.</summary>
    public static HttpListener Listener() => StartOnAFreePort(port =>
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        return listener;
    }, listener => listener.Start());
}
