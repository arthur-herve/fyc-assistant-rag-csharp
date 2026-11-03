# Fil rouge, étape S4.1 : stratégies d'isolation

> **Branche d'étape.** Cette branche est l'état du fil rouge au début de la séquence S4.1. Certains
> défauts y sont laissés exprès : la séquence les montre, et sa correction les pare. L'application
> complète, avec toute sa documentation, est sur la branche `final`.

## Où en est le fil rouge

Le fil rouge a les parades des séquences précédentes : les droits filtrés avant le prompt, les citations
vérifiées, les tentatives et la graine, le manifeste de l'index et un seuil par modèle d'embeddings. Des
décorateurs entourent maintenant les ports : un cache pour les embeddings des questions, un journal des
appels, et de nouvelles tentatives quand le service IA a une panne passagère. Il manque, exprès, un
contrôle de la sortie du modèle : seules ses citations sont vérifiées, si bien qu'un texte qui n'a pas la
forme d'une réponse arrive jusqu'à l'utilisateur. La séquence part de là pour distinguer ce qui isole la
technologie de ce qui isole l'incertitude.

## Ce que vous faites dans cette séquence

1. Lisez le support : isoler la technologie ou isoler l'incertitude, la couche anticorruption, la
   validation de schéma. Lisez ensuite les décorateurs fournis, dans
   `src/Assistant.Infrastructure/Decorators.cs`, et la façon dont `Composition.Decorate` les empile selon
   la section `decorators` de la configuration : ce sont les exemples commentés de la séquence. Le cache
   ne sert que l'index courant, qu'il reconnaît à son manifeste. Ce lien protège surtout un processus qui
   dure plus longtemps qu'une commande. Ici, chaque commande construit son conteneur et son cache, comme
   le banc d'essai et les expériences le font pour chaque configuration.

2. Voyez la fuite, hors ligne. L'alias `extractive-bavard` du service IA fait précéder sa réponse d'un
   raisonnement en anglais, comme certains modèles qui « réfléchissent » à voix haute. Le service IA
   lancé, construisez l'index, puis posez-lui une question :

   ```bash
   dotnet run --project src/Assistant.Cli -- index
   dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail par semaine ?" --generation-model extractive-bavard -v
   ```

   La réponse commence par « Okay, let me check the documents. » et se termine par la citation [1]. Elle
   reçoit le statut `answered`, et l'utilisateur la lit telle quelle. Le journal affiché par `-v` montre
   une seule génération, sans erreur ni nouvelle tentative : aucun décorateur ne regarde ce que le modèle
   a répondu. Ces décorateurs isolent la technologie : ils s'occupent des pannes du réseau, de la durée
   des appels et des embeddings déjà calculés, sans rien savoir de ce qu'est une bonne réponse.
   Qu'est-ce qui, dans le fil rouge, isole l'incertitude ? Gardez la question en tête pendant toute la
   séquence.

3. Lancez le test de caractérisation qui fige ce comportement. Il est dans `AskQuestionTests.cs`, avec les
   autres tests du cas d'usage, et il passe, parce qu'il affirme ce que fait le code aujourd'hui, pas ce
   qu'il devrait faire :

   ```bash
   dotnet test --filter "FullyQualifiedName~AskQuestionTests"
   ```

4. Faites l'exercice décrit dans
   [`exercices/s4.1-decorateur-de-validation/README.md`](exercices/s4.1-decorateur-de-validation/README.md).
   Créez votre branche, puis récupérez les tests attendus en nommant les fichiers :

   ```bash
   git switch -c mon-s4.1 origin/etape/s4.1
   git restore --source corrige/s4.1 -- tests/Assistant.Tests/DecoratorTests.cs tests/Assistant.Tests/AskQuestionTests.cs tests/Assistant.Tests/CompositionRootTests.cs tests/Assistant.Tests/ConfigTests.cs
   ```

   Les erreurs de compilation forment votre liste de tâches. L'énoncé donne les signatures, l'ordre dans
   lequel faire passer les tests et la place de la validation parmi les décorateurs. Le test de
   caractérisation disparaît avec la restauration : la version d'`AskQuestionTests.cs` de la correction le
   remplace par un test qui affirme le contraire.

5. Quand tous les tests passent, ceux de l'architecture compris, relancez la démonstration avec
   `extractive-bavard`. La réponse doit maintenant être refusée après deux tentatives : elle reçoit le
   statut `unsourced`, et l'utilisateur lit « Je n'ai pas pu produire de réponse correctement sourcée ».

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

La réponse se termine par une ligne qui nomme les modèles et le nombre de tentatives. Avec `-v`, elle
est suivie de sa trace : les passages retrouvés, leurs scores et la sortie brute du modèle. Après chaque
changement de branche, reconstruisez l'index avec `index` : le dossier `data/` n'est pas versionné, et
l'index d'une autre étape n'a pas forcément le même format. Dans ce cas, l'application refuse de le lire
et donne la commande qui le reconstruit.

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
  L'index vit dans l'application. Construit par un autre modèle, il est refusé au lieu de répondre à côté.
- Le seuil de pertinence se règle par modèle d'embeddings et par corpus, dans la configuration.
- Les droits sont filtrés avant le prompt, et le prompt est construit dans l'application. Il se choisit
  par son nom, dans la configuration ou avec `--prompt`.
- La recherche, déterministe, se teste par assertion. La génération a droit à une seconde tentative,
  et une graine peut la rendre reproductible.
- Les décorateurs entourent les ports sans que les cas d'usage le sachent, et le cache suit l'index courant.
