# Fil rouge, correction de S3.2 : les données sont du code

> **Correction de la séquence S3.2.** Ce tag, `corrige/s3.2`, est l'état du fil rouge à la fin de la
> séquence. Il sert à lire la parade et à refaire les manipulations de la séquence pour la voir agir.
> L'application complète, avec toute sa documentation, est sur la branche `final`.

## Où en est le fil rouge

Le fil rouge a les parades des séquences précédentes : les droits filtrés avant le prompt, les citations
vérifiées, les tentatives et la graine. Cette correction ajoute celle de S3.2. L'index sait maintenant
quel modèle l'a construit et refuse une question vectorisée par un autre, et le seuil de pertinence se
règle par modèle d'embeddings. Elle ajoute aussi de quoi mesurer : un banc d'essai, qui propose un seuil
par modèle, et des expériences qui rejouent les ruptures. La séquence S3.3 part de là, avec une question
qu'elle laisse ouverte : où passe la frontière entre le métier et le modèle, quand une règle vit dans un
prompt ou dans les poids d'un modèle ?

## Ce que la correction a changé

Au départ de la séquence, l'index ne savait pas quel modèle l'avait construit. Le service IA renvoyait
pourtant `model` et `dimension` depuis S2.3, mais le port les jetait : `IEmbedder` ne rendait que des
vecteurs. Un changement de modèle d'embeddings sans réindexation donnait donc, à dimension égale, des
réponses fausses ou des refus sans aucune erreur, et sinon une erreur de dimension qui ne disait pas quoi
faire.

La correction change la forme des ports. `IEmbedder` rend un `EmbeddingBatch`, avec le modèle et la
dimension, et `IGenerator` une `Generation`, avec le modèle. L'index enregistre un manifeste : le modèle,
la dimension, le nombre de documents et de morceaux. Avant de chercher, `SearchPassages` compare le
modèle qui a vectorisé la question à celui du manifeste. S'ils diffèrent, il refuse et demande de
réindexer. La trace d'une réponse et les instantanés nomment les modèles qui ont servi.

Le seuil de pertinence devient une table par modèle d'embeddings, `retrieval.min_score`, avec une valeur
`default` pour les modèles qui n'en ont pas. Les commandes qui cherchent des passages préviennent quand
elles se rabattent sur cette valeur. Le banc d'essai, `benchmark`, mesure la recherche et la génération
et propose un seuil par modèle. Les expériences `cace-decoupage`, `changement-embeddings` et
`changement-generateur` rejouent les ruptures en ne changeant qu'une chose à la fois.

Pour lire la parade seule, sur les fichiers qui la montrent :

```bash
git diff origin/etape/s3.2 corrige/s3.2 -- src/Assistant.Application/Ports.cs src/Assistant.Application/SearchPassages.cs src/Assistant.Application/IndexCorpus.cs src/Assistant.Domain
```

Sur tout `src`, ce diff serait noyé dans le banc d'essai, `src/Assistant.Cli/Benchmark.cs`, qui compte à
lui seul plus de 600 lignes.

Refaites ensuite les deux pannes de la séquence, le service IA lancé et l'index construit avec
`hashing` :

```bash
dotnet run --project src/Assistant.Cli -- ask "Quelle est la capitale de l'Australie ?" -v --embedding-model hashing-stem4
dotnet run --project src/Assistant.Cli -- ask "Quelle est la capitale de l'Australie ?" -v --embedding-model hashing-512
```

La panne silencieuse ne l'est plus. Avec `hashing-stem4`, la dimension est la même, mais l'application
voit que l'index vient de `hashing-256-stem6` et la question de `hashing-256-stem4`, et elle demande de
réindexer au lieu de répondre à côté. Un avertissement précède l'erreur, parce que la configuration ne
donne pas de seuil pour ce modèle. Avec `hashing-512`, l'erreur de dimension laisse place au même
refus, qui dit quoi faire.

Pour le seuil, lancez le banc sur le corpus réduit, avec `hashing` :

```bash
dotnet run --project src/Assistant.Cli -- benchmark --config config/app-ollama.json --embedding hashing --generation extractive --questions eval/questions-service-public.json --runs 1
```

La table donne à `hashing` sa propre valeur, 0,45. L'assistant répond alors à 30 des 32 questions
répondables et refuse 8 des 10 autres, ce que le rapport écrit 0,94 et 0,80. Ajoutez `--min-score 0.65`
pour imposer le seuil calibré pour `bge-m3` : il ne répond plus qu'à 9 des 32 questions répondables. Le
banc écrit son index dans son propre dossier, et n'écrase donc pas celui de `data/`.

## Démarrer

Si votre poste n'est pas encore prêt, suivez [`docs/installation.md`](docs/installation.md). Vérifiez
ensuite les deux programmes :

```bash
dotnet test
python -m unittest discover -s tests_python -t .
```

Les deux commandes doivent se terminer sans échec. Lancez ensuite le service IA hors ligne dans un
premier terminal, et laissez-le tourner :

```bash
python -m ai_service
```

Dans un second terminal, construisez l'index, puis posez une question :

```bash
dotnet run --project src/Assistant.Cli -- index
dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail par semaine ?" -v
```

`index` affiche le manifeste de l'index qu'il vient de construire. La réponse se termine par une ligne
qui nomme les modèles et le nombre de tentatives. Avec `-v`, elle est suivie de sa trace : les passages
retrouvés, leurs scores et la sortie brute du modèle. Après chaque changement de branche, reconstruisez
l'index avec `index` : le dossier `data/` n'est pas versionné, et l'index d'une autre étape n'a pas
forcément le même format. Dans ce cas, l'application refuse de le lire et donne la commande qui le
reconstruit.

## Les branches du cours

| Séquence | Où l'on travaille | Correction |
|---|---|---|
| S1.1, S1.3 et S2.1 | `main` | `corrige/s2.1` |
| S2.2 Cœur sans IA | `etape/s2.2` | `corrige/s2.2` |
| S2.3 Brancher le modèle | `etape/s2.3` | `corrige/s2.3` |
| S3.1 Non-déterminisme | `etape/s3.1` | `corrige/s3.1` |
| S3.2 Les données sont du code | `etape/s3.2` | `corrige/s3.2` |
| S3.3 Frontière métier et modèle | `etape/s3.3` | pas de correction, c'est un débat |
| S4.1 Stratégies d'isolation | `etape/s4.1` | `corrige/s4.1` |
| S4.2 Versionner | `etape/s4.2` | `corrige/s4.2` |
| S4.3 et le module 5 | `final` | `final` |

Pour changer d'étape : `git switch etape/s3.2`, par exemple. Les corrections sont des tags : on les
compare à une branche avec `git diff origin/etape/s3.2 corrige/s3.2`, et on en récupère un fichier avec
`git restore --source corrige/s3.2 -- <fichier>`. La commande de restauration nomme toujours des fichiers,
jamais un dossier.

## Les choix de cette étape

- L'application C# et le service IA en Python ne partagent que le contrat de `docs/contrat-http.md`.
- L'index vit dans l'application. Construit par un autre modèle, il est refusé au lieu de répondre à côté.
- Le seuil de pertinence se règle par modèle d'embeddings et par corpus, dans la configuration.
- Les droits sont filtrés avant le prompt, et le prompt est construit dans l'application.
- La recherche, déterministe, se teste par assertion. La génération a droit à une seconde tentative,
  et une graine peut la rendre reproductible.
