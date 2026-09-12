// Séquence 1.3 — le transfert naïf : « le modèle est un détail d'infrastructure ».
//
//   dotnet run --project exemples/s1.3-transfert-naif -- generate fake
//   dotnet run --project exemples/s1.3-transfert-naif -- generate llama3-2-3b        (service IA lancé)
//   dotnet run --project exemples/s1.3-transfert-naif -- index hashing
//   dotnet run --project exemples/s1.3-transfert-naif -- search hashing "jours de télétravail"
//   dotnet run --project exemples/s1.3-transfert-naif -- search hashing-512 "jours de télétravail"   → plante (dimension)
//   dotnet run --project exemples/s1.3-transfert-naif -- search hashing-stem4 "remboursement du repas" → répond à côté, sans rien dire
//
// Partie 1 (generate) : substituer le générateur marche comme promis, le domaine ne bouge pas.
// Partie 2 (index / search) : substituer le fournisseur d'embeddings « marche » aussi… et
// c'est là que le transfert naïf casse : l'index a été construit dans l'espace d'un modèle,
// et rien ne le sait. Le fil rouge répond à cela avec un manifeste et un refus explicite.

using System.Text;
using System.Text.Json;
using TransfertNaif.Domaine;
using TransfertNaif.Infrastructure;

Console.OutputEncoding = new UTF8Encoding(false);
var baseUrl = Environment.GetEnvironmentVariable("AI_SERVICE_URL") ?? "http://127.0.0.1:8100";
var indexPath = Path.Combine(AppContext.BaseDirectory, "index-naif.json");

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
            // Partie 1 : le port dans le domaine, deux adaptateurs, substitution par un argument.
            var alias = args.Length > 1 ? args[1] : "fake";
            ITextGenerator generator = alias == "fake" ? new FakeGenerator() : new HttpGenerator(baseUrl, alias);
            var assistant = new Assistant(generator);
            var question = args.Length > 2 ? args[2] : "Combien de jours de télétravail par semaine ?";
            Console.WriteLine($"Générateur : {alias}");
            Console.WriteLine(assistant.Answer(question, corpus.Take(2).ToList()));
            Console.WriteLine();
            Console.WriteLine("→ Le domaine (Assistant) n'a pas changé d'une ligne. Jusqu'ici, la promesse tient.");
            return 0;
        }
        case "index":
        {
            // Partie 2a : on indexe avec un fournisseur d'embeddings, et on ne note nulle part lequel.
            var alias = Alias(args);
            var provider = Provider(alias, baseUrl);
            var index = new NaiveIndex();
            foreach (var passage in corpus)
            {
                index.Add(passage, provider.Embed(passage.Text));
            }
            File.WriteAllText(indexPath, JsonSerializer.Serialize(index.Entries.Select(e => new { e.Passage.Title, e.Passage.Text, e.Vector })));
            Console.WriteLine($"Index de {index.Count} passages construit avec « {alias} » et écrit dans {indexPath}.");
            Console.WriteLine("→ Le fichier contient des vecteurs. Il ne contient pas le nom du modèle qui les a produits.");
            return 0;
        }
        case "search":
        {
            // Partie 2b : on cherche avec un fournisseur… qui n'est peut-être pas celui de l'index.
            var alias = Alias(args);
            var question = args.Length > 2 ? args[2] : "jours de télétravail";
            if (!File.Exists(indexPath))
            {
                throw new InvalidOperationException("aucun index : lancer d'abord `index <alias>`");
            }
            var index = new NaiveIndex();
            foreach (var entry in JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(indexPath))!)
            {
                index.Add(new Passage(entry.Title, entry.Text), entry.Vector);
            }
            var results = index.Search(Provider(alias, baseUrl).Embed(question), topK: 2);
            Console.WriteLine($"Recherche avec « {alias} » : {question}");
            foreach (var (passage, score) in results)
            {
                Console.WriteLine($"  {score:0.000}  {passage.Title}");
            }
            Console.WriteLine();
            Console.WriteLine("→ Si le fournisseur n'est pas celui de l'index, la dimension a changé (plantage) ou n'a pas changé");
            Console.WriteLine("  (scores sans signification, aucune erreur). Dans les deux cas, rien dans ce programme ne pouvait le savoir.");
            return 0;
        }
        default:
            Console.WriteLine("Usage : generate <fake|alias> [question] · index <alias> · search <alias> [question]");
            return 1;
    }
}
catch (Exception error) when (error is InvalidOperationException or HttpRequestException or AggregateException)
{
    Console.Error.WriteLine($"Erreur : {(error as AggregateException)?.InnerException?.Message ?? error.Message}");
    return 1;
}

static string Alias(string[] args) => args.Length > 1 ? args[1] : "fake";

static IEmbeddingProvider Provider(string alias, string baseUrl) =>
    alias == "fake" ? new KeywordEmbeddings() : new HttpEmbeddings(baseUrl, alias);

internal sealed record Entry(string Title, string Text, double[] Vector);
