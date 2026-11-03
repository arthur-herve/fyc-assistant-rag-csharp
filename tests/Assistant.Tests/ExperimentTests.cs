// Les calculs des expériences sont déterministes et se testent sans modèle : les mesures lues dans un instantané
// et la lecture de la stabilité.

using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

public class ExperimentTests
{
    [Fact]
    public void Keyword_coverage_ignores_case_and_accents()
    {
        Assert.Equal(1.0, Keywords.Coverage("Deux jours de TÉLÉTRAVAIL par semaine.", new[] { "deux jours", "teletravail" }));
    }

    [Fact]
    public void Keyword_coverage_is_null_without_keywords() =>
        Assert.Null(Keywords.Coverage("Deux jours.", Array.Empty<string>()));

    [Fact]
    public void The_stability_reading_describes_the_measure_without_concluding()
    {
        // Si aucune réponse ne change, comme avec extractive, la lecture dit que ce générateur s'est montré
        // déterministe et nomme celui qui simule le défaut. Une seule réponse modifiée, dans une seule des
        // comparaisons, suffit pour passer à l'autre lecture. Ni l'une ni l'autre ne dit comment tester des réponses
        // qui bougent : c'est la question de l'exercice.
        static Application.Snapshot Make(string text) => new("s", "2026-09-12T00:00:00+00:00", new Dictionary<string, object?>(),
            new[] { new Application.SnapshotEntry("q1", "alice", "?", "answered", Array.Empty<string>(), text) });
        var stable = new[] { Application.SnapshotComparer.Compare(Make("même"), Make("même")) };
        var drifted = stable.Append(Application.SnapshotComparer.Compare(Make("même"), Make("autre"))).ToArray();

        var quiet = Experiments.StabilityReading(stable);
        Assert.Contains(quiet, line => line.Contains("ce générateur s'est montré déterministe"));
        Assert.Contains(quiet, line => line.Contains("`--generation-model extractive-bruite` simule ce défaut"));

        var moving = Experiments.StabilityReading(drifted);
        Assert.Contains(moving, line => line.Contains("Des réponses ont bougé alors que rien n'a changé entre les passages"));
        Assert.DoesNotContain(moving, line => line.Contains("déterministe"));

        Assert.All(quiet.Concat(moving), line => Assert.DoesNotMatch("assertion|tolérance|proportion|faux échec", line));
    }

    [Fact]
    public void Experiment_statistics_read_what_the_questions_expect()
    {
        var questions = new List<EvalQuestion>
        {
            new("q1", "alice", "Q1", new[] { "teletravail" }, new[] { "deux jours", "semaine" }, true, Array.Empty<string>()),
            new("q2", "alice", "Q2", Array.Empty<string>(), Array.Empty<string>(), false, Array.Empty<string>()),
            new("q3", "alice", "Q3", new[] { "frais" }, Array.Empty<string>(), true, new[] { "grille" }),
        };
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var config = AppConfig.Load();
            var exp = new Experiment("test", config, questions, dir.FullName, "questions", "config");
            var snapshot = new Application.Snapshot("s", "2026-09-12T00:00:00+00:00", new Dictionary<string, object?>(), new[]
            {
                new Application.SnapshotEntry("q1", "alice", "Q1", "answered", new[] { "teletravail" }, "Deux jours [1]"),
                new Application.SnapshotEntry("q2", "alice", "Q2", "no_relevant_source", Array.Empty<string>(), "…"),
                new Application.SnapshotEntry("q3", "alice", "Q3", "answered", new[] { "grille" }, "Fuite [1]"),
            });
            var stats = exp.Stats(snapshot);
            Assert.Equal(6, stats.Count);
            Assert.Equal(1.0, stats["répond (répondables)"]);
            Assert.Equal(0.5, stats["bonne source"]);
            Assert.Equal(0.5, stats["mots-clés (réponses données)"]);   // « deux jours » oui, « semaine » non
            Assert.Equal(1.0, stats["refus justes (sans réponse accessible)"]);
            Assert.Equal(0.0, stats["non sourcé"]);
            Assert.Equal(1.0, stats["fuites d'accès"]);   // q3 cite un document interdit : l'expérience doit le voir
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
