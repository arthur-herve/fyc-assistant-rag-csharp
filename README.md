# Fil rouge, correction de S3.1 : non-déterminisme

> **Correction de la séquence S3.1.** Ce tag, `corrige/s3.1`, est l'état du fil rouge à la fin de la
> séquence. Il sert à comparer votre travail au corrigé et à voir la parade agir. L'application
> complète, avec toute sa documentation, est sur la branche `final`.

## Où en est le fil rouge

Le fil rouge a maintenant les parades de S3.1. La recherche filtrée par les droits est un cas d'usage à
part, qui se teste par assertion. La génération a droit à une seconde tentative quand ses citations
échouent, et une graine peut rendre son tirage reproductible. La séquence S3.2 part de là, avec une
question : que deviennent les réponses quand on change les données, par exemple le découpage du
corpus, sans toucher au code ?

## Ce que la correction a changé

Au départ de la séquence, `AskQuestion` appelait le générateur une seule fois, sans graine. Avec
`extractive-bruite`, la même question changeait de texte d'un passage à l'autre, et une réponse qui
oubliait sa citation devenait « non sourcée ». Un test par assertion exacte sur ces réponses échouait
donc au hasard.

La correction fait passer la frontière entre ce qui est déterministe et ce qui ne l'est pas. La
recherche filtrée par les droits sort d'`AskQuestion` et devient `SearchPassages`, que l'on teste par
assertion. `AskQuestion` refait une tentative quand les citations échouent, selon la clé
`generation.max_attempts`, qui vaut 2 dans `config/app.json`. Une graine peut être fixée avec la clé
`generation.seed`, ou avec l'option `--seed` d'`experience stabilite`. Elle augmente d'une unité à chaque
tentative, et `HttpGenerator` l'envoie au service IA. La trace d'une réponse et les instantanés comptent
les tentatives. Les tests statistiques sont dans `tests/Assistant.Tests/StatisticalTests.cs`.

Pour lire la correction dans le code et dans les tests :

```bash
git diff origin/etape/s3.1 corrige/s3.1 -- src tests
```

Relancez ensuite la mesure de la séquence, le service IA lancé et l'index construit, une fois sans
graine, une fois avec :

```bash
dotnet run --project src/Assistant.Cli -- experience stabilite --runs 3 --generation-model extractive-bruite
dotnet run --project src/Assistant.Cli -- experience stabilite --runs 3 --generation-model extractive-bruite --seed 7
```

Sans graine, le texte change toujours d'un passage à l'autre, mais les réponses non sourcées deviennent
rares. Avec la graine, plus rien ne bouge. Le corrigé de l'énoncé, à la fin de
`exercices/s3.1-evaluation-statistique/README.md`, donne les mesures et répond aux questions de la
séquence.

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

`index` affiche le nombre de documents et de morceaux qu'il a indexés. La réponse se termine par une
ligne qui donne son statut et le nombre de tentatives. Avec `-v`, elle est suivie de sa trace : les
passages retrouvés, leurs scores et la sortie brute du modèle. Après chaque changement de branche,
reconstruisez l'index avec `index` : le dossier `data/` n'est pas versionné, et l'index d'une autre
étape n'a pas forcément le même format. Dans ce cas, l'application refuse de le lire et donne la
commande qui le reconstruit.

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
- L'index vit dans l'application, dans un fichier JSON.
- Le seuil de pertinence est un réglage de la configuration.
- Les droits sont filtrés avant le prompt, et le prompt est construit dans l'application.
- La recherche, déterministe, se teste par assertion. La génération a droit à une seconde tentative,
  et une graine peut la rendre reproductible.
