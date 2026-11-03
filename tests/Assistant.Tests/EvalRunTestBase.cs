// Ce que partagent les tests qui font tourner le banc et les expériences de bout en bout, contre un faux service IA
// (embeddings par mots-clés, génération qui cite [1]).

using Assistant.Cli;

namespace Assistant.Tests;

/// <summary>
/// Un petit projet dans un dossier temporaire : trois fiches, deux questions et un faux service IA. Les tests qui
/// font tourner une évaluation de bout en bout en héritent.
/// </summary>
public abstract class EvalRunTestBase : IDisposable
{
    /// <summary>Le message pour le prompt « nexiste » : son nom, le dossier et les prompts livrés.</summary>
    protected static readonly string NoPrompt = $"prompt introuvable : nexiste dans {Composition.PromptsDir} (connus : answer, answer-v2)";
    protected readonly FakeAiService _ai = new();
    protected readonly string _dir = Directory.CreateTempSubdirectory("fyc-eval-").FullName;
    protected readonly AppConfig _config;
    protected readonly string _questionsPath;

    protected EvalRunTestBase()
    {
        var corpus = Directory.CreateDirectory(Path.Combine(_dir, "corpus")).FullName;
        foreach (var (id, groups, text) in new[]
                 {
                     ("teletravail", "tous", "Deux jours de télétravail par semaine."),
                     ("frais", "tous", "Le repas est plafonné à 25 euros, frais remboursés."),
                     ("grille-salaires", "rh", "Salaire senior : 56 000 à 68 000 euros."),
                 })
        {
            File.WriteAllText(Path.Combine(corpus, $"{id}.md"), $"---\nid: {id}\ntitre: {id}\ngroupes: {groups}\n---\n{text}\n");
        }
        // Seul le seuil `default` est configuré : aucun alias n'a le sien. Le délai est court pour qu'un faux service
        // muet fasse échouer le test vite.
        _config = AppConfig.Load() with
        {
            AiBaseUrl = _ai.Url, Timeout = TimeSpan.FromSeconds(5), CorpusDir = corpus,
            MinScores = new Dictionary<string, double> { ["default"] = 0.15 },
        };
        _questionsPath = Path.Combine(_dir, "questions.json");
        File.WriteAllText(_questionsPath, """
            {"questions": [
              {"id": "tt", "user": "alice", "question": "Combien de jours de télétravail par semaine ?", "expected_documents": ["teletravail"]},
              {"id": "frais", "user": "alice", "question": "Quel est le plafond d'un repas ?", "expected_documents": ["frais"]}
            ]}
            """);
    }

    public void Dispose()
    {
        _ai.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    protected List<EvalQuestion> Questions => EvalQuestions.Load(_questionsPath);
}
