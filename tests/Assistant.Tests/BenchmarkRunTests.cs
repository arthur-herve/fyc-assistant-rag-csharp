// Le banc d'essai, de bout en bout contre un faux service IA (embeddings par mots-clés, génération qui cite [1]).

using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

/// <summary>
/// Le déroulé du banc, de bout en bout contre <see cref="FakeAiService"/>. Les options sont vérifiées avant tout
/// travail, la recherche passe par le cas d'usage et ses droits, les lignes s'écrivent au fil de l'eau et le seuil
/// `default` est signalé. Ces tests lisent la console, d'où la collection « Console », comme ceux de la ligne de
/// commande.
/// </summary>
[Collection("Console")]
public sealed class BenchmarkRunTests : EvalRunTestBase
{
    private const string Salary = "Quelle est la fourchette de salaire d'un consultant senior ?";

    private BenchmarkOptions Options(string outName, IReadOnlyList<EvalQuestion>? questions = null) =>
        new(new[] { "hashing" }, new[] { "extractive" }, questions ?? Questions, null, Runs: 1, OutDir: Path.Combine(_dir, outName));

    /// <summary>L'option est refusée avant tout travail : ni dossier de résultats, ni indexation.</summary>
    private T Refused<T>(BenchmarkOptions options, AppConfig? config = null) where T : Exception
    {
        var error = Assert.ThrowsAny<T>(() => Benchmark.Run(config ?? _config, options, _ => { }));
        Assert.False(Directory.Exists(options.OutDir));
        return error;
    }

    [Fact]
    public void Benchmark_options_are_refused_before_any_work()
    {
        Assert.Equal("--runs doit valoir au moins 1, pas 0", Refused<ArgumentException>(Options("jamais") with { Runs = 0 }).Message);
        Assert.Equal("--runs doit valoir au moins 1, pas -2", Refused<ArgumentException>(Options("jamais") with { Runs = -2 }).Message);
        foreach (var mode in new[] { "nan", "5" })
        {
            Assert.Equal($"--min-score attend config, auto ou un nombre de [-1, 1] (ex. 0.6), pas « {mode} »",
                         Refused<ArgumentException>(Options("jamais") with { MinScoreMode = mode }).Message);
        }
        // Le message entier, sans le « (Parameter 'maxChars') » que .NET ajoute.
        Assert.Equal("max_chars doit valoir au moins 100", Refused<ArgumentException>(Options("jamais") with { SplitterMaxChars = 50 }).Message);
        Assert.Equal("overlap_chars doit être compris entre 0 et max_chars / 2",
                     Refused<ArgumentException>(Options("jamais") with { SplitterOverlapChars = 900 }).Message);
        Assert.Equal(NoPrompt, Refused<Application.PromptNotFoundException>(Options("jamais"), _config with { PromptName = "nexiste" }).Message);
        var intruder = new[] { Questions[0] with { UserName = "mallory" } };
        Assert.Contains("mallory", Refused<UnknownUserException>(Options("jamais", intruder)).Message);
        Assert.Contains("mallory", Refused<UnknownUserException>(Options("jamais") with { Validation = intruder }).Message);
    }

    [Fact]
    public void The_benchmark_writes_each_row_as_soon_as_it_is_known()
    {
        var options = Options("banc");
        var written = new List<int>();
        Benchmark.Run(_config, options, message =>
        {
            if (message.Contains("passage 1/1 terminé"))
            {
                // Le fichier est encore ouvert par le banc : on le lit sans le bloquer.
                using var stream = new FileStream(Path.Combine(options.OutDir, "resultats.csv"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                written.Add(reader.ReadToEnd().Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
            }
        });
        Assert.Equal(new[] { 1 + options.Questions.Count }, written);   // en-tête et réponses, écrits avant la fin
    }

    [Fact]
    public void The_benchmark_writes_its_synthesis_like_ask_json()
    {
        // synthese.json s'écrit comme la sortie de ask --json (Presenter.ToJson), suivie d'une fin de ligne.
        var options = Options("banc-synthese");
        var summary = Benchmark.Run(_config, options, _ => { });
        var synthesis = File.ReadAllText(Path.Combine(options.OutDir, "synthese.json"));
        Assert.Equal(Presenter.ToJson(summary.ToJson()) + Environment.NewLine, synthesis);
    }

    [Fact]
    public void The_report_header_gives_the_splitter_in_json()
    {
        var options = Options("banc-entete");
        Benchmark.Run(_config, options, _ => { });
        Assert.Contains("découpage `{\"max_chars\":", File.ReadAllText(Path.Combine(options.OutDir, "rapport.md")));
    }

    [Fact]
    public void The_benchmark_checks_its_threshold_on_the_validation_questions()
    {
        var options = Options("banc-validation") with { Validation = Questions.Take(1).ToList() };
        var retrieval = Assert.IsType<RetrievalSummary>(Assert.Single(Benchmark.Run(_config, options, _ => { }).Retrieval));
        Assert.Equal(1, retrieval.Validation!.Questions);   // la question de validation seule, pas les deux du banc
    }

    [Fact]
    public void Each_answered_row_names_the_generation_model_served()
    {
        // Colonnes status (6e) et generation_model_id (9e) : aucune ne contient de virgule. La colonne donne
        // l'identifiant que le service IA a servi, « faux:extractive », et non l'alias demandé, « extractive ».
        var options = Options("banc-modele");
        Benchmark.Run(_config, options, _ => { });
        Assert.Equal(new[] { "faux:extractive" }, File.ReadLines(Path.Combine(options.OutDir, "resultats.csv")).Skip(1)
                                                      .Select(l => l.Split(',')).Where(f => f[5] == "answered").Select(f => f[8]).Distinct());
    }

    [Fact]
    public void The_benchmark_searches_with_the_rights_of_each_question()
    {
        // La recherche du banc passe par SearchPassages, donc par les droits et le contrôle du modèle. Même question et
        // même attente pour les deux : seul bruno a le droit de lire la grille des salaires.
        var questions = new[]
        {
            new EvalQuestion("rh", "bruno", Salary, new[] { "grille-salaires" }, Array.Empty<string>(), true, Array.Empty<string>()),
            new EvalQuestion("sans-droit", "alice", Salary, new[] { "grille-salaires" }, Array.Empty<string>(), true, Array.Empty<string>()),
        };
        var summary = Benchmark.Run(_config, Options("banc-droits", questions), _ => { });
        var retrieval = Assert.IsType<RetrievalSummary>(Assert.Single(summary.Retrieval));
        Assert.Equal((0.5, 0.5), (retrieval.HitAt1, retrieval.HitAtK));
    }

    [Fact]
    public void The_benchmark_says_when_the_default_threshold_applies()
    {
        var lines = new List<string>();
        var options = Options("banc-default");
        var retrieval = Assert.IsType<RetrievalSummary>(Assert.Single(Benchmark.Run(_config, options, lines.Add).Retrieval));
        Assert.True(retrieval.ConfiguredThresholdIsDefault);
        Assert.Contains(lines, l => l.Contains("seuil configuré=0.15 (default : aucun seuil pour cet alias)"));
        Assert.Contains("| 0.15 (default) |", File.ReadAllText(Path.Combine(options.OutDir, "rapport.md")));

        options = Options("banc-calibre");
        var calibrated = _config with { MinScores = new Dictionary<string, double> { ["default"] = 0.15, ["hashing"] = 0.15 } };
        retrieval = Assert.IsType<RetrievalSummary>(Assert.Single(Benchmark.Run(calibrated, options, _ => { }).Retrieval));
        Assert.False(retrieval.ConfiguredThresholdIsDefault);
        Assert.DoesNotContain("(default)", File.ReadAllText(Path.Combine(options.OutDir, "rapport.md")));
    }

    [Fact]
    public void The_benchmark_output_has_bare_numbers_slashes_and_lf_only()
    {
        // Avec un seuil configuré à 0 et un seuil imposé à 1, le journal écrit « 0 » et « 1 », sans décimales. Le
        // chemin du rapport s'affiche avec des « / », et le rapport n'a que des « \n », y compris dans la lecture du
        // seuil suggéré.
        var lines = new List<string>();
        var options = Options("banc-octets") with { MinScoreMode = "1", Validation = Questions.Take(1).ToList() };
        Benchmark.Run(_config with { MinScores = new Dictionary<string, double> { ["default"] = 0.15, ["hashing"] = 0.0 } }, options, lines.Add);
        var log = string.Join("\n", lines);
        Assert.Contains(" · seuil configuré=0 · ", log);
        Assert.Matches(@" · seuil utilisé=1\n", log);
        Assert.Contains("(1 questions jamais vues, seuil 1) :", log);
        Assert.Equal($"\nRapport : {Path.Combine(options.OutDir, "rapport.md").Replace('\\', '/')}", lines[^1]);
        var report = File.ReadAllText(Path.Combine(options.OutDir, "rapport.md"));
        Assert.DoesNotContain('\r', report);
        Assert.Contains("- **Seuil suggéré** : sépare au mieux les questions répondables des questions hors corpus. "
                        + "Calibré sur ces mêmes questions, il est optimiste.\n", report);
    }

    [Fact]
    public void Two_benchmarks_of_the_same_second_keep_their_own_folder()
    {
        // Sans --out, le dossier est daté à la seconde, et un second banc lancé dans la même seconde tomberait sur le
        // même. Il reçoit « -2 » (NewOutDir), et tous ses fichiers y vont. CliBenchmarkTests.cs vérifie le même cas
        // sur la commande.
        var name = Path.Combine("resultats", "20261001-164152");
        var (path, second) = (Path.Combine(_dir, name), Path.Combine(_dir, name) + "-2");
        var lines = new List<string>();
        foreach (var count in new[] { 1, 2 })
        {
            Benchmark.Run(_config, Options(name, Questions.Take(count).ToList()) with { NewOutDir = true }, lines.Add);
        }
        Assert.Equal(new[] { path, second }.Select(d => $"\nRapport : {Path.Combine(d, "rapport.md").Replace('\\', '/')}"),
                     lines.Where(l => l.StartsWith("\nRapport : ", StringComparison.Ordinal)));
        Assert.Contains("\n1 questions · ", File.ReadAllText(Path.Combine(path, "rapport.md")));
        Assert.Contains("\n2 questions · ", File.ReadAllText(Path.Combine(second, "rapport.md")));
        foreach (var folder in new[] { path, second })
        {
            Assert.Equal(new[] { "index-hashing.json", "rapport.md", "resultats.csv", "synthese.json" },
                         Directory.GetFileSystemEntries(folder).Select(f => Path.GetFileName(f)).Order());
        }
    }
}
