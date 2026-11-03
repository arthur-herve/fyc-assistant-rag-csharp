# Fil rouge, étape S2.2 : le cœur sans IA

> **Branche d'étape.** Cette branche est l'état du fil rouge au début de la séquence S2.2. Il y manque
> exprès le cœur de l'assistant, les deux règles métier et le cas d'usage qui répond à une question : la
> séquence vous les fait écrire, et sa correction les donne. L'application complète, avec toute sa
> documentation, est sur la branche `final`.

## Où en est le fil rouge

Le découpage de S2.1 est en place : la solution `FycAssistantRag.sln`, ses quatre projets et leurs
références, le modèle du domaine et les ports de l'application. Le départ de la séquence y ajoute les
erreurs de l'application, le cas d'usage d'indexation, déjà écrit, et un double de test pour chaque port.
Les deux règles métier de `src/Assistant.Domain/Rules.cs` et le cas d'usage de
`src/Assistant.Application/AskQuestion.cs` lèvent encore `NotImplementedException`. `Assistant.Infrastructure`
est vide, et `Assistant.Cli` ne fait qu'annoncer l'application : la séquence montre que le cœur s'écrit
et se teste sans eux, sans modèle, sans réseau et sans disque.

## Ce que vous faites dans cette séquence

L'exercice est décrit dans
[`exercices/s2.2-coeur-metier/README.md`](exercices/s2.2-coeur-metier/README.md). En voici les étapes.

1. Comparez votre gabarit de S2.1 à `docs/decoupage.md`, aux quatre projets et à leurs références. Pour
   chaque port de `src/Assistant.Application/Ports.cs`, dites qui le déclare, qui l'implémente et pourquoi
   il vit dans l'application.

2. Créez votre branche à partir de la branche d'étape :

   ```bash
   git switch -c mon-s2.2 origin/etape/s2.2
   ```

3. Écrivez d'abord votre test, dans `tests/Assistant.Tests/MesTests.cs`, classe `MesTests`. Il vérifie
   que la trace d'un refus « aucune source pertinente » garde les passages retrouvés et leurs scores.
   L'énoncé dit pourquoi le fichier et la classe sont imposés.

4. Récupérez les tests attendus depuis la correction, en nommant les fichiers :

   ```bash
   git restore --source corrige/s2.2 -- tests/Assistant.Tests/DomainTests.cs tests/Assistant.Tests/AskQuestionTests.cs
   ```

5. Faites passer les tests un par un : `AccessPolicy.CanRead` et `Citations.Check` dans
   `src/Assistant.Domain/Rules.cs`, puis `AskQuestion.FormatPassages` et `AskQuestion.Execute` dans
   `src/Assistant.Application/AskQuestion.cs`. Les commentaires de ces méthodes disent ce qu'elles doivent
   faire.

6. Vérifiez que le cœur ne dépend de rien d'extérieur :

   ```bash
   dotnet test --filter "FullyQualifiedName~ArchitectureTests"
   ```

   Utilisez ensuite un `HttpClient` au début d'`Execute` : `The_core_does_not_touch_the_network` échoue.
   Retirez-le.

7. Lisez la correction, puis comparez-la à votre travail :

   ```bash
   git diff origin/etape/s2.2 corrige/s2.2
   git diff corrige/s2.2
   ```

## Démarrer

Si votre poste n'est pas encore prêt, suivez [`docs/installation.md`](docs/installation.md). Vérifiez
ensuite les deux programmes :

```bash
dotnet test
python -m unittest discover -s tests_python -t .
```

Les deux commandes doivent se terminer sans échec : les tests des règles et du cas d'usage ne sont pas
encore là. `IndexCorpusTests` montre comment on teste un cas d'usage avec les doubles de
`tests/Assistant.Tests/Fakes.cs`. Pour ne lancer qu'une classe de tests, filtrez-la par son nom :

```bash
dotnet test --filter "FullyQualifiedName~IndexCorpusTests"
```

Il n'y a pas encore d'application à lancer. Le service IA, `python -m ai_service`, ne sert pour
l'instant qu'à l'exemple de S1.3.

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

- Le domaine ne dépend de rien. Les ports sont déclarés dans l'application, à côté des cas d'usage qui
  s'en servent.
- L'index vit dans l'application, derrière le port `IVectorIndex`, et le service IA reste un programme
  à part, joint par le contrat de `docs/contrat-http.md`.
- Le seuil de pertinence est un réglage du cas d'usage.
- Les droits sont filtrés avant le prompt, et le prompt est construit dans l'application.
