// Adaptateurs, décorateurs et contrat HTTP (contre un faux service). La règle de dépendance : ArchitectureTests.cs.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Application;
using Assistant.Cli;
using Assistant.Domain;
using Assistant.Infrastructure;
using Xunit;

namespace Assistant.Tests;

public class MarkdownCorpusTests
{
    [Fact]
    public void Parses_header_and_groups()
    {
        var doc = MarkdownCorpus.Parse("---\nid: rh-1\ntitre: Grille des salaires\ngroupes: rh, direction\n---\n# Grille\nTexte.");
        Assert.Equal("rh-1", doc.Id);
        Assert.Equal("Grille des salaires", doc.Title);
        Assert.Equal(new HashSet<string> { "rh", "direction" }, doc.AllowedGroups);
        Assert.StartsWith("# Grille", doc.Text);
    }

    [Theory]
    [InlineData("id: x")]
    [InlineData("id: x\ngroupes:")]
    [InlineData("id: x\ngroupe: rh")]
    public void Access_groups_are_mandatory(string header) =>
        // Un droit oublié ou mal écrit ne rend jamais un document public en silence.
        Assert.Throws<CorpusFormatException>(() => MarkdownCorpus.Parse($"---\n{header}\n---\nTexte."));

    [Fact]
    public void A_comment_in_the_groups_is_rejected() =>
        // L'exemple commenté d'une ancienne documentation donnait des droits faux.
        Assert.Throws<CorpusFormatException>(() => MarkdownCorpus.Parse("---\nid: x\ngroupes: rh  # ou : rh, direction\n---\nTexte."));

    [Fact]
    public void Id_is_mandatory() =>
        Assert.Throws<CorpusFormatException>(() => MarkdownCorpus.Parse("---\ntitre: Sans id\n---\nTexte."));

    [Fact]
    public void Project_corpora_load()
    {
        var root = AppConfig.ProjectRoot;
        Assert.True(new MarkdownCorpus(Path.Combine(root, "corpus", "solveo")).Load().Count >= 5);
        var real = new MarkdownCorpus(Path.Combine(root, "corpus", "service-public")).Load();
        Assert.True(real.Count >= 300);
        Assert.Equal(new HashSet<string> { "tous", "rh", "direction" }, real.SelectMany(d => d.AllowedGroups).ToHashSet());
    }
}

public class SplitterTests
{
    [Fact]
    public void Short_document_gives_one_chunk_with_title()
    {
        var chunks = new ParagraphSplitter().Split(Fakes.Doc("a", "Un paragraphe court."));
        Assert.Single(chunks);
        Assert.StartsWith("A\n", chunks[0].Text);
        Assert.Equal("a#0", chunks[0].Id);
    }

    [Fact]
    public void Long_document_is_cut_with_overlap()
    {
        // Des paragraphes tous différents : le recouvrement ne peut pas être trouvé par hasard.
        var text = string.Join("\n\n", Enumerable.Range(1, 12).Select(i =>
            $"Paragraphe numéro {i} : " + string.Join(" ", Enumerable.Range(1, 25).Select(j => $"mot{i}x{j}"))));
        var chunks = new ParagraphSplitter(400, 60, includeTitle: false).Split(Fakes.Doc("a", text));
        Assert.True(chunks.Count > 3);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 400 + 60));
        var lastWord = chunks[0].Text.TrimEnd().Split(' ')[^1];        // mot unique du corpus
        Assert.Contains(lastWord, chunks[1].Text[..Math.Min(100, chunks[1].Text.Length)]);   // recouvrement
    }

    [Fact]
    public void Describe_is_recorded_in_the_manifest()
    {
        var describe = new ParagraphSplitter(300, 50).Describe();
        Assert.Equal(300, describe["max_chars"]);
        Assert.Equal("paragraph", describe["type"]);
    }

    [Fact]
    public void The_splitter_turns_crlf_into_lf_and_keeps_a_lone_cr()
    {
        // Les lecteurs de corpus ramènent les fins de ligne à « \n », pas forcément un Document construit dans le
        // code : le découpage le fait aussi. Un « \r » seul reste tel quel.
        static IEnumerable<string> Texts(string text) =>
            new ParagraphSplitter(100, 0, includeTitle: false).Split(Fakes.Doc("a", text)).Select(c => c.Text);

        Assert.Equal(new[] { "Un.\nDeux.\n\nTrois.\rQuatre." }, Texts("Un.\r\nDeux.\r\n\r\nTrois.\rQuatre."));
        // 40 lignes d'un caractère : 79 caractères avec « \n », sous la limite (98) ; 118 avec « \r\n ».
        var lines = Enumerable.Repeat("x", 40).ToList();
        Assert.Equal(new[] { string.Join("\n", lines) }, Texts(string.Join("\r\n", lines)));
    }

    [Fact]
    public void An_emoji_at_a_boundary_is_never_cut_in_two()
    {
        // Les longueurs sont des string.Length : un emoji y compte pour deux unités UTF-16, une paire de substitution.
        // À la limite d'un morceau, ou au début du recouvrement, la coupure passe avant ou après lui, jamais entre ses
        // deux moitiés : coupé là, le texte ne serait plus de l'Unicode valide.
        const string emoji = "\U0001F600";
        static string[] Texts(string text, int overlap) =>
            new ParagraphSplitter(100, overlap, includeTitle: false).Split(Fakes.Doc("a", text)).Select(c => c.Text).ToArray();

        // Sans espace, un paragraphe de plus de 98 unités est coupé après la 98e : l'emoji occupe les 98e et 99e.
        Assert.Equal(new[] { new string('x', 97), emoji + new string('y', 10) },
                     Texts(new string('x', 97) + emoji + new string('y', 10), 0));
        // Le recouvrement reprend les 20 dernières unités du morceau : la première est la seconde moitié de l'emoji.
        var first = new string('a', 50) + emoji + new string('b', 19);
        Assert.Equal(new[] { first, new string('b', 19) + "\n\n" + new string('c', 50) },
                     Texts(first + "\n\n" + new string('c', 50), 20));
    }
}

public class JsonVectorIndexTests
{
    [Fact]
    public void Round_trip_through_the_file()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "index.json");
            var index = new JsonVectorIndex(path);
            var manifest = new IndexManifest("id", "m", 2, "fp", new Dictionary<string, object> { ["type"] = "whole" }, 1, 2, "t");
            var chunks = new[] { new Chunk("a#0", "a", "A", "x", 0, new HashSet<string> { "tous" }), new Chunk("a#1", "a", "A", "y", 1, new HashSet<string> { "rh" }) };
            index.Replace(manifest, chunks, new[] { new[] { 1.0, 0.0 }, new[] { 0.0, 1.0 } });

            var reloaded = new JsonVectorIndex(path);
            Assert.Equal("id", reloaded.Manifest()!.IndexId);
            var hits = reloaded.Search(new[] { 0.9, 0.1 }, 2, c => c.AllowedGroups.Contains("tous"));
            Assert.Single(hits);
            Assert.Equal("a#0", hits[0].Chunk.Id);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    private static readonly IndexManifest Manifest2 =
        new("id", "m", 2, "fp", new Dictionary<string, object> { ["type"] = "whole" }, 1, 2, "t");

    private static readonly Chunk[] TwoChunks =
    {
        new("a#0", "a", "A", "x", 0, new HashSet<string> { "tous" }),
        new("b#0", "b", "B", "y", 0, new HashSet<string> { "rh" }),
    };

    [Fact]
    public void Forbidden_passages_take_no_place_in_the_top_k()
    {
        // Pré-filtrage (ADR 0006) : un passage interdit mieux classé ne masque pas le passage autorisé.
        var index = new InMemoryVectorIndex();
        index.Replace(Manifest2, TwoChunks, new[] { new[] { 1.0, 0.0 }, new[] { 0.0, 1.0 } });
        var hits = index.Search(new[] { 0.0, 1.0 }, 1, c => !c.AllowedGroups.Contains("rh"));
        Assert.Equal("a#0", Assert.Single(hits).Chunk.Id);
    }

    [Fact]
    public void Rejects_a_query_of_the_wrong_dimension()
    {
        var index = new InMemoryVectorIndex();
        index.Replace(Manifest2, TwoChunks, new[] { new[] { 1.0, 0.0 }, new[] { 0.0, 1.0 } });
        Assert.Throws<ArgumentException>(() => index.Search(new[] { 1.0, 0.0, 0.0 }, 2, _ => true));
    }

    [Fact]
    public void An_unreadable_file_is_not_an_absent_index()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "index.json");
            File.WriteAllText(path, "{ pas du json");
            var index = new JsonVectorIndex(path);
            Assert.Throws<IndexUnreadableException>(() => index.Manifest());
            Assert.Throws<IndexUnreadableException>(() => index.Manifest());   // pas « aucun index » la deuxième fois
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void An_index_rebuilt_by_another_process_is_seen()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "index.json");
            new JsonVectorIndex(path).Replace(Manifest2, TwoChunks, new[] { new[] { 1.0, 0.0 }, new[] { 0.0, 1.0 } });
            var server = new JsonVectorIndex(path);   // le processus `serve`
            Assert.Equal("id", server.Manifest()!.IndexId);
            new JsonVectorIndex(path).Replace(Manifest2 with { IndexId = "id-2", ChunkCount = 1 }, TwoChunks[..1], new[] { new[] { 1.0, 0.0 } });
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(1));   // une seconde plus tard
            Assert.Equal("id-2", server.Manifest()!.IndexId);
            Assert.Single(server.Search(new[] { 1.0, 0.0 }, 5, _ => true));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void An_index_rebuilt_with_the_same_size_and_date_is_seen()
    {
        // Même taille, même date : seule la reconstruction (created_at) distingue les deux fichiers.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "index.json");
            var vectors = new[] { new[] { 1.0, 0.0 }, new[] { 0.0, 1.0 } };
            new JsonVectorIndex(path).Replace(Manifest2 with { CreatedAt = "t1" }, TwoChunks, vectors);
            var server = new JsonVectorIndex(path);   // le processus `serve`
            Assert.Equal("t1", server.Manifest()!.CreatedAt);
            var date = File.GetLastWriteTimeUtc(path);
            new JsonVectorIndex(path).Replace(Manifest2 with { CreatedAt = "t2" }, TwoChunks, vectors);
            File.SetLastWriteTimeUtc(path, date);
            Assert.Equal("t2", server.Manifest()!.CreatedAt);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Reads_and_searches_an_index_written_by_the_python_version()
    {
        // Même format JSON : la fixture a été produite par fyc-assistant-rag-python-full (Python, moteur hashing 64 dim.)
        // sur le corpus Solvéo. Le modèle d'embeddings doit correspondre, pas le langage de l'application.
        var index = new JsonVectorIndex(Path.Combine(AppConfig.ProjectRoot, "tests", "Assistant.Tests", "fixtures", "index-python-hashing.json"));
        var manifest = index.Manifest();
        Assert.NotNull(manifest);
        Assert.Equal("hashing-64-stem6", manifest!.EmbeddingModel);
        Assert.Equal(15, manifest.ChunkCount);
        Assert.Equal(800, manifest.Splitter["max_chars"]);
        // Le vecteur du morceau « télétravail » retrouve ce morceau en tête : les vecteurs sont lus et normalisés.
        var teletravail = index.Search(new double[64], 1, _ => true);   // vecteur nul : aucun score, mais aucune erreur
        Assert.Single(teletravail);
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AppConfig.ProjectRoot, "tests", "Assistant.Tests", "fixtures", "index-python-hashing.json")))!;
        var vector = root["vectors"]![0]!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
        var hits = index.Search(vector, 1, _ => true);
        Assert.Equal(root["chunks"]![0]!["id"]!.GetValue<string>(), hits[0].Chunk.Id);
        Assert.InRange(hits[0].Score, 0.999, 1.001);
    }

    [Fact]
    public void Same_corpus_same_model_same_splitter_gives_the_index_id_of_the_python_version()
    {
        // On indexe ici le même corpus Solvéo, avec le même découpage, qu'a indexé la version Python pour
        // produire la fixture : l'identifiant et l'empreinte du corpus doivent être identiques.
        var fixture = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AppConfig.ProjectRoot, "tests", "Assistant.Tests", "fixtures", "index-python-hashing.json")))!["manifest"]!;
        var manifest = new IndexCorpus(new MarkdownCorpus(Path.Combine(AppConfig.ProjectRoot, "corpus", "solveo")), new ParagraphSplitter(800, 120, true),
                                       new FixedModelEmbedder("hashing-64-stem6", 64), new InMemoryVectorIndex(), new FixedClock()).Execute();
        Assert.Equal(fixture["corpus_fingerprint"]!.GetValue<string>(), manifest.CorpusFingerprint);
        Assert.Equal(fixture["index_id"]!.GetValue<string>(), manifest.IndexId);
    }

    [Fact]
    public void Index_id_matches_the_python_formula()
    {
        // Même corpus, même modèle, même découpage → même identifiant que json.dumps(sort_keys=True) en Python.
        var splitter = new Dictionary<string, object> { ["type"] = "paragraph", ["max_chars"] = 800, ["overlap_chars"] = 120, ["include_title"] = true };
        var identity = $"[{Fingerprints.PythonJson("fp")}, {Fingerprints.PythonJson("m")}, {64}, {Fingerprints.PythonJson(splitter)}]";
        Assert.Equal("[\"fp\", \"m\", 64, {\"include_title\": true, \"max_chars\": 800, \"overlap_chars\": 120, \"type\": \"paragraph\"}]", identity);
    }

    /// <summary>Un service qui annonce un modèle et une dimension donnés : seuls ceux-ci entrent dans l'identifiant.</summary>
    private sealed class FixedModelEmbedder(string model, int dimension) : IEmbedder
    {
        public EmbeddingBatch EmbedDocuments(IReadOnlyList<string> texts) =>
            new(model, dimension, texts.Select(_ => Enumerable.Range(0, dimension).Select(i => i == 0 ? 1.0 : 0.0).ToArray()).ToList());

        public EmbeddingBatch EmbedQuery(string text) => EmbedDocuments(new[] { text });
    }
}

public class PromptAndSnapshotFilesTests
{
    [Fact]
    public void Prompt_version_carries_a_fingerprint_of_the_content()
    {
        var template = new FilePromptRepository(Composition.PromptsDir).Get("answer");
        Assert.StartsWith("v1+", template.Version);
        Assert.Contains("{passages}", template.User);
        Assert.Contains("[1]", template.Render("Q ?", "[1] Titre\ntexte"));
    }

    [Fact]
    public void Prompt_version_is_the_same_as_in_the_python_version()
    {
        // Valeurs calculées par prompt_files.py sur assistant/prompts/answer*.toml (même contenu) :
        // l'empreinte porte sur le contenu canonique, pas sur le format du fichier.
        var prompts = new FilePromptRepository(Composition.PromptsDir);
        Assert.Equal("v1+085b70e7", prompts.Get("answer").Version);
        Assert.Equal("v2+1708960b", prompts.Get("answer-v2").Version);
        Assert.Equal("v1+085b70e7"[3..], FilePromptRepository.Fingerprint("v1", prompts.Get("answer").System, prompts.Get("answer").User));
    }

    [Fact]
    public void Prompt_modified_without_changing_its_declared_version_is_detected()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "answer.json");
            File.WriteAllText(path, """{"version": "v1", "system": "a", "user": "{question}"}""");
            var first = new FilePromptRepository(dir.FullName).Get("answer").Version;
            File.WriteAllText(path, """{"version": "v1", "system": "b", "user": "{question}"}""");
            var second = new FilePromptRepository(dir.FullName).Get("answer").Version;
            File.WriteAllText(path, """{"version": "v1", "_commentaire": "sans effet", "system": "a", "user": "{question}"}""");
            var third = new FilePromptRepository(dir.FullName).Get("answer").Version;
            Assert.NotEqual(first, second);   // contenu modifié : détecté
            Assert.Equal(first, third);       // commentaire ou mise en forme : sans effet
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("""{"system": "a", "user": "{question}"}""")]                   // sans version
    [InlineData("""{"version": 2, "system": "a", "user": "{question}"}""")]     // version qui n'est pas un texte
    [InlineData("""["version"]""")]                                             // pas un objet
    public void An_incomplete_prompt_names_the_file(string content)
    {
        // Édité à la main : un champ oublié donne une erreur qui dit quoi corriger, pas une NullReferenceException.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "casse.json"), content);
            var error = Assert.Throws<FormatException>(() => new FilePromptRepository(dir.FullName).Get("casse"));
            Assert.Contains("casse.json", error.Message);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void An_unknown_prompt_names_its_folder_and_the_known_ones()
    {
        // Un nom mal tapé, ou un dossier absent : le nom, le dossier et les prompts connus (« aucun »), pas « Could not
        // find file … ».
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            foreach (var name in new[] { "b", "a-v2", "a" })
            {
                File.WriteAllText(Path.Combine(dir.FullName, $"{name}.json"), """{"version": "v1", "system": "s", "user": "{question}"}""");
            }
            File.WriteAllText(Path.Combine(dir.FullName, "notes.txt"), "pas un prompt");
            Directory.CreateDirectory(Path.Combine(dir.FullName, "dossier.json"));   // un dossier non plus
            var absent = Path.Combine(dir.FullName, "absent");
            foreach (var (directory, known) in new[] { (dir.FullName, "a, a-v2, b"), (absent, "aucun") })
            {
                var error = Assert.Throws<PromptNotFoundException>(() => new FilePromptRepository(directory).Get("answr"));
                Assert.Equal($"prompt introuvable : answr dans {directory} (connus : {known})", error.Message);
            }
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Theory]
    [InlineData("""{"name": "a", "created_at": "t"}""")]                                   // sans entries
    [InlineData("""{"name": "a", "created_at": "t", "entries": [{"status": "answered"}]}""")]
    [InlineData("""["a"]""")]
    public void An_incomplete_snapshot_names_the_file(string content)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "a.json"), content);
            var error = Assert.Throws<FormatException>(() => new JsonSnapshotStore(dir.FullName).Load("a"));
            Assert.Contains("a.json", error.Message);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Snapshot_store_round_trip_and_names()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var store = new JsonSnapshotStore(Path.Combine(dir.FullName, "instantanes"));
            Assert.Empty(store.Names());
            var snapshot = new Snapshot("ref", "2026-09-11T12:00:00+00:00",
                new Dictionary<string, object?> { ["generation_model"] = "extractive", ["top_k"] = 4, ["seed"] = null, ["splitter"] = new Dictionary<string, object> { ["max_chars"] = 800 } },
                new[] { new SnapshotEntry("q1", "alice", "Q ?", "answered", new[] { "a", "b" }, "Texte [1]", 1) });
            store.Save(snapshot);
            var loaded = store.Load("ref");
            Assert.Equal(snapshot.Entries[0] with { CitedDocuments = Array.Empty<string>() }, loaded.Entries[0] with { CitedDocuments = Array.Empty<string>() });
            Assert.Equal(new[] { "a", "b" }, loaded.Entries[0].CitedDocuments);
            Assert.Equal("extractive", loaded.Configuration["generation_model"]);
            Assert.Equal(4, loaded.Configuration["top_k"]);
            Assert.Null(loaded.Configuration["seed"]);
            Assert.Empty(SnapshotComparer.Compare(snapshot, loaded).ConfigurationDifferences);   // relu du disque = construit en mémoire
            Assert.Equal(new[] { "ref" }, store.Names());
            Assert.Throws<SnapshotNotFoundException>(() => store.Load("absent"));
            Assert.Throws<InvalidSnapshotNameException>(() => store.Load("../autre"));
            Assert.Throws<InvalidSnapshotNameException>(() => store.Load("ref\n"));   // « $ » laisserait passer un saut de ligne final
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void An_unknown_snapshot_names_the_known_ones_without_brackets()
    {
        // Les noms connus sans crochets : ni « [b, ref] », ni « [] » pour un dossier vide. Et un nom invalide entre « ».
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var store = new JsonSnapshotStore(Path.Combine(dir.FullName, "instantanes"));
            var snapshot = new Snapshot("ref", "2026-09-11T12:00:00+00:00", new Dictionary<string, object?>(), Array.Empty<SnapshotEntry>());
            foreach (var (saved, known) in new[] { (Array.Empty<string>(), "aucun"), (new[] { "ref", "b" }, "b, ref") })
            {
                foreach (var name in saved)
                {
                    store.Save(snapshot with { Name = name });
                }
                Assert.Equal($"instantané introuvable : absent (connus : {known})",
                             Assert.Throws<SnapshotNotFoundException>(() => store.Load("absent")).Message);
            }
            Assert.Equal("nom d'instantané invalide : « a b » (lettres, chiffres, . _ - ; 64 caractères au plus)",
                         Assert.Throws<InvalidSnapshotNameException>(() => store.Load("a b")).Message);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void A_folder_named_like_a_snapshot_is_not_one()
    {
        // Seuls les fichiers comptent (Directory.GetFiles, File.Exists) : un dossier « dossier.json » n'est pas listé, et
        // il est introuvable.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var store = new JsonSnapshotStore(dir.FullName);
            store.Save(new Snapshot("ref", "2026-09-11T12:00:00+00:00", new Dictionary<string, object?>(), Array.Empty<SnapshotEntry>()));
            Directory.CreateDirectory(Path.Combine(dir.FullName, "dossier.json"));
            Assert.Equal(new[] { "ref" }, store.Names());
            Assert.Equal("instantané introuvable : dossier (connus : ref)",
                         Assert.Throws<SnapshotNotFoundException>(() => store.Load("dossier")).Message);
        }
        finally
        {
            dir.Delete(true);
        }
    }
}

public class DecoratorTests
{
    private static readonly GenerationRequest Request = new("système", "Passages :\n[1] x\n\nQuestion : ?", 0.2, 100);

    private sealed class Flaky : IGenerator
    {
        private readonly int _failures;
        public int Calls { get; private set; }
        public Flaky(int failures) => _failures = failures;

        public Generation Generate(GenerationRequest request)
        {
            Calls++;
            if (Calls <= _failures)
            {
                throw new AiServiceException("injoignable");
            }
            return new Generation("llm", "Deux jours [1].");
        }
    }

    // Découpage partagé : deux manifestes construits ici sont égaux en valeur (record ==), comme deux
    // index reconstruits à l'identique. Le cache doit pourtant voir deux index différents.
    private static readonly Dictionary<string, object> Whole = new() { ["type"] = "whole" };

    private static IndexManifest Manifest(string model = "fake-keywords", int dimension = 8) =>
        new("idx", model, dimension, "empreinte", Whole, 1, 1, "2026-09-21T12:00:00");

    [Fact]
    public void Cache_embeds_the_same_question_once()
    {
        var inner = new KeywordEmbedder();
        var index = Manifest();
        var cached = new CachedEmbedder(inner, () => index);
        var first = cached.EmbedQuery("télétravail");
        Assert.Same(first, cached.EmbedQuery("télétravail"));
        Assert.Single(inner.Calls);
        Assert.Equal((1, 1), (cached.Hits, cached.Misses));
    }

    [Fact]
    public void Cache_never_stores_documents()
    {
        // Une réindexation doit refléter le modèle servi maintenant (S4.2).
        var inner = new KeywordEmbedder();
        var index = Manifest();
        var cached = new CachedEmbedder(inner, () => index);
        cached.EmbedDocuments(new[] { "a", "b" });
        cached.EmbedDocuments(new[] { "a", "b" });
        Assert.Equal(2, inner.Calls.Count);
    }

    [Fact]
    public void A_new_index_empties_the_cache_even_with_the_same_model_name()
    {
        // Préfixes changés, moteur sans empreinte : les vecteurs changent, pas le nom du modèle.
        var inner = new KeywordEmbedder();
        var index = Manifest();
        var cached = new CachedEmbedder(inner, () => index);
        cached.EmbedQuery("télétravail");
        var rebuilt = Manifest();   // réindexé : nouveau manifeste, mêmes valeurs
        Assert.Equal(index, rebuilt);
        index = rebuilt;
        cached.EmbedQuery("télétravail");
        Assert.Equal(2, inner.Calls.Count);
    }

    [Fact]
    public void Cache_does_not_keep_vectors_that_do_not_match_the_index()
    {
        // Sinon, une question posée pendant que le service servait un autre modèle resterait
        // en erreur après le retour du service au modèle de l'index.
        var inner = new KeywordEmbedder("modele-b");
        var index = Manifest("modele-a");
        var cached = new CachedEmbedder(inner, () => index);
        Assert.Equal("modele-b", cached.EmbedQuery("télétravail").Model);   // SearchPassages : erreur
        inner.Model = "modele-a";                                           // le service revient
        Assert.Equal("modele-a", cached.EmbedQuery("télétravail").Model);
        cached.EmbedQuery("télétravail");
        Assert.Equal((2, 1), (inner.Calls.Count, cached.Hits));
    }

    [Fact]
    public void Cache_does_not_keep_vectors_of_another_dimension()
    {
        // Même nom de modèle, autre dimension (réglage du moteur changé) : rien à garder pour cet index.
        var inner = new KeywordEmbedder();
        var index = Manifest(dimension: 16);
        var cached = new CachedEmbedder(inner, () => index);
        cached.EmbedQuery("télétravail");
        cached.EmbedQuery("télétravail");
        Assert.Equal((2, 0), (inner.Calls.Count, cached.Hits));
    }

    [Fact]
    public void Cache_evicts_the_oldest_question_first()
    {
        var inner = new KeywordEmbedder();
        var index = Manifest();
        var cached = new CachedEmbedder(inner, () => index, maxEntries: 1);
        foreach (var question in new[] { "télétravail", "congés", "télétravail" })
        {
            cached.EmbedQuery(question);
        }
        Assert.Equal(3, inner.Calls.Count);
    }

    [Fact]
    public void Retries_a_transient_error_then_succeeds()
    {
        var inner = new Flaky(1);
        var slept = new List<TimeSpan>();
        var generation = new RetryingGenerator(inner, attempts: 1, sleep: slept.Add).Generate(Request);
        Assert.Equal("Deux jours [1].", generation.Text);
        Assert.Equal(2, inner.Calls);
        Assert.Single(slept);
    }

    [Fact]
    public void Does_not_retry_a_refused_request()
    {
        var calls = 0;
        var refusing = new LambdaGenerator(_ => { calls++; throw new AiServiceException("HTTP 404 — modèle inconnu", transient: false); });
        Assert.Throws<AiServiceException>(() => new RetryingGenerator(refusing, attempts: 3, sleep: _ => { }).Generate(Request));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Validation_rejects_leaked_reasoning()
    {
        var generator = new OutputValidatingGenerator(ScriptedGenerator.WithModel("qwen", "Okay, let's see. The user is asking… [1]"));
        var error = Assert.Throws<ModelOutputRejectedException>(() => generator.Generate(Request));
        Assert.Equal("qwen", error.Model);
        Assert.Contains(error.Problems, p => p.Contains("raisonnement"));
    }

    [Fact]
    public void Logging_reports_model_and_duration()
    {
        var lines = new List<string>();
        new LoggingGenerator(ScriptedGenerator.WithModel("llm-x", "Deux jours [1]."), lines.Add).Generate(Request);
        Assert.Contains("llm-x", lines[0]);
        Assert.Contains("ms", lines[0]);
    }

    private sealed class LambdaGenerator : IGenerator
    {
        private readonly Func<GenerationRequest, Generation> _f;
        public LambdaGenerator(Func<GenerationRequest, Generation> f) => _f = f;
        public Generation Generate(GenerationRequest request) => _f(request);
    }
}

/// <summary>Ce que l'application envoie au service IA, et ce qu'elle attend en retour (docs/contrat-http.md).</summary>
public class HttpContractTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _url;
    private readonly List<(string Path, JsonObject Body)> _requests = new();
    private readonly List<long> _contentLengths = new();

    public HttpContractTests()
    {
        _listener = TestPorts.Listener();
        _url = _listener.Prefixes.Single();
        _ = Task.Run(Serve);
    }

    private static readonly TimeSpan Short = TimeSpan.FromSeconds(5);

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                return;
            }
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
            lock (_requests)
            {
                _requests.Add((context.Request.Url!.AbsolutePath, body));
                _contentLengths.Add(context.Request.ContentLength64);
            }
            string response;
            var status = 200;
            if (context.Request.Url.AbsolutePath == "/v1/embeddings")
            {
                var n = body["inputs"]!.AsArray().Count;
                var dimension = body["model"]!.GetValue<string>() == "dimension-fausse" ? 4 : 3;   // annonce 4, envoie 3
                response = JsonSerializer.Serialize(new { model = "ollama:nomic-embed-text@0a109f422b47", alias = "nomic", dimension,
                                                         vectors = Enumerable.Repeat(new[] { 0.1, 0.2, 0.3 }, n) });
            }
            else if (body["model"]!.GetValue<string>() == "inconnu")
            {
                status = 404;
                response = """{"error": {"code": "unknown_model", "message": "modèle de génération inconnu : inconnu"}}""";
            }
            else if (body["model"]!.GetValue<string>() is "absent" or "surcharge")
            {
                status = 502;
                response = JsonSerializer.Serialize(new
                {
                    error = new { code = "backend_error", message = "moteur en échec", retryable = body["model"]!.GetValue<string>() == "surcharge" },
                });
            }
            else
            {
                response = JsonSerializer.Serialize(new { model = "ollama:llama3.2:3b@a80c", alias = "llama3-2-3b", text = "Deux jours [1].", duration_ms = 5 });
            }
            var bytes = Encoding.UTF8.GetBytes(response);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }

    [Fact]
    public void Embeddings_request_declares_an_intent_and_returns_the_concrete_model()
    {
        var batch = new HttpEmbedder(_url, "nomic", Short).EmbedQuery("Combien de jours ?");
        Assert.Equal("ollama:nomic-embed-text@0a109f422b47", batch.Model);
        Assert.Equal(3, batch.Dimension);
        var (path, body) = _requests.Single();
        Assert.Equal("/v1/embeddings", path);
        Assert.Equal("nomic", body["model"]!.GetValue<string>());
        Assert.Equal("query", body["input_type"]!.GetValue<string>());
        Assert.True(_contentLengths.Single() > 0);   // le vrai service (Python) refuse les envois en morceaux
    }

    [Fact]
    public void Vectors_that_do_not_match_the_announced_dimension_are_refused()
    {
        var error = Assert.Throws<AiServiceException>(() => new HttpEmbedder(_url, "dimension-fausse", Short).EmbedQuery("a"));
        Assert.False(error.Transient);
        Assert.Contains("incohérente", error.Message);
    }

    [Fact]
    public void Documents_are_sent_as_documents()
    {
        var batch = new HttpEmbedder(_url, "nomic", Short).EmbedDocuments(new[] { "a", "b" });
        Assert.Equal(2, batch.Vectors.Count);
        Assert.Equal("document", _requests.Single().Body["input_type"]!.GetValue<string>());
    }

    [Fact]
    public void Generation_request_sends_the_prompt_built_by_the_application()
    {
        var generation = new HttpGenerator(_url, "llama3-2-3b", Short).Generate(new GenerationRequest("sys", "prompt", 0.2, 150, 42));
        Assert.Equal("Deux jours [1].", generation.Text);
        Assert.Equal("ollama:llama3.2:3b@a80c", generation.Model);
        var (path, body) = _requests.Single();
        Assert.Equal("/v1/generate", path);
        Assert.Equal("llama3-2-3b", body["model"]!.GetValue<string>());
        Assert.Equal("sys", body["system"]!.GetValue<string>());
        Assert.Equal("prompt", body["prompt"]!.GetValue<string>());
        Assert.Equal(0.2, body["temperature"]!.GetValue<double>());
        Assert.Equal(150, body["max_tokens"]!.GetValue<int>());
        Assert.Equal(42, body["seed"]!.GetValue<int>());
    }

    [Fact]
    public void A_refused_request_is_not_transient()
    {
        var error = Assert.Throws<AiServiceException>(() => new HttpGenerator(_url, "inconnu", Short).Generate(new GenerationRequest("s", "p", 0.2, 10)));
        Assert.Contains("HTTP 404", error.Message);
        Assert.False(error.Transient);
    }

    [Theory]
    [InlineData("surcharge", true)]
    [InlineData("absent", false)]
    public void A_502_is_transient_unless_the_service_says_retrying_is_useless(string model, bool transient)
    {
        // Modèle absent : le service le signale (retryable = false), le client ne réessaie pas pour rien.
        var error = Assert.Throws<AiServiceException>(() => new HttpGenerator(_url, model, Short).Generate(new GenerationRequest("s", "p", 0.2, 10)));
        Assert.Equal(transient, error.Transient);
    }

    [Fact]
    public void An_unreachable_service_is_transient() =>
        Assert.True(Assert.Throws<AiServiceException>(() => new HttpEmbedder("http://127.0.0.1:1", "x", TimeSpan.FromSeconds(2)).EmbedQuery("a")).Transient);

    [Fact]
    public void An_invalid_address_is_an_explicit_non_transient_error()
    {
        var error = Assert.Throws<AiServiceException>(() => new HttpEmbedder("localhost:8100", "x", Short).EmbedQuery("a"));
        Assert.False(error.Transient);
        Assert.Contains("invalide", error.Message);
    }
}
