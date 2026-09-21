// Décorateurs techniques sur les ports IEmbedder et IGenerator (séquence 4.1).
// Chacun enveloppe une implémentation du même port et ajoute une chose : cache,
// journal, nouvelles tentatives. Les cas d'usage n'en savent rien ; la racine de
// composition choisit lesquels empiler, et dans quel ordre.

using System.Diagnostics;
using Assistant.Application;

namespace Assistant.Infrastructure;

/// <summary>
/// Mémorise, dans le processus, les vecteurs des questions déjà posées. Leçon de la séquence 4.2 :
/// un cache d'embeddings est un artefact dérivé de l'index, comme l'index est dérivé du modèle
/// (ADR 0010). D'où trois règles :
/// <list type="bullet">
/// <item>il ne sert que l'index courant (<c>currentIndex</c>) : un nouvel index le vide, même si le
/// nom du modèle n'a pas changé (préfixes modifiés, moteur qui ne fournit pas d'empreinte…) ;</item>
/// <item>il ne garde que des vecteurs du modèle et de la dimension de cet index : sinon, une question
/// posée pendant que le service servait un autre modèle resterait en erreur même après le retour
/// du service au bon modèle ;</item>
/// <item>les documents ne sont jamais mis en cache : une (ré)indexation doit refléter le modèle
/// servi <i>maintenant</i>, pas celui d'une indexation précédente.</item>
/// </list>
/// Garanti : le cache ne mélange jamais deux index. Pas garanti : une question déjà posée ne repart
/// pas au service, donc c'est une question nouvelle (ou <c>status</c>, qui n'utilise pas ce cache) qui
/// révèle un changement de modèle servi (ADR 0010). Pas de verrou : l'API HTTP de l'application
/// (HttpApi) traite une requête à la fois.
/// </summary>
public sealed class CachedEmbedder : IEmbedder
{
    private readonly IEmbedder _inner;
    private readonly Func<IndexManifest?> _currentIndex;
    private readonly int _maxEntries;
    private readonly Dictionary<string, EmbeddingBatch> _queries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private IndexManifest? _index;   // l'index que les entrées servent

    public int Hits { get; private set; }
    public int Misses { get; private set; }

    public CachedEmbedder(IEmbedder inner, Func<IndexManifest?> currentIndex, int maxEntries = 10_000)
    {
        _inner = inner;
        _currentIndex = currentIndex;
        _maxEntries = maxEntries;
    }

    public EmbeddingBatch EmbedQuery(string text)
    {
        var index = _currentIndex();
        // Un nouvel index est un nouvel objet manifeste. ReferenceEquals, pas == : deux manifestes
        // de mêmes valeurs (record) peuvent décrire deux index aux vecteurs différents.
        if (!ReferenceEquals(index, _index))
        {
            _index = index;
            _queries.Clear();
            _order.Clear();
        }
        if (_queries.TryGetValue(text, out var cached))
        {
            Hits++;
            return cached;
        }
        Misses++;
        var batch = _inner.EmbedQuery(text);
        if (index is not null && batch.Model == index.EmbeddingModel && batch.Dimension == index.Dimension)
        {
            if (_order.Count >= _maxEntries)
            {
                _queries.Remove(_order.Dequeue());   // le plus ancien sort
            }
            _queries[text] = batch;
            _order.Enqueue(text);
        }
        return batch;
    }

    public EmbeddingBatch EmbedDocuments(IReadOnlyList<string> texts) => _inner.EmbedDocuments(texts);
}

public sealed class LoggingEmbedder : IEmbedder
{
    private readonly IEmbedder _inner;
    private readonly Action<string> _log;

    public LoggingEmbedder(IEmbedder inner, Action<string> log)
    {
        _inner = inner;
        _log = log;
    }

    public EmbeddingBatch EmbedQuery(string text)
    {
        var watch = Stopwatch.StartNew();
        var batch = _inner.EmbedQuery(text);
        _log($"embeddings requête · {batch.Model} · {batch.Dimension} dim. · {watch.ElapsedMilliseconds} ms");
        return batch;
    }

    public EmbeddingBatch EmbedDocuments(IReadOnlyList<string> texts)
    {
        var watch = Stopwatch.StartNew();
        var batch = _inner.EmbedDocuments(texts);
        _log($"embeddings {texts.Count} documents · {batch.Model} · {watch.ElapsedMilliseconds} ms");
        return batch;
    }
}

public sealed class LoggingGenerator : IGenerator
{
    private readonly IGenerator _inner;
    private readonly Action<string> _log;

    public LoggingGenerator(IGenerator inner, Action<string> log)
    {
        _inner = inner;
        _log = log;
    }

    public Generation Generate(GenerationRequest request)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var generation = _inner.Generate(request);
            _log($"génération · {generation.Model} · {generation.Text.Length} caractères · {watch.ElapsedMilliseconds} ms");
            return generation;
        }
        catch (Exception error)
        {
            _log($"génération rejetée ou échouée après {watch.ElapsedMilliseconds} ms : {error.Message}");
            throw;
        }
    }
}

/// <summary>
/// Réessaie quand le service IA est injoignable ou en erreur passagère (5xx), pas quand
/// la requête est refusée (4xx) ni quand la réponse est invalide.
/// </summary>
public static class Retry
{
    public static T Run<T>(Func<T> action, int attempts, TimeSpan delay, Action<TimeSpan> sleep, Action<string> log)
    {
        AiServiceException? last = null;
        for (var attempt = 0; attempt <= attempts; attempt++)
        {
            try
            {
                return action();
            }
            catch (AiServiceException error)
            {
                last = error;
                if (!error.Transient)
                {
                    throw;   // modèle inconnu, requête refusée : réessayer ne changera rien
                }
                if (attempt < attempts)
                {
                    log($"service IA en erreur ({error.Message}), nouvelle tentative {attempt + 1}/{attempts}");
                    sleep(delay * (attempt + 1));
                }
            }
        }
        throw last!;
    }
}

public sealed class RetryingEmbedder : IEmbedder
{
    private readonly IEmbedder _inner;
    private readonly int _attempts;
    private readonly TimeSpan _delay;
    private readonly Action<TimeSpan> _sleep;
    private readonly Action<string> _log;

    public RetryingEmbedder(IEmbedder inner, int attempts = 1, TimeSpan? delay = null,
                            Action<TimeSpan>? sleep = null, Action<string>? log = null)
    {
        _inner = inner;
        _attempts = attempts;
        _delay = delay ?? TimeSpan.FromMilliseconds(500);
        _sleep = sleep ?? Thread.Sleep;
        _log = log ?? (_ => { });
    }

    public EmbeddingBatch EmbedQuery(string text) => Retry.Run(() => _inner.EmbedQuery(text), _attempts, _delay, _sleep, _log);

    public EmbeddingBatch EmbedDocuments(IReadOnlyList<string> texts) =>
        Retry.Run(() => _inner.EmbedDocuments(texts), _attempts, _delay, _sleep, _log);
}

public sealed class RetryingGenerator : IGenerator
{
    private readonly IGenerator _inner;
    private readonly int _attempts;
    private readonly TimeSpan _delay;
    private readonly Action<TimeSpan> _sleep;
    private readonly Action<string> _log;

    public RetryingGenerator(IGenerator inner, int attempts = 1, TimeSpan? delay = null,
                             Action<TimeSpan>? sleep = null, Action<string>? log = null)
    {
        _inner = inner;
        _attempts = attempts;
        _delay = delay ?? TimeSpan.FromMilliseconds(500);
        _sleep = sleep ?? Thread.Sleep;
        _log = log ?? (_ => { });
    }

    public Generation Generate(GenerationRequest request) => Retry.Run(() => _inner.Generate(request), _attempts, _delay, _sleep, _log);
}
