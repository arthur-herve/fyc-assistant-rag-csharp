// Les décorateurs des ports : le cache d'embeddings, les nouvelles tentatives et le journal.

using Assistant.Application;
using Assistant.Infrastructure;
using Xunit;

namespace Assistant.Tests;

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

    // Deux manifestes construits ici avec les mêmes valeurs sont égaux (==), comme deux index reconstruits à
    // l'identique. Le cache doit pourtant y voir deux index différents.
    private static IndexManifest Manifest(string model = "fake-keywords", int dimension = 8) =>
        new(model, dimension, 1, 1);

    [Fact]
    public void Cache_embeds_the_same_question_once()
    {
        var inner = new KeywordEmbedder();
        var index = Manifest();
        var cached = new CachedEmbedder(inner, () => index);
        var first = cached.EmbedQuery("télétravail");
        Assert.Same(first, cached.EmbedQuery("télétravail"));
        Assert.Single(inner.Calls);
    }

    [Fact]
    public void Cache_never_stores_documents()
    {
        // Une réindexation doit refléter le modèle servi maintenant.
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
        // Préfixes changés ou moteur sans empreinte : les vecteurs changent alors que le nom du modèle reste le même.
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
        // Sinon, une question posée pendant que le service servait un autre modèle resterait en erreur, même
        // après le retour du service au modèle de l'index.
        var inner = new KeywordEmbedder("modele-b");
        var index = Manifest("modele-a");
        var cached = new CachedEmbedder(inner, () => index);
        Assert.Equal("modele-b", cached.EmbedQuery("télétravail").Model);   // SearchPassages le refuserait
        inner.Model = "modele-a";                                           // le service revient
        Assert.Equal("modele-a", cached.EmbedQuery("télétravail").Model);
        cached.EmbedQuery("télétravail");
        Assert.Equal(2, inner.Calls.Count);
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
        Assert.Equal(2, inner.Calls.Count);
    }

    [Fact]
    public void Cache_evicts_the_oldest_question_first()
    {
        var inner = new KeywordEmbedder();
        var index = Manifest();
        var cached = new CachedEmbedder(inner, () => index, maxEntries: 2);
        foreach (var question in new[] { "télétravail", "congés", "salaire", "congés", "télétravail" })
        {
            cached.EmbedQuery(question);
        }
        // « congés » est resté en cache, mais « télétravail », la plus ancienne, en est sortie.
        Assert.Equal(new[] { "télétravail", "congés", "salaire", "télétravail" }, inner.Calls.Select(call => call.Texts.Single()));
    }

    [Fact]
    public void Retries_a_transient_error_then_succeeds()
    {
        var inner = new Flaky(1);
        var generation = new RetryingGenerator(inner, attempts: 1, sleep: _ => { }).Generate(Request);
        Assert.Equal("Deux jours [1].", generation.Text);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public void Waits_before_a_new_attempt()
    {
        var slept = new List<TimeSpan>();
        new RetryingGenerator(new Flaky(1), attempts: 1, sleep: slept.Add).Generate(Request);
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
