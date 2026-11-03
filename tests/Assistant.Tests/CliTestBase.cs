// Ce que partagent les tests de la ligne de commande, appelée comme on la tape. Ces tests sont rangés par commande,
// et CliTests vérifie l'analyse de la ligne et les options communes.

using System.Text.Json.Nodes;
using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

/// <summary>
/// Console.Out et Console.Error sont partagés par tout le processus : les tests qui les lisent ou y écrivent ne
/// doivent pas tourner en même temps. Ils sont tous dans cette collection, sauf le test de l'aide de ConfigTests,
/// qui écrit aussi en console et se trouve dans celle de l'environnement. Ces deux collections ne sont pas
/// parallèles : xUnit les fait passer l'une après l'autre, après les autres tests.
/// </summary>
[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleCollection
{
}

/// <summary>
/// Un dossier temporaire, Program.Main appelé comme dans un terminal, une configuration qui pointe vers un faux
/// service IA, et le contrôle d'une saisie refusée avant tout travail. Les classes qui en héritent sont dans la
/// collection « Console ».
/// </summary>
public abstract class CliTestBase : IDisposable
{
    protected readonly string _dir = Directory.CreateTempSubdirectory("fyc-cli-").FullName;
    protected readonly string _missing;   // lue seulement si la ligne de commande est acceptée

    protected CliTestBase() => _missing = Path.Combine(_dir, "absente.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Code de retour, sortie et erreurs de Program.Main, comme dans un terminal.</summary>
    protected static (int Code, string Out, string Err) Run(params string[] argv)
    {
        var (previousOut, previousError) = (Console.Out, Console.Error);
        var (output, errors) = (new StringWriter(), new StringWriter());
        Console.SetOut(output);
        Console.SetError(errors);
        try
        {
            return (Program.Main(argv), output.ToString().Replace("\r\n", "\n"), errors.ToString().Replace("\r\n", "\n"));
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    /// <summary>Le seuil vaut 0,15, celui de « hashing » sur le corpus Solvéo.</summary>
    protected string WriteConfig(string aiUrl, string corpus = "corpus/solveo")
    {
        var path = Path.Combine(_dir, "app.json");
        File.WriteAllText(path, new JsonObject
        {
            ["ai_service"] = new JsonObject
            {
                ["base_url"] = aiUrl, ["embedding_model"] = "hashing", ["generation_model"] = "extractive", ["timeout_seconds"] = 5,
            },
            ["corpus"] = new JsonObject { ["directory"] = corpus },
            ["index"] = new JsonObject { ["path"] = Path.Combine(_dir, "index.json") },
            ["snapshots"] = new JsonObject { ["directory"] = Path.Combine(_dir, "instantanes") },
            ["retrieval"] = new JsonObject { ["min_score"] = 0.15 },
            ["users"] = new JsonObject { ["alice"] = new JsonObject { ["groups"] = new JsonArray("tous") } },
        }.ToJsonString());
        return path;
    }

    /// <summary>Un jeu d'une seule question, posée par alice, que connaissent toutes les configurations.</summary>
    protected string WriteQuestions()
    {
        var path = Path.Combine(_dir, "questions.json");
        File.WriteAllText(path, """{"questions": [{"id": "tt", "user": "alice", "question": "Combien de jours de télétravail ?"}]}""");
        return path;
    }

    protected void AssertRefused(string message, string[] argv)
    {
        var (code, output, errors) = Run(argv.Append("--config").Append(_missing).ToArray());
        Assert.Equal(1, code);
        Assert.Contains($"Erreur : {message}", errors);
        Assert.Equal("", output);
    }

    /// <summary>
    /// Remplace « CONFIG » par « --config absente.json ». Il faut le placer avant « -- », sinon il serait lu comme un
    /// argument.
    /// </summary>
    protected string[] WithConfig(string[] argv) => argv.SelectMany(a => a == "CONFIG" ? new[] { "--config", _missing } : new[] { a }).ToArray();
}
