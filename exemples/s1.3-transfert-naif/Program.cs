// Séquence 1.3 : le transfert naïf, ou « le modèle est un détail d'infrastructure ».
//
//   dotnet run --project exemples/s1.3-transfert-naif -- generate fake
//   dotnet run --project exemples/s1.3-transfert-naif -- generate extractive
//   dotnet run --project exemples/s1.3-transfert-naif -- generate extractive-variante
//   dotnet run --project exemples/s1.3-transfert-naif -- generate llama3-2-3b
//
// Seul l'alias fake se passe du service IA, et llama3-2-3b demande en plus Ollama.
//
// On change de générateur par un argument, et le domaine ne bouge pas : la promesse tient.

using System.Text;
using TransfertNaif.Domaine;
using TransfertNaif.Infrastructure;

Console.OutputEncoding = new UTF8Encoding(false);
var baseUrl = Environment.GetEnvironmentVariable("AI_SERVICE_URL") ?? "http://127.0.0.1:8100";

var corpus = new List<Passage>
{
    new("Télétravail", "Tout salarié ayant terminé sa période d'essai peut télétravailler jusqu'à deux jours par semaine."),
    new("Notes de frais", "Le repas est remboursé sur justificatif, plafonné à 25 euros."),
    new("Congés", "Les congés payés se prennent par journée entière, après validation du responsable."),
    new("Sécurité", "Le badge est strictement personnel ; le mot de passe change tous les 90 jours."),
};

try
{
    switch (args.Length > 0 ? args[0] : "")
    {
        case "generate":
        {
            // Le port est dans le domaine, et un argument choisit l'adaptateur.
            var alias = args.Length > 1 ? args[1] : "fake";
            ITextGenerator generator = alias == "fake" ? new FakeGenerator() : new HttpGenerator(baseUrl, alias);
            var assistant = new Assistant(generator);
            var question = args.Length > 2 ? args[2] : "Combien de jours de télétravail par semaine ?";
            Console.WriteLine($"Générateur : {alias}");
            Console.WriteLine(assistant.Answer(question, corpus));
            Console.WriteLine();
            Console.WriteLine("Le domaine (Assistant) n'a pas changé d'une ligne : la promesse tient.");
            return 0;
        }
        default:
            Console.WriteLine("Usage : generate <fake|alias> [question]");
            return 1;
    }
}
catch (Exception error) when (error is InvalidOperationException or HttpRequestException or AggregateException)
{
    Console.Error.WriteLine($"Erreur : {(error as AggregateException)?.InnerException?.Message ?? error.Message}");
    return 1;
}
