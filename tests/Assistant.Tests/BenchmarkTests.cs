// Le banc d'essai et les expériences se testent sans modèle : leurs calculs sont déterministes, et
// leur déroulé se joue contre un faux service IA (embeddings par mots-clés, génération qui cite [1]).

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

public class BenchmarkTests
{
    [Fact]
    public void Csv_lines_quote_commas_quotes_and_line_breaks_as_in_rfc_4180()
    {
        var row = new BenchmarkRow("e", "g", 1, "q", true, "answered", 100, Attempts: 1, GenerationModelId: "m", CitedDocuments: "d",
                                   SourceHit: true, KeywordCoverage: 0.5, Text: "Réponse, \"citée\"\nsuite");
        // Fin de ligne exclue : virgules et guillemets protégés, champ vide pour « sans objet ».
        Assert.Equal("e,g,1,q,True,answered,1,100,m,d,True,False,0.5,,\"Réponse, \"\"citée\"\"\nsuite\"", row.CsvLine());
        // Un réel au format invariant de .NET (1) ; un retour chariot est protégé par des guillemets, comme un saut de ligne.
        var whole = new BenchmarkRow("e", "g", 2, "q", false, "no_relevant_source", 3, Attempts: 0, KeywordCoverage: 1.0, Text: "a\rb");
        Assert.Equal("e,g,2,q,False,no_relevant_source,0,3,,,,False,1,,\"a\rb\"", whole.CsvLine());
    }

    [Fact]
    public void Csv_columns_are_those_of_the_reference_results()
    {
        // Mêmes colonnes, dans le même ordre, que les résultats de référence.
        var references = Directory.GetFiles(Path.Combine(AppConfig.ProjectRoot, "eval", "resultats"), "resultats.csv", SearchOption.AllDirectories);
        Assert.NotEmpty(references);
        Assert.All(references, path => Assert.Equal(File.ReadLines(path).First().Split(','), BenchmarkRow.CsvFields));
    }

    [Fact]
    public void Latency_is_measured_on_generated_answers_only()
    {
        var rows = new List<BenchmarkRow>
        {
            new("e", "g", 1, "q1", true, "answered", 900, Attempts: 1),
            new("e", "g", 1, "q2", false, "no_relevant_source", 2, Attempts: 0),   // refus sans appel au modèle
        };
        var summary = Benchmark.Summarize(rows).Single();
        Assert.Equal(900.0, summary.LatencyMedianMs);
        Assert.Equal(900.0, summary.LatencyP90Ms);
        Assert.Equal(1.0, summary.MeanAttempts);
        Assert.Equal(1, summary.RefusalsWithoutGeneration);
    }

    [Fact]
    public void Synthesis_is_written_under_fixed_keys_in_a_fixed_order()
    {
        // synthese.json : des clés fixes, dans un ordre fixe, indépendantes des noms des propriétés C#.
        var validation = new ThresholdValidation(16, 0.923, 0.923, 1.0, 0.65);
        var retrieval = new RetrievalSummary("bge-m3", "ollama:bge-m3@7907", 1024, 740, 19.9, 0.969, 0.969, 4, 0.737, 0.535,
                                             0.65, false, 0.635, 1.0, 0.65, validation);
        var generation = new GenerationSummary("bge-m3", "llama3-2-3b", 42, 0, 0.969, 1.0, 0.871, 0.024, 0.9, 0, null, 1.1, 850.0, 1200.0, 3);
        Assert.Equal(new[]
        {
            "embedding", "model_id", "dimension", "chunks", "index_seconds", "hit@1", "hit@k", "top_k", "top1_median_answerable",
            "top1_median_unanswerable", "configured_threshold", "configured_threshold_is_default", "suggested_threshold",
            "separation_accuracy", "threshold_used", "validation",
        }, retrieval.ToJson().Select(kv => kv.Key));
        Assert.Equal("""{"questions":16,"hit@1":0.923,"kept_answerable":0.923,"correct_refusals":1,"threshold":0.65}""",
                     retrieval.ToJson()["validation"]!.ToJsonString());
        Assert.Null((retrieval with { Validation = null }).ToJson()["validation"]);   // sans --validate-with : clé absente
        Assert.False((retrieval with { Validation = null }).ToJson().ContainsKey("validation"));
        Assert.Equal("""{"embedding":"nomic","error":"panne"}""", new IndexingFailure("nomic", "panne").ToJson().ToJsonString());
        Assert.Equal(new[]
        {
            "embedding", "generation", "calls", "errors", "answer_rate", "source_hit_rate", "keyword_coverage", "unsourced_rate",
            "correct_refusal_rate", "forbidden_leaks", "stability", "mean_attempts", "latency_median_ms", "latency_p90_ms",
            "refusals_without_generation",
        }, generation.ToJson().Select(kv => kv.Key));
        Assert.Null(generation.ToJson()["stability"]);   // « sans objet » s'écrit null
    }

    [Fact]
    public void Synthesis_is_indented_json_that_reads_back_the_same()
    {
        // Comme la sortie de status --json (Presenter.ToJson) : indenté, accents et guillemets français tels quels ;
        // relu, les mêmes valeurs, « sans objet » compris (null).
        var summary = new BenchmarkSummary(
            new RetrievalResult[]
            {
                new RetrievalSummary("hashing", "hashing:256@abc", 256, 15, 0.3, 1.0, 1.0, 4, 0.5, 0.25, 0.4, true, 0.375, 1.0, 0.4,
                                     new ThresholdValidation(2, 1.0, 1.0, 0.0, 0.4)),
                new IndexingFailure("nomic", "Service IA : HTTP 502 — « modèle absent »"),
            },
            new[] { new GenerationSummary("hashing", "extractive", 4, 0, 1.0, 0.5, null, 0.0, 1.0, 0, null, 1.0, 12.0, 15.0, 2) });
        var written = Presenter.ToJson(summary.ToJson());
        Assert.StartsWith("{" + Environment.NewLine + "  \"retrieval\": [", written);
        Assert.Contains("\"error\": \"Service IA : HTTP 502 — « modèle absent »\"", written);
        Assert.True(JsonNode.DeepEquals(summary.ToJson(), JsonNode.Parse(written)));
    }

    [Fact]
    public void Suggested_threshold_separates_the_two_populations()
    {
        var (threshold, separation) = Benchmark.SuggestThreshold(new[] { 0.8, 0.7, 0.65 }, new[] { 0.4, 0.5 });
        Assert.NotNull(threshold);
        Assert.InRange(threshold!.Value, 0.5, 0.65);
        Assert.Equal(1.0, separation);
    }

    [Fact]
    public void Suggested_threshold_needs_both_populations() =>
        Assert.Equal((null, null), Benchmark.SuggestThreshold(new[] { 0.8 }, Array.Empty<double>()));

    [Fact]
    public void Keyword_coverage_ignores_case_and_accents()
    {
        Assert.Equal(1.0, Benchmark.KeywordCoverage("Deux jours de TÉLÉTRAVAIL par semaine.", new[] { "deux jours", "teletravail" }));
        Assert.Equal(0.5, Benchmark.KeywordCoverage("Deux jours.", new[] { "deux jours", "30 euros" }));
        Assert.Null(Benchmark.KeywordCoverage("Deux jours.", Array.Empty<string>()));
    }

    [Fact]
    public void Median_and_p90_are_taken_from_the_sorted_values()
    {
        Assert.Equal(2.5, Benchmark.Median(new[] { 4.0, 1.0, 3.0, 2.0 }));
        Assert.Equal(3.0, Benchmark.Median(new[] { 3.0, 1.0, 5.0 }));
        Assert.Equal(9.0, Benchmark.P90(Enumerable.Range(0, 10).Select(i => (double)i).ToList()));
        Assert.Null(Benchmark.Mean(Array.Empty<double>()));
    }

    [Fact]
    public void Min_score_is_config_auto_or_a_threshold_of_the_configuration()
    {
        Assert.Null(Benchmark.FixedMinScore("config"));
        Assert.Null(Benchmark.FixedMinScore("auto"));
        Assert.Equal(new double?[] { 0.6, -1.0, 1.0 }, new[] { "0.6", "-1", "1" }.Select(Benchmark.FixedMinScore));
        foreach (var mode in new[] { "abc", "nan", "inf", "5", "-1.5" })
        {
            var error = Assert.Throws<ArgumentException>(() => Benchmark.FixedMinScore(mode));
            Assert.Equal($"--min-score attend config, auto ou un nombre de [-1, 1] (ex. 0.6), pas « {mode} »", error.Message);
        }
    }

    /// <summary>
    /// Répond <paramref name="models"/> (texte JSON tel quel), avec ce statut, à la première requête reçue : un faux
    /// GET /v1/models, ou /health.
    /// </summary>
    private static Task AnswerOnce(HttpListener listener, string models, int status = 200) => Task.Run(async () =>
    {
        var context = await listener.GetContextAsync();
        context.Response.StatusCode = status;
        var bytes = Encoding.UTF8.GetBytes(models);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    });

    /// <summary>Répond ces octets tels quels à la première requête reçue : un faux GET /v1/models qui n'est pas en UTF-8.</summary>
    private static Task AnswerOnce(HttpListener listener, byte[] bytes) => Task.Run(async () =>
    {
        var context = await listener.GetContextAsync();
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    });

    [Fact]
    public async Task The_list_of_served_models_must_be_readable()
    {
        // Injoignable, ou hors contrat (un alias qui n'est pas un texte, une clé en double) : le même message, pas une
        // exception de lecture JSON, ni « la dernière valeur l'emporte ».
        // La réponse est lue en JSON strict (JsonText), comme un fichier, même là où rien n'est lu : « nomic » y est servi.
        AssertUnreadable("http://127.0.0.1:9/");
        static string Served(string x) => $$"""{"embedding": [{"alias": "nomic", "x": {{x}}}], "generation": []}""";
        foreach (var models in new[]
                 {
                     """{"embedding": [{"alias": 1}], "generation": []}""",
                     """{"embedding": [{"alias": "a"}], "embedding": [{"alias": "nomic"}], "generation": []}""",
                     Served("""{"b": 1, "b": 2}"""),
                     Served(new string('1', 4301)),
                     Served("\"\\ud800\""),
                 })
        {
            using var listener = TestPorts.Listener();
            var served = AnswerOnce(listener, models);
            AssertUnreadable(listener.Prefixes.Single());
            await served;   // la réponse hors contrat a bien été lue
        }
        using (var listener = TestPorts.Listener())
        {
            var served = AnswerOnce(listener, Served("1"));
            AiService.RequireModel(listener.Prefixes.Single(), "embedding", "nomic");   // sans défaut : accepté
            await served;
        }
        // Les octets, lus comme toute réponse du service IA (docs/contrat-http.md) : une marque d'ordre des octets est
        // acceptée ; un octet qui n'est pas de l'UTF-8 (latin-1) rend la réponse hors contrat (il était remplacé).
        using (var listener = TestPorts.Listener())
        {
            var served = AnswerOnce(listener, "\uFEFF" + Served("1"));
            AiService.RequireModel(listener.Prefixes.Single(), "embedding", "nomic");
            await served;
        }
        using (var listener = TestPorts.Listener())
        {
            var served = AnswerOnce(listener, Encoding.Latin1.GetBytes(Served("\"é\"")));
            AssertUnreadable(listener.Prefixes.Single());
            await served;
        }

        static void AssertUnreadable(string baseUrl)
        {
            var error = Assert.Throws<Application.AiServiceException>(() => AiService.RequireModel(baseUrl, "embedding", "nomic"));
            Assert.Equal($"le service IA ne donne pas la liste de ses modèles ({baseUrl.TrimEnd('/')}/v1/models)", error.Message);
            Assert.False(error.Transient);
        }
    }

    [Fact]
    public async Task An_unreachable_or_failing_ai_service_is_said_with_its_address()
    {
        // Deux messages : injoignable (« Lancez-le d'abord »), ou joignable mais en erreur, avec l'adresse interrogée
        // (une barre finale ne donne plus « //health ») ; joignable, le corps n'est pas interprété (« ok » n'est pas du
        // JSON).
        // Adresse invalide ou autre protocole que HTTP : le même message (« Invalid URI » en anglais, ou une trace, avant).
        // Un dossier qui contient un fichier « health » aussi : une adresse file:// n'est pas le service IA.
        var folder = Directory.CreateTempSubdirectory("fyc-health-");
        try
        {
            File.WriteAllText(Path.Combine(folder.FullName, "health"), "ok");
            foreach (var baseUrl in new[] { "http://127.0.0.1:9", "http://127.0.0.1:99999", "http://[::1", "ftp://127.0.0.1:9", "file:///c:/x",
                                            new Uri(folder.FullName).AbsoluteUri })
            {
                var unreachable = Assert.Throws<Application.AiServiceException>(() => AiService.Require(baseUrl));
                Assert.Equal(($"service IA injoignable ({baseUrl}). Lancez-le d'abord : python -m ai_service", false),
                             (unreachable.Message, unreachable.Transient));
            }
        }
        finally
        {
            folder.Delete(true);
        }
        using var listener = TestPorts.Listener();
        var url = listener.Prefixes.Single();   // avec sa barre finale
        var served = AnswerOnce(listener, """{"error": "en panne"}""", status: 500);
        var failing = Assert.Throws<Application.AiServiceException>(() => AiService.Require(url));
        Assert.Equal(($"le service IA répond HTTP 500 sur {url.TrimEnd('/')}/health", false), (failing.Message, failing.Transient));
        await served;
        served = AnswerOnce(listener, "ok");
        AiService.Require(url);   // joignable : le corps est lu, sans être interprété
        await served;
    }

    [Fact]
    public async Task A_health_answer_cut_short_is_unreachable()
    {
        // Le service annonce son corps, en envoie le début, puis coupe : injoignable, d'un 200 comme d'une erreur, car
        // GetAsync lit tout le corps avant de rendre la réponse. Le service qui se tait n'est pas testé ici : le délai
        // (5 s) ne se raccourcit pas.
        foreach (var status in new[] { 200, 500 })
        {
            using var listener = TestPorts.Listener();
            var baseUrl = listener.Prefixes.Single().TrimEnd('/');
            var served = AnswerCut(listener, status);
            var unreachable = Assert.Throws<Application.AiServiceException>(() => AiService.Require(baseUrl));
            Assert.Equal(($"service IA injoignable ({baseUrl}). Lancez-le d'abord : python -m ai_service", false),
                         (unreachable.Message, unreachable.Transient));
            await served;
        }
    }

    /// <summary>À la première requête reçue : ce statut, 100 octets annoncés, un seul envoyé, puis la connexion coupée.</summary>
    private static Task AnswerCut(HttpListener listener, int status) => Task.Run(async () =>
    {
        var context = await listener.GetContextAsync();
        context.Response.StatusCode = status;
        context.Response.ContentLength64 = 100;
        await context.Response.OutputStream.WriteAsync("{"u8.ToArray());
        await context.Response.OutputStream.FlushAsync();
        context.Response.Abort();
    });

    [Fact]
    public async Task An_empty_list_of_served_models_says_aucun()
    {
        // « nomic » est servi, mais pour la génération.
        using var listener = TestPorts.Listener();
        var served = AnswerOnce(listener, """{"embedding": [], "generation": [{"alias": "nomic"}]}""");
        var error = Assert.Throws<ArgumentException>(() => AiService.RequireModel(listener.Prefixes.Single(), "embedding", "nomic"));
        Assert.Equal("le service IA ne sert pas « nomic » comme modèle d'embeddings (servis : aucun)", error.Message);
        await served;
    }

    [Fact]
    public void The_mean_length_of_the_answers_counts_code_points()
    {
        // prompt-v2 : en caractères (points de code), et non en unités UTF-16 : trois emoji comptent pour 3, pas pour 6.
        // Les seules réponses données, moyenne arrondie à égalité vers le pair ; null sans réponse donnée.
        static Application.Snapshot Make(params (string Status, string Text)[] entries) => new("s", "2026-09-12T00:00:00+00:00",
            new Dictionary<string, object?>(),
            entries.Select((e, i) => new Application.SnapshotEntry($"q{i}", "alice", "?", e.Status, Array.Empty<string>(), e.Text, 1)).ToList());
        Assert.Equal(2, Experiments.MeanLength(Make(("answered", "\U0001F600\U0001F600\U0001F600"), ("answered", "a"), ("no_relevant_source", "un refus"))));
        Assert.Equal(2, Experiments.MeanLength(Make(("answered", "ab"), ("answered", "abc"))));   // 2,5 → 2
        Assert.Null(Experiments.MeanLength(Make(("no_relevant_source", "un refus"))));
    }

    [Fact]
    public void Evaluation_questions_load_with_their_expectations()
    {
        var questions = EvalQuestions.Load(Path.Combine(AppConfig.ProjectRoot, "eval", "questions.json"));
        Assert.True(questions.Count >= 20);
        Assert.Contains(questions, q => !q.Answerable);
        Assert.Contains(questions, q => q.ForbiddenDocuments.Count > 0);
        Assert.All(questions.Where(q => q.Answerable), q => Assert.NotEmpty(q.ExpectedDocuments));
        foreach (var path in Directory.GetFiles(Path.Combine(AppConfig.ProjectRoot, "eval"), "questions*.json"))
        {
            Assert.NotEmpty(EvalQuestions.Load(path));   // tous les jeux livrés passent la lecture stricte
        }
    }

    private const string KnownKeys = "answerable, expected_documents, expected_keywords, forbidden_documents, id, question, user";
    private const string NotText = "chaîne qui n'est pas du texte : surrogate UTF-16 isolé (\\ud800 à \\udfff sans sa paire)";

    /// <summary>Jeux mal formés et ce que dit le message.</summary>
    public static readonly TheoryData<string, string> Malformed = new()
    {
        { "[1, 2]", "un objet JSON avec une liste « questions » est attendu" },
        { """{"questions": {}}""", "un objet JSON avec une liste « questions » est attendu" },
        { """{"questions": [1]}""", "question n° 1 : un objet JSON est attendu" },
        { """{"questions": [{"id": 1, "question": "?"}]}""", "question n° 1 : champ « id » manquant ou non textuel" },
        { """{"questions": [{"id": "a", "question": "?"}, {"id": "b"}]}""", "question n° 2 : champ « question » manquant ou non textuel" },
        { """{"questions": [{"id": "a", "question": 12}]}""", "question n° 1 : champ « question » manquant ou non textuel" },
        { """{"questions": [{"id": "a", "question": "?", "user": 3}]}""", "question n° 1 : « user » doit être un texte" },
        { """{"questions": [{"id": "a", "question": "?", "answerable": "false"}]}""", "question n° 1 : « answerable » doit valoir true ou false" },
        { """{"questions": [{"id": "a", "question": "?", "expected_documents": "teletravail"}]}""",
          "question n° 1 : « expected_documents » doit être une liste de textes" },
        { """{"questions": [{"id": "a", "question": "?", "expected_keywords": [1]}]}""", "question n° 1 : « expected_keywords » doit être une liste de textes" },
        { """{"questions": [{"id": "a", "question": "?", "expected_document": ["x"]}]}""",
          $"question n° 1 : clé(s) inconnue(s) [expected_document] (connues : {KnownKeys})" },
        { """{"questions": [{"id": "a", "id": "b", "question": "?"}]}""", "clé « id » en double" },
        { """{"questions": [{"id": {"x": 1, "x": 2}, "id": "a", "question": "?"}]}""", "clé « x » en double" },   // l'objet intérieur d'abord
        { """{"questions": [{"id": "a", "question": "?"}, {"id": "a", "question": "?"}]}""", "question n° 2 : identifiant « a » déjà utilisé" },
        { "{\"x\": " + Bytes.Nested(64) + ", \"questions\": []}", "JSON trop imbriqué : plus de 64 niveaux" },
        { Bytes.Deep, "JSON trop imbriqué : plus de 64 niveaux" },
        // « \ud800 » isolé : du JSON, mais pas du texte (GetString levait une InvalidOperationException, une trace).
        // Partout, commentaire compris. La lecture s'arrête au premier défaut rencontré, imbrication comprise.
        { """{"questions": [{"id": "a", "question": "\ud800"}]}""", NotText },
        { """{"questions": [{"id": "a", "question": "?", "user": "\udc00"}]}""", NotText },
        { """{"questions": [{"id": "\ud800", "question": "?"}]}""", NotText },
        { """{"questions": [{"id": "a", "question": "?", "expected_documents": ["\ud83d"]}]}""", NotText },
        { """{"questions": [{"id": "a", "question": "?", "\ud800": 1}]}""", NotText },
        { """{"questions": [{"id": "a", "question": "?", "\ud800": 1, "\ud800": 2}]}""", NotText },   // en double, mais pas du texte
        { """{"questions": [{"id": "a", "question": "?", "_note": "\ud800"}]}""", NotText },
        { """{"questions": [{"id": "\ud800", "question": "?", "_x": """ + Bytes.Nested(70) + "}]}", NotText },
        { """{"questions": [{"_x": """ + Bytes.Nested(70) + """, "id": "\ud800", "question": "?"}]}""", "JSON trop imbriqué : plus de 64 niveaux" },
        { """{"questions": [{"id": "\ud800", "question": "?", "_x": NaN}]}""", NotText },
        { """{"questions": [{"id": "\ud800", "question": "?", "_n": """ + new string('1', 4301) + "}]}", NotText },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Malformed_question_sets_name_the_file_and_the_field(string content, string detail)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("fyc-questions-").FullName, "questions.json");
        try
        {
            File.WriteAllText(path, content);
            var error = Assert.Throws<FormatException>(() => EvalQuestions.Load(path));
            Assert.Equal($"{path} : jeu de questions mal formé ({detail})", error.Message);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Theory]
    [InlineData("{pas json", "jeu de questions mal formé (JSON invalide : ")]
    [InlineData("""{"questions": [{"id": "a", "question": "?"},]}""", "jeu de questions mal formé (JSON invalide : ")]   // virgule finale
    [InlineData("""{"x": NaN, "questions": [{"id": "a", "question": "?"}]}""", "jeu de questions mal formé (JSON invalide : ")]   // NaN
    [InlineData("""{"questions": []}""", "aucune question dans ")]
    public void Unreadable_or_empty_question_sets_name_the_file(string content, string start)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("fyc-questions-").FullName, "questions.json");
        try
        {
            File.WriteAllText(path, content);
            var error = Assert.Throws<FormatException>(() => EvalQuestions.Load(path));
            Assert.Contains(path, error.Message);
            Assert.Contains(start, error.Message);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void A_question_set_that_is_not_utf8_names_the_byte_and_its_position()
    {
        // File.ReadAllText aurait lu « Cong�s » sans rien dire. Avec ou sans marque d'ordre des octets, la position se
        // compte après elle.
        using var dir = new TempDir();
        const string content = """{"questions": [{"id": "a", "question": "Congés ?"}]}""";
        foreach (var bytes in new[] { Encoding.Latin1.GetBytes(content), Bytes.WithBom(Encoding.Latin1.GetBytes(content)) })
        {
            File.WriteAllBytes(dir.File("q.json"), bytes);
            Assert.Equal($"{dir.File("q.json")} : jeu de questions mal formé (pas en UTF-8 (octet 0xe9 à la position {content.IndexOf('é')}))",
                         Assert.Throws<FormatException>(() => EvalQuestions.Load(dir.File("q.json"))).Message);
        }
        File.WriteAllBytes(dir.File("q.json"), Bytes.WithBom(Encoding.UTF8.GetBytes(content)));
        Assert.Equal("Congés ?", Assert.Single(EvalQuestions.Load(dir.File("q.json"))).Question);
    }

    [Fact]
    public void A_key_that_starts_with_an_underscore_is_a_comment()
    {
        // Comme dans la configuration JSON : « _note » se lit sans effet ; les autres clés inconnues restent refusées.
        using var dir = new TempDir();
        File.WriteAllText(dir.File("q.json"), """{"questions": [{"id": "a", "_note": "à revoir", "question": "?", "_x": [1]}]}""");
        Assert.Equal("a", Assert.Single(EvalQuestions.Load(dir.File("q.json"))).Id);
        File.WriteAllText(dir.File("q.json"), """{"questions": [{"id": "a", "note": "à revoir", "question": "?"}]}""");
        Assert.Contains("clé(s) inconnue(s) [note]", Assert.Throws<FormatException>(() => EvalQuestions.Load(dir.File("q.json"))).Message);
    }

    [Fact]
    public void Without_out_the_folder_is_dated_and_shown_from_the_current_folder()
    {
        // Sans --out (banc, expériences) : sous la racine du projet, d'où qu'on lance la commande ; depuis la racine,
        // « eval/resultats/… », daté à la seconde (AAAAMMJJ-HHMMSS).
        // Depuis un autre dossier, un chemin relatif qui y mène.
        var now = new DateTime(2026, 10, 1, 16, 41, 52);
        Assert.Equal("eval/resultats/20261001-164152", AppConfig.ResultsDir("", now, from: AppConfig.ProjectRoot).Replace('\\', '/'));
        Assert.Equal("eval/resultats/exp-prompt-v2-20261001-164152",
                     AppConfig.ResultsDir("exp-prompt-v2-", now, from: AppConfig.ProjectRoot).Replace('\\', '/'));
        Assert.Equal("../../eval/resultats/20261001-164152",
                     AppConfig.ResultsDir("", now, from: Path.Combine(AppConfig.ProjectRoot, "src", "Assistant.Cli")).Replace('\\', '/'));
        Assert.Equal(Path.Combine(AppConfig.ProjectRoot, "eval", "resultats", "20261001-164152"), Path.GetFullPath(AppConfig.ResultsDir("", now)));
        // Depuis un autre lecteur (Windows), où aucun chemin relatif ne mène : le chemin entier. GetRelativePath ne lit
        // pas le disque : ce lecteur peut ne pas exister.
        if (OperatingSystem.IsWindows())
        {
            var other = new[] { @"Z:\", @"Y:\", @"X:\" }.First(d => !AppConfig.ProjectRoot.StartsWith(d[..2], StringComparison.OrdinalIgnoreCase));
            Assert.Equal(Path.GetFullPath(Path.Combine(AppConfig.ProjectRoot, "eval", "resultats", "20261001-164152")),
                         AppConfig.ResultsDir("", now, from: other));
        }
    }

    [Fact]
    public void A_folder_that_already_exists_gets_a_suffix()
    {
        // Daté à la seconde, le dossier par défaut peut déjà exister : deux bancs lancés dans la même seconde écrivaient
        // dans le même dossier (le second écrasait le rapport du premier, ou échouait sur resultats.csv, encore ouvert par
        // l'autre). Le suivant reçoit « -2 », puis « -3 »… ; un fichier de ce nom compte aussi.
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "eval", "resultats", "20261001-164152");   // dossiers parents absents : créés
        Assert.Equal(path, AppConfig.CreateNewDir(path));
        Assert.Equal(path + "-2", AppConfig.CreateNewDir(path));
        File.WriteAllText(path + "-3", "");
        Assert.Equal(path + "-4", AppConfig.CreateNewDir(path));
        Assert.Equal(new[] { "20261001-164152", "20261001-164152-2", "20261001-164152-4" },
                     Directory.GetDirectories(Path.GetDirectoryName(path)!).Select(d => Path.GetFileName(d)).Order());
    }

    [Fact]
    public void A_parent_that_is_not_a_folder_is_an_error()
    {
        // eval/resultats existe, mais c'est un fichier : l'erreur remonte (IOException), et rien n'est créé ; ce parent
        // ne doit pas passer pour un nom déjà pris (« -2 », « -3 »… sans fin).
        using var dir = new TempDir();
        var parent = dir.File("resultats");
        File.WriteAllText(parent, "");
        Assert.ThrowsAny<IOException>(() => AppConfig.CreateNewDir(Path.Combine(parent, "20261001-164152")));
        Assert.Equal(new[] { parent }, Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public void Folders_created_at_the_same_instant_are_all_different()
    {
        // Directory.CreateDirectory réussit aussi sur un dossier qui existe : vérifier puis créer, sans verrou, pouvait
        // donner le même dossier à deux processus. Huit fils lancés ensemble reçoivent huit dossiers différents.
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "20261001-164152");
        using var start = new Barrier(8);
        var created = new string[8];
        var failures = new Exception?[8];
        var threads = Enumerable.Range(0, 8).Select(i => new Thread(() =>
        {
            try
            {
                start.SignalAndWait();
                created[i] = AppConfig.CreateNewDir(path);
            }
            catch (Exception error)   // toute exception : sortie du fil, elle arrêterait l'hôte de test
            {
                failures[i] = error;
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.All(failures, failure => Assert.Null(failure));
        Assert.Equal(new[] { "20261001-164152" }.Concat(Enumerable.Range(2, 7).Select(n => $"20261001-164152-{n}")).Order(),
                     created.Select(d => Path.GetFileName(d)).Order());
    }

    [Fact]
    public void A_complete_surrogate_pair_and_an_integer_of_4300_digits_are_read()
    {
        // Un emoji écrit en paire (« \ud83d\ude00 ») est du texte, contrairement aux demi-paires de Malformed ; un entier,
        // même de milliers de chiffres, se lit.
        using var dir = new TempDir();
        File.WriteAllText(dir.File("q.json"), """{"questions": [{"id": "a", "question": "\ud83d\ude00 ?", "_n": -""" + new string('9', 4300)
                                              + ", \"_m\": " + new string('1', 5000) + "}]}");
        Assert.Equal("\U0001F600 ?", Assert.Single(EvalQuestions.Load(dir.File("q.json"))).Question);
    }

    [Fact]
    public void Limit_keeps_the_first_questions_and_refuses_zero_or_less()
    {
        var questions = Enumerable.Range(0, 3)
            .Select(i => new EvalQuestion($"{i}", "alice", "?", Array.Empty<string>(), Array.Empty<string>(), true, Array.Empty<string>())).ToList();
        Assert.Equal(questions, EvalQuestions.Limit(questions, null));
        Assert.Equal(new[] { "0", "1" }, EvalQuestions.Limit(questions, 2).Select(q => q.Id));
        foreach (var limit in new[] { 0, -1 })
        {
            Assert.Equal($"--limit doit valoir au moins 1, pas {limit}", Assert.Throws<ArgumentException>(() => EvalQuestions.Limit(questions, limit)).Message);
        }
    }

    [Fact]
    public void Multi_valued_options_stop_at_the_next_option()
    {
        var args = new Args(new[] { "benchmark", "--embedding", "hashing", "nomic", "--generation", "extractive", "--runs", "2", "--json" });
        Assert.Equal(new[] { "hashing", "nomic" }, args.Values("--embedding"));
        Assert.Equal(new[] { "extractive" }, args.Values("--generation"));
        Assert.Equal("2", args.Value("--runs"));
        Assert.True(args.Has("--json"));
        Assert.Equal(new[] { "benchmark" }, args.Positional);
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
            var exp = new Experiment("test", config, questions, dir.FullName, "questions", "config", _ => { });
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
            Assert.Equal(1.0, stats["fuites d'accès"]);   // q3 cite un document interdit : le banc doit le voir
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

/// <summary>
/// Le déroulé du banc et des expériences, de bout en bout contre <see cref="FakeAiService"/> : options
/// vérifiées avant tout travail, recherche par le cas d'usage (droits compris), lignes écrites au fil de
/// l'eau, seuil `default` signalé. Ils lisent la console : collection « Console », comme CliTests.
/// </summary>
[Collection("Console")]
public sealed class BenchmarkRunTests : IDisposable
{
    private const string Salary = "Quelle est la fourchette de salaire d'un consultant senior ?";
    /// <summary>Le prompt « nexiste » : son nom, le dossier et les prompts livrés.</summary>
    private static readonly string NoPrompt = $"prompt introuvable : nexiste dans {Composition.PromptsDir} (connus : answer, answer-v2)";
    private readonly FakeAiService _ai = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("fyc-banc-").FullName;
    private readonly AppConfig _config;
    private readonly string _questionsPath;

    public BenchmarkRunTests()
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
        // Seuil `default` seul : aucun alias n'a le sien. Délai court : un faux service muet ferait échouer vite.
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

    private List<EvalQuestion> Questions => EvalQuestions.Load(_questionsPath);

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
        // Le message entier : sans « (Parameter 'maxChars') ».
        Assert.Equal("max_chars doit valoir au moins 100", Refused<ArgumentException>(Options("jamais") with { SplitterMaxChars = 50 }).Message);
        Assert.Equal("overlap_chars doit être compris entre 0 et max_chars / 2",
                     Refused<ArgumentException>(Options("jamais") with { SplitterOverlapChars = 900 }).Message);
        Assert.Equal(NoPrompt, Refused<Application.PromptNotFoundException>(Options("jamais") with { PromptName = "nexiste" }).Message);
        var intruder = new[] { Questions[0] with { UserName = "mallory" } };
        Assert.Contains("mallory", Refused<UnknownUserException>(Options("jamais", intruder)).Message);
        Assert.Contains("mallory", Refused<UnknownUserException>(Options("jamais") with { Validation = intruder }).Message);
    }

    [Fact]
    public void The_benchmark_writes_each_row_as_soon_as_it_is_known()
    {
        var options = Options("banc") with { Validation = Questions.Take(1).ToList() };
        var written = new List<int>();
        var summary = Benchmark.Run(_config, options, message =>
        {
            if (message.Contains("passage 1/1 terminé"))
            {
                // Le fichier est encore ouvert par le banc : on le lit sans le bloquer.
                using var stream = new FileStream(Path.Combine(options.OutDir, "resultats.csv"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                written.Add(reader.ReadToEnd().Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
            }
        });
        Assert.Equal(new[] { 1 + options.Questions.Count }, written);   // en-tête + une ligne par réponse, avant la fin
        // synthese.json : la sortie de status --json (Presenter.ToJson), suivie d'une fin de ligne.
        var synthesis = File.ReadAllText(Path.Combine(options.OutDir, "synthese.json"));
        Assert.Equal(Presenter.ToJson(summary.ToJson()) + Environment.NewLine, synthesis);
        // L'en-tête du rapport donne le découpage en JSON.
        Assert.Contains("découpage `{\"max_chars\":", File.ReadAllText(Path.Combine(options.OutDir, "rapport.md")));
        var retrieval = Assert.IsType<RetrievalSummary>(Assert.Single(summary.Retrieval));
        Assert.Equal(1, retrieval.Validation!.Questions);
        // Colonnes status (6e) et generation_model_id (9e) : aucune ne contient de virgule.
        Assert.Equal(new[] { "faux:extractive" }, File.ReadLines(Path.Combine(options.OutDir, "resultats.csv")).Skip(1)
                                                      .Select(l => l.Split(',')).Where(f => f[5] == "answered").Select(f => f[8]).Distinct());
    }

    [Fact]
    public void The_benchmark_searches_with_the_rights_of_each_question()
    {
        // La recherche du banc passe par SearchPassages (droits, contrôle du modèle). Même question, même
        // attente : seul bruno a le droit de lire la grille des salaires.
        var questions = new[]
        {
            new EvalQuestion("rh", "bruno", Salary, new[] { "grille-salaires" }, Array.Empty<string>(), true, Array.Empty<string>()),
            new EvalQuestion("sans-droit", "alice", Salary, new[] { "grille-salaires" }, Array.Empty<string>(), true, Array.Empty<string>()),
        };
        var summary = Benchmark.Run(_config, Options("banc-droits", questions), _ => { });
        var retrieval = Assert.IsType<RetrievalSummary>(Assert.Single(summary.Retrieval));
        Assert.Equal((0.5, 0.5), (retrieval.HitAt1, retrieval.HitAtK));
        Assert.Equal(0, Assert.Single(summary.Generation).ForbiddenLeaks);
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
        var calibrated = _config with { MinScores = new Dictionary<string, double> { ["hashing"] = 0.15 } };
        retrieval = Assert.IsType<RetrievalSummary>(Assert.Single(Benchmark.Run(calibrated, options, _ => { }).Retrieval));
        Assert.False(retrieval.ConfiguredThresholdIsDefault);
        Assert.DoesNotContain("(default)", File.ReadAllText(Path.Combine(options.OutDir, "rapport.md")));
    }

    private int Experiment(string name, params string[] options) => Experiment(_config, new Overrides(), name, options);

    private int Experiment(AppConfig config, Overrides common, string name, params string[] options) =>
        Experiments.Run(name, new Args(new[] { "experience", name, "--questions", _questionsPath }.Concat(options).ToArray()),
                        config, common, "config", _ => { });

    [Fact]
    public void Experiment_options_are_refused_before_any_work()
    {
        var outDir = Path.Combine(_dir, "exp");
        Assert.Equal("max_chars doit valoir au moins 100",
                     Assert.Throws<ArgumentException>(() => Experiment("cace-decoupage", "--max-chars", "50", "--out", outDir)).Message);
        Assert.Equal(NoPrompt, Assert.Throws<Application.PromptNotFoundException>(
            () => Experiment("prompt-v2", "--other", "nexiste", "--out", outDir)).Message);
        Assert.Equal("--limit doit valoir au moins 1, pas 0",
                     Assert.Throws<ArgumentException>(() => Experiment("stabilite", "--limit", "0", "--out", outDir)).Message);
        File.WriteAllText(_questionsPath, """{"questions": [{"id": "q", "user": "mallory", "question": "?"}]}""");
        Assert.Contains("mallory", Assert.Throws<UnknownUserException>(() => Experiment("stabilite", "--out", outDir)).Message);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void The_main_configuration_of_an_experiment_is_checked_before_any_work()
    {
        // Pas seulement ce que l'expérience change : le prompt principal, celui de la configuration ou de --prompt.
        var outDir = Path.Combine(_dir, "exp");
        Assert.Equal(NoPrompt, Assert.Throws<Application.PromptNotFoundException>(
            () => Experiment(_config with { PromptName = "nexiste" }, new Overrides(), "stabilite", "--out", outDir)).Message);
        Assert.Equal(NoPrompt, Assert.Throws<Application.PromptNotFoundException>(
            () => Experiment(_config, new Overrides(PromptName: "nexiste"), "stabilite", "--out", outDir)).Message);
        Assert.False(Directory.Exists(outDir));
    }

    /// <summary>
    /// Le résultat de l'action, et ce qu'elle a écrit en console (sortie, erreurs), avec des « \n » ; avec
    /// <paramref name="raw"/>, tel quel (les fins de ligne de WriteLine sont alors celles de la plateforme).
    /// </summary>
    private static (T Result, string Out, string Err) Captured<T>(Func<T> action, bool raw = false)
    {
        var (previousOut, previousError) = (Console.Out, Console.Error);
        var (output, errors) = (new StringWriter(), new StringWriter());
        Console.SetOut(output);
        Console.SetError(errors);
        try
        {
            var result = action();
            string Text(StringWriter writer) => raw ? writer.ToString() : writer.ToString().Replace("\r\n", "\n");
            return (result, Text(output), Text(errors));
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    [Fact]
    public void An_experiment_says_when_the_default_threshold_applies()
    {
        // Ni le modèle principal ni --other n'ont de seuil configuré : `default` s'applique, et l'expérience le dit
        // en console et dans le rapport (ADR 0004). Rapport en « \n » (sous Windows aussi) et chemin affiché avec des
        // « / » : la même sortie sous Windows et Linux.
        var outDir = Path.Combine(_dir, "exp-embeddings");
        var (code, output, errors) = Captured(() => Experiment("changement-embeddings", "--other", "hashing-512", "--out", outDir));
        Assert.Equal(0, code);
        var warnings = new[] { "hashing", "hashing-512" }
            .Select(alias => $"Attention : aucun seuil de pertinence configuré pour « {alias} » : valeur `default` 0.15 (ADR 0004 : lancer le banc d'essai)")
            .ToArray();
        Assert.Equal(warnings, errors.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        var report = File.ReadAllText(Path.Combine(outDir, "rapport.md"));
        foreach (var warning in warnings)
        {
            Assert.Contains($"> {warning}", report);
        }
        Assert.Contains("| seuil | 0.15 | 0.15 |", report);
        Assert.DoesNotContain('\r', report);   // le bloc « Détail » de la comparaison compris
        Assert.EndsWith($"\nRapport : {Path.Combine(outDir, "rapport.md").Replace('\\', '/')}\n", output);
    }

    [Theory]
    [InlineData("changement-embeddings", "extractive", "modèle d'embeddings (servis : hashing, hashing-512)")]
    [InlineData("changement-embeddings", "nexiste", "modèle d'embeddings (servis : hashing, hashing-512)")]
    [InlineData("changement-generateur", "hashing", "modèle de génération (servis : extractive)")]
    public void The_other_alias_of_an_experiment_is_checked_before_any_index(string name, string other, string served)
    {
        // --other doit être servi, et du bon type (GET /v1/models) : sinon, ni index ni instantané, et pas
        // d'avertissement avant l'erreur.
        var outDir = Path.Combine(_dir, "exp-autre");
        var (error, output, errors) = Captured(() => Assert.Throws<ArgumentException>(
            () => Experiment(name, "--limit", "1", "--other", other, "--out", outDir)));
        Assert.Equal($"le service IA ne sert pas « {other} » comme {served}", error.Message);
        Assert.Equal(("", ""), (output, errors));
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void The_default_threshold_is_announced_after_the_checks()
    {
        // Une option fausse, ou un service IA injoignable : l'erreur seule, pas d'avertissement avant elle.
        var outDir = Path.Combine(_dir, "exp");
        var (invalid, _, errors) = Captured(() => Assert.Throws<ArgumentException>(
            () => Experiment("cace-decoupage", "--max-chars", "50", "--out", outDir)));
        Assert.Equal("max_chars doit valoir au moins 100", invalid.Message);
        Assert.Equal("", errors);
        var (unreachable, _, stillNothing) = Captured(() => Assert.Throws<Application.AiServiceException>(
            () => Experiment(_config with { AiBaseUrl = "http://127.0.0.1:9" }, new Overrides(), "stabilite", "--out", outDir)));
        Assert.Equal("service IA injoignable (http://127.0.0.1:9). Lancez-le d'abord : python -m ai_service", unreachable.Message);
        Assert.Equal("", stillNothing);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void An_experiment_prints_its_steps_and_the_path_of_its_report()
    {
        // La console : « Avant : 800 / 120 », une ligne par index et par instantané, « Rapport : » et son chemin. Elle
        // n'affichait ni les unes ni l'autre. Valeurs fixées : trois documents d'un paragraphe, donc 3 morceaux avant
        // comme après ; la dimension du faux service, un axe par mot du vocabulaire plus l'axe constant (FakeAiService).
        var outDir = Path.Combine(_dir, "exp-cace");
        var (code, raw, _) = Captured(() => Experiment("cace-decoupage", "--limit", "1", "--out", outDir), raw: true);
        Assert.Equal(0, code);
        // Les fins de ligne de la plateforme : sous Windows, aucun « \n » seul (la ligne vide avant « Rapport : » en était un).
        Assert.DoesNotContain("\n", raw.Replace(Environment.NewLine, ""));
        var output = raw.Replace(Environment.NewLine, "\n");
        Assert.Contains("Avant : 800 / 120\n", output);
        Assert.Contains("Après : 300 / 50\n", output);
        foreach (var name in new[] { "avant", "apres" })
        {
            Assert.Contains($"  index « {name} » : 3 morceaux, faux:hashing, {Fakes.Vocabulary.Length + 1} dim., ", output);
            Assert.Contains($"  instantané « {name} » : 1 réponses en ", output);
        }
        Assert.EndsWith($"\nRapport : {Path.Combine(outDir, "rapport.md").Replace('\\', '/')}\n", output);
    }

    [Fact]
    public void Stability_writes_its_mean_drift_in_the_report()
    {
        var outDir = Path.Combine(_dir, "exp-stabilite");
        Assert.Equal(0, Captured(() => Experiment("stabilite", "--runs", "2", "--out", outDir)).Result);
        // Générateur déterministe : aucune dérive, écrite « 0 » (format « 0.### »).
        Assert.Contains("**Dérive moyenne à configuration constante : 0** (0 changement(s) de statut sur 1 comparaison(s)).",
                        File.ReadAllText(Path.Combine(outDir, "rapport.md")));
    }

    [Fact]
    public void The_benchmark_output_has_bare_numbers_slashes_and_lf_only()
    {
        // Seuil configuré 0 et seuil imposé 1 : « 0 » et « 1 » dans le journal ; chemin du rapport avec des « / » ;
        // rapport en « \n », lecture du seuil suggéré comprise.
        var lines = new List<string>();
        var options = Options("banc-octets") with { MinScoreMode = "1", Validation = Questions.Take(1).ToList() };
        Benchmark.Run(_config with { MinScores = new Dictionary<string, double> { ["hashing"] = 0.0 } }, options, lines.Add);
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
        // Sans --out, le dossier est daté à la seconde : un second banc lancé dans la même seconde écrivait dans le même
        // dossier. Il reçoit « -2 » (NewOutDir), et tous ses fichiers y vont. --out, lui, reste le dossier donné, même
        // s'il existe déjà (la commande : CliTests.cs).
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
        Benchmark.Run(_config, Options(name), _ => { });
        Assert.Contains("\n2 questions · ", File.ReadAllText(Path.Combine(path, "rapport.md")));
        Assert.False(Path.Exists(path + "-3"));
    }

    [Fact]
    public void Two_experiments_of_the_same_second_keep_their_own_folder()
    {
        // Comme le banc : le second dossier reçoit « -2 » (newOutDir), et ses index le suivent. --out, lui, reste le dossier
        // donné, même s'il existe déjà.
        var path = Path.Combine(_dir, "resultats", "exp-stabilite-20261001-164152");
        var (experiments, _, _) = Captured(() =>
        {
            var made = new[] { 1, 2 }.Select(i => new Experiment("stabilite", _config, Questions, path, "questions", "config", _ => { }, newOutDir: true))
                                     .ToList();
            made.ForEach(e => e.Index("partage"));
            return made;
        });
        Assert.Equal(new[] { path, path + "-2" }, experiments.Select(e => e.OutDir));
        Assert.All(experiments, e => Assert.True(File.Exists(Path.Combine(e.OutDir, "index-partage.json"))));
        var (code, output, _) = Captured(() => Experiment("stabilite", "--runs", "2", "--out", path));
        Assert.Equal(0, code);
        Assert.EndsWith($"\nRapport : {Path.Combine(path, "rapport.md").Replace('\\', '/')}\n", output);
        Assert.False(Path.Exists(path + "-3"));
    }

    [Fact]
    public void The_benchmark_uses_the_prompt_it_is_given()
    {
        // --prompt : le rapport nomme sa version.
        var options = Options("banc-prompt") with { PromptName = "answer-v2" };
        Benchmark.Run(_config, options, _ => { });
        var prompts = Composition.Build(_config, new Overrides()).Prompts;
        Assert.NotEqual(prompts.Get("answer").Version, prompts.Get("answer-v2").Version);
        Assert.Contains($"prompt `{prompts.Get("answer-v2").Version}`", File.ReadAllText(Path.Combine(options.OutDir, "rapport.md")));
    }
}
