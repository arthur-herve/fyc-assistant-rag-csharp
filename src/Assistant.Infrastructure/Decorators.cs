// Décorateurs techniques sur les ports IEmbedder et IGenerator (séquence 4.1). Chacun enveloppe
// une implémentation du même port et lui ajoute une seule chose : un cache, un journal ou de
// nouvelles tentatives. Les cas d'usage n'en savent rien. C'est la racine de composition qui
// choisit lesquels empiler, et dans quel ordre.

using System.Diagnostics;
using Assistant.Application;

namespace Assistant.Infrastructure;

/// <summary>
/// Garde en mémoire, dans le processus, les vecteurs des questions déjà posées. Un cache d'embeddings
/// dérive de l'index, comme l'index dérive du modèle.
/// Ce cache ne sert donc que l'index courant, que lui donne <c>currentIndex</c>. Un nouvel index le vide,
/// même quand le nom du modèle n'a pas changé, comme avec un moteur qui ne fournit pas d'empreinte. Le cache
/// ne mélange ainsi jamais deux index.
/// Il ne garde que les vecteurs du modèle et de la dimension de cet index. Sinon, une question posée
/// pendant que le service servait un autre modèle resterait en erreur, même une fois le bon modèle revenu.
/// Les documents ne passent jamais par le cache : une indexation doit refléter le modèle servi
/// <i>maintenant</i>, pas celui d'une indexation précédente.
/// Limite assumée : une question déjà posée ne repart pas au service. C'est donc une question nouvelle
/// qui révèle un changement du modèle servi.
/// </summary>
public sealed class CachedEmbedder : IEmbedder
{
    private readonly IEmbedder _inner;
    private readonly Func<IndexManifest?> _currentIndex;
    private readonly int _maxEntries;
    private readonly Dictionary<string, EmbeddingBatch> _queries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private IndexManifest? _index;   // l'index que les entrées servent

    public CachedEmbedder(IEmbedder inner, Func<IndexManifest?> currentIndex, int maxEntries = 10_000)
    {
        _inner = inner;
        _currentIndex = currentIndex;
        _maxEntries = maxEntries;
    }

    public EmbeddingBatch EmbedQuery(string text)
    {
        var index = _currentIndex();
        // Chaque nouvel index arrive avec un nouvel objet manifeste, d'où ReferenceEquals plutôt que ==.
        // IndexManifest est un record : deux manifestes aux mêmes valeurs peuvent décrire deux index
        // dont les vecteurs diffèrent.
        if (!ReferenceEquals(index, _index))
        {
            _index = index;
            _queries.Clear();
            _order.Clear();
        }
        if (_queries.TryGetValue(text, out var cached))
        {
            return cached;
        }
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
                    throw;   // erreur définitive, comme un modèle inconnu ou une requête refusée : inutile de réessayer
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
