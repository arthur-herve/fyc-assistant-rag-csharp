// Ce que la ligne de commande affiche doit se lire : null, true, [a, b], « 33 % ».

using System.Text.Json.Nodes;
using Assistant.Application;
using Assistant.Cli;
using Assistant.Domain;
using Xunit;

namespace Assistant.Tests;

/// <summary>Des affichages qui se lisent : null, true, [a, b], « 33 % », clés du découpage triées.</summary>
public sealed class ReadableDisplayTests
{
    private static readonly Dictionary<string, object> Splitter = new()
    {
        ["type"] = "paragraph", ["max_chars"] = 800, ["overlap_chars"] = 120, ["include_title"] = true,
    };

    private static SnapshotComparison Comparison(int changed, int compared,
                                                 Dictionary<string, object?>? before = null, Dictionary<string, object?>? after = null)
    {
        Snapshot Make(string name, Dictionary<string, object?>? configuration, string text) => new(name, "2026-09-11",
            configuration ?? new Dictionary<string, object?>(),
            Enumerable.Range(0, compared).Select(i => new SnapshotEntry($"q{i}", "alice", "?", "answered", new[] { "teletravail", "conges" },
                                                                         i < changed ? text : "même")).ToList());
        return SnapshotComparer.Compare(Make("a", before, "avant"), Make("b", after, "après"));
    }

    [Fact]
    public void The_drift_rate_lists_and_configuration_differences()
    {
        var text = Presenter.ComparisonToText(Comparison(1, 3,
            new Dictionary<string, object?> { ["temperature"] = 0.2, ["splitter"] = Splitter },
            new Dictionary<string, object?> { ["temperature"] = 0.7, ["splitter"] = new Dictionary<string, object>(Splitter) { ["max_chars"] = 300 } }))
            .Replace("\r\n", "\n");
        Assert.Contains("  taux de dérive      : 33 %", text);
        Assert.Contains("  avant : answered [teletravail, conges] « avant »", text);
        Assert.Contains("  - temperature : 0.2 → 0.7", text);
        Assert.Contains("  - splitter : {include_title=true, max_chars=800, overlap_chars=120, type=paragraph} → "
                        + "{include_title=true, max_chars=300, overlap_chars=120, type=paragraph}", text);
    }

    [Fact]
    public void The_whole_comparison_line_by_line()
    {
        // Le texte entier, ligne par ligne, que la console joint avec les fins de ligne du système.
        var comparison = Comparison(1, 2, new Dictionary<string, object?> { ["top_k"] = 4 }, new Dictionary<string, object?> { ["top_k"] = 8 });
        var expected = new[]
        {
            "Comparaison : a → b", "", "Différences de configuration", "  - top_k : 4 → 8", "",
            "Dérive", "  questions comparées : 2", "  réponses modifiées  : 1", "  taux de dérive      : 50 %", "",
            "| Nature | Nombre | Lecture |", "|---|---|---|",
            "| statut modifié | 0 | changement de comportement : refus devenu réponse, ou l'inverse |",
            "| sources modifiées | 0 | même décision, autres documents cités |",
            "| texte modifié | 1 | mêmes sources, même statut, texte différent : à relire, le sens a pu changer (Oui devenu Non…) |",
            "| identique | 1 | rien n'a bougé |",
            "| absente d'un des deux | 0 | question présente d'un seul côté |",
            "", "q0 [texte modifié]", "  avant : answered [teletravail, conges] « avant »",
            "  après : answered [teletravail, conges] « après »",
        };
        Assert.Equal(expected, Presenter.ComparisonLines(comparison));
        Assert.Equal(string.Join(Environment.NewLine, expected), Presenter.ComparisonToText(comparison));
    }

    [Fact]
    public void Answers_are_cut_at_90_code_points()
    {
        // Les lignes « avant » et « après » gardent les 90 premiers caractères (points de code), et non 90 unités
        // UTF-16. Un emoji placé avant la coupure compte pour un et reste entier : coupé en deux, sa moitié s'écrirait
        // « � ». Une moitié de paire déjà isolée compte aussi pour un et reste telle quelle.
        const string emoji = "\U0001F600";
        var cases = new (string Text, string Head)[]
        {
            (new string('a', 89) + emoji + new string('b', 10), new string('a', 89) + emoji),
            (emoji + new string('a', 88) + "\ud800" + "b", emoji + new string('a', 88) + "\ud800"),
            ("court", "court"),
        };
        static Snapshot Make(string name, string text) => new(name, "2026-09-11", new Dictionary<string, object?>(),
            new[] { new SnapshotEntry("q0", "alice", "?", "answered", new[] { "d" }, text) });
        foreach (var (text, head) in cases)
        {
            var lines = Presenter.ComparisonLines(SnapshotComparer.Compare(Make("a", text), Make("b", text + "c")));
            Assert.Contains($"  avant : answered [d] « {head} »", lines);
        }
    }

    [Fact]
    public void The_configuration_values()
    {
        // Les valeurs s'écrivent null, true et false, plutôt que rien, True et False. Un réel s'écrit avec un point, et
        // les listes et dictionnaires sans guillemets.
        Assert.Equal(new[] { "null", "true", "false", "0.15", "4", "hashing" },
                     new object?[] { null, true, false, 0.15, 4, "hashing" }.Select(ConfigurationValues.Canonical));
        Assert.Equal("{include_title=true, max_chars=800, overlap_chars=120, type=paragraph}", ConfigurationValues.Canonical(Splitter));
        Assert.Equal("[a, null, true, 0.5]", ConfigurationValues.Canonical(new object?[] { "a", null, true, 0.5 }));
    }

    [Fact]
    public void The_verbose_answer()
    {
        // Chaque sortie brute s'écrit en chaîne JSON, entre guillemets, sur une seule ligne grâce aux sauts de ligne
        // échappés. Relue, elle redonne la sortie telle quelle, y compris espaces insécables, emoji et caractères de
        // contrôle.
        const string raw = "Deux\u00a0jours\u202f! \U0001F600\u2028\u001b\u007f \"q\" \\ \n\t";
        var trace = new AnswerTrace(new[] { new Retrieved("teletravail#0", 0.5276), new Retrieved("conges#0", 0.0) }, 2.0,
                                    new[] { "Deux jours.\n[1]", raw });
        var text = Presenter.AnswerToText(new Answer("?", AnswerStatus.Answered, "Deux jours. [1]", Array.Empty<Source>(), trace), verbose: true)
            .Replace("\r\n", "\n");
        Assert.Contains("seuil=2\n", text);
        Assert.Contains("  retrouvé conges#0 score=0\n", text);
        Assert.Contains("  retrouvé teletravail#0 score=0.5276", text);
        Assert.Contains("  sortie brute 1 : \"Deux jours.\\n[1]\"\n", text);
        const string prefix = "  sortie brute 2 : ";
        var last = text[(text.LastIndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..];
        Assert.DoesNotContain('\n', last);   // une sortie par ligne
        Assert.Equal(raw, JsonNode.Parse(last)!.GetValue<string>());   // une chaîne JSON, qui redonne la sortie
    }

    /// <summary>Une réponse aux textes délicats de Bytes.Special, qui cite un document au titre accentué.</summary>
    private static readonly Answer Sample = new("Combien ?", AnswerStatus.Answered, Bytes.Special, new[] { new Source(1, "a", "Congés", "a#0") },
        new AnswerTrace(new[] { new Retrieved("a#0", 1.0), new Retrieved("a#1", 0.25) },
                        0.4, Array.Empty<string>()));

    [Fact]
    public void The_json_of_an_answer_is_indented_and_reads_back_the_same()
    {
        // ask --json écrit un JSON indenté. Relu, il redonne les mêmes valeurs, y compris le texte avec son espace
        // insécable, ses guillemets, son saut de ligne et son emoji.
        var written = Presenter.ToJson(Presenter.AnswerToJson(Sample));
        Assert.StartsWith("{" + Environment.NewLine + "  \"question\": \"Combien ?\",", written);
        Assert.True(JsonNode.DeepEquals(Presenter.AnswerToJson(Sample), JsonNode.Parse(written)));
    }

    [Fact]
    public void The_json_of_an_answer_keeps_accents_as_they_are() =>
        Assert.Contains("\"document_title\": \"Congés\"", Presenter.ToJson(Presenter.AnswerToJson(Sample)));
}
