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

    /// <summary>Un instantané d'une seule réponse donnée, avec ce texte.</summary>
    private static Application.Snapshot OneAnswer(string text) =>
        new("s", "2026-09-12T00:00:00+00:00", new Dictionary<string, object?>(),
            new[] { new Application.SnapshotEntry("q1", "alice", "?", "answered", Array.Empty<string>(), text, 1) });

    /// <summary>Une comparaison où aucune réponse ne change.</summary>
    private static readonly Application.SnapshotComparison[] Stable =
        { Application.SnapshotComparer.Compare(OneAnswer("même"), OneAnswer("même")) };

    /// <summary>La même, plus une comparaison où une seule réponse change.</summary>
    private static readonly Application.SnapshotComparison[] Drifted =
        Stable.Append(Application.SnapshotComparer.Compare(OneAnswer("même"), OneAnswer("autre"))).ToArray();

    [Fact]
    public void The_stability_reading_claims_random_failures_only_after_a_measured_drift()
    {
        // Un test par assertion exacte n'échoue au hasard que sur des réponses qui bougent. Si aucune réponse ne
        // change, comme avec extractive, ou avec extractive-bruite et une graine, la lecture dit que ce générateur
        // s'est montré déterministe. Une seule réponse modifiée, dans une seule des comparaisons, suffit pour passer à
        // l'autre lecture.
        const string Claim = "un test par assertion exacte sur ces réponses échouerait au hasard";

        var quiet = Experiments.StabilityReading(Stable);
        Assert.DoesNotContain(quiet, line => line.Contains(Claim));
        Assert.Contains(quiet, line => line.Contains("ce générateur s'est montré déterministe"));
        Assert.Contains(quiet, line => line.Contains("`--generation-model extractive-bruite` sans graine"));

        var moving = Experiments.StabilityReading(Drifted);
        Assert.Contains(moving, line => line.Contains(Claim));
        Assert.DoesNotContain(moving, line => line.Contains("déterministe"));
    }

    [Fact]
    public void The_stability_reading_always_ends_with_the_seed_line()
    {
        // La dernière ligne, sur la graine, est la même dans les deux cas.
        var (quiet, moving) = (Experiments.StabilityReading(Stable), Experiments.StabilityReading(Drifted));
        Assert.Equal(quiet[^1], moving[^1]);
        Assert.StartsWith("- Une graine réduit la variabilité", moving[^1]);
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
                new Application.SnapshotEntry("q1", "alice", "Q1", "answered", new[] { "teletravail" }, "Deux jours [1]", 1),
                new Application.SnapshotEntry("q2", "alice", "Q2", "no_relevant_source", Array.Empty<string>(), "…", 0),
                new Application.SnapshotEntry("q3", "alice", "Q3", "answered", new[] { "grille" }, "Fuite [1]", 1),
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
