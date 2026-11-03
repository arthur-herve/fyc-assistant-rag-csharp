// La ligne de commande de l'application. Elle n'a encore rien à lancer : le cœur se teste sans elle, avec
// dotnet test.

using System.Text;

namespace Assistant.Cli;

public static class Program
{
    public static int Main()
    {
        // Sortie en UTF-8, même redirigée vers un fichier : sans cela, Windows l'encoderait en cp1252.
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.WriteLine("L'application arrive en S2.3. En attendant, le cœur se teste avec dotnet test.");
        return 0;
    }
}
