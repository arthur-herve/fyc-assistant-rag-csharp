# ADR 0007 — Bibliothèque standard uniquement dans l'application

Date : 11/09/2026

Statut : acceptée. Reprise dans la version C# le 12/09/2026, avec des références de fichiers et des
mesures adaptées.

## Contexte

Le cours doit tourner sur un portable ordinaire, sans matériel spécialisé, pour des apprenants qui
découvrent le sujet. Chaque dépendance est une installation qui peut échouer, et une API qui peut
changer pendant les cinq ans où le dépôt doit rester accessible.

## Décision

L'application (`src/Assistant.*`) n'utilise aucun paquet NuGet. .NET 8 et sa bibliothèque de classes
suffisent : `System.Text.Json`, `HttpClient`, `System.Security.Cryptography`. Seul le projet de tests
ajoute xUnit.

Le service IA (`ai_service/`) n'utilise que la bibliothèque standard de Python 3.11+ : `http.server`,
`urllib`, `json`, `tomllib`, `hashlib`. La seule exception, explicite, est `sentence-transformers` :
il reste optionnel et ne concerne que le service IA. Les vrais modèles sont servis par Ollama, un
programme externe.

## Conséquences

- Les tests et le démarrage hors-ligne fonctionnent avec `dotnet` et `python` seuls, sans aucun
  `dotnet add package` ni `pip install`.
- L'index est un fichier JSON, et chaque recherche parcourt tous les morceaux. Cela tient pour
  quelques milliers de morceaux, pas plus. C'est une limite nommée dans le cours (S4.3), pas un
  oubli : une base vectorielle pourrait se brancher derrière le même port `IVectorIndex`.
- Le code est plus long qu'avec un cadriciel, puisqu'il faut analyser les arguments et sérialiser à
  la main, mais il se lit en entier. Et `Directory.Build.props` traite les avertissements comme des
  erreurs.

## Écarté

FastAPI et numpy, qu'`AssistantQR` avait choisis pour son service Python. Ce serait plus court et
plus rapide, mais les apprenants auraient une chaîne d'installation de plus, et ce code cacherait
ce que le cours veut justement montrer.

Côté C#, `Microsoft.Extensions.AI` ou un client Ollama ont été écartés pour les mêmes raisons. En
plus, la frontière HTTP (ADR 0001) aurait disparu dans une bibliothèque.
