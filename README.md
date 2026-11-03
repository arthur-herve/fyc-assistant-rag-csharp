# Fil rouge, étape S2.3 : brancher le modèle

> **Branche d'étape.** Cette branche est l'état du fil rouge au début de la séquence S2.3. Il y manque
> exprès une pièce, l'adaptateur de génération : la séquence vous le fait écrire, et sa correction le
> donne. L'application complète, avec toute sa documentation, est sur la branche `final`.

## Où en est le fil rouge

L'application est complète, sauf son générateur. Le cœur est celui de S2.2, et les adaptateurs sont en
place autour de lui : le corpus en Markdown, le découpage, l'index en JSON, les prompts et le calcul des
embeddings auprès du service IA. Le générateur, lui, est un bouchon : `index` fonctionne, mais `ask`
s'arrête dès qu'il faut générer une réponse. La séquence part de là : vous écrivez le seul adaptateur qui
manque, puis vous changez de générateur sans toucher au cœur.

## Ce que vous faites dans cette séquence

1. Lisez le corrigé de S2.2, à la fin de
   [`exercices/s2.2-coeur-metier/README.md`](exercices/s2.2-coeur-metier/README.md), puis suivez le
   live-coding de la séquence.

2. Voyez ensuite le bouchon à l'œuvre. Lancez le service IA hors ligne dans un premier terminal avec
   `python -m ai_service`, puis construisez l'index et posez une question dans un second :

   ```bash
   dotnet run --project src/Assistant.Cli -- index
   dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail par semaine ?"
   ```

   `index` fonctionne, puisqu'il n'a pas besoin du modèle. La question, elle, s'arrête sur le message
   « Erreur : le générateur HTTP reste à écrire », qui renvoie à l'énoncé. Une question sans passage
   pertinent est refusée normalement, puisque le modèle n'est pas appelé. Essayez avec alice, qui n'est
   que dans le groupe `tous` :

   ```bash
   dotnet run --project src/Assistant.Cli -- ask "Quelle est la grille des salaires ?" --user alice -v
   ```

   alice reçoit un refus, et la trace affichée par `-v` montre que la grille ne figure pas parmi ses
   passages : elle a été écartée avant même la construction du prompt. Posée par bruno, qui est aussi
   aux RH, la même question trouve la grille et s'arrête sur le bouchon.

3. L'exercice est décrit dans
   [`exercices/s2.3-adaptateur-de-generation/README.md`](exercices/s2.3-adaptateur-de-generation/README.md).
   Créez votre branche, puis récupérez les tests attendus en nommant les fichiers :

   ```bash
   git switch -c mon-s2.3 origin/etape/s2.3
   git restore --source corrige/s2.3 -- tests/Assistant.Tests/HttpContractTests.cs tests/Assistant.Tests/AiServiceResponseTests.cs tests/Assistant.Tests/CliAskTests.cs
   ```

   La compilation échoue sur `HttpGenerator`, qui n'existe pas encore : c'est votre tâche.

4. Écrivez `HttpGenerator` dans `src/Assistant.Infrastructure/HttpAiClient.cs`, d'après
   `docs/contrat-http.md`, puis branchez-le dans `src/Assistant.Cli/Composition.cs` à la place du
   bouchon. L'énoncé donne la signature du constructeur et les deux fonctions qui lisent la réponse du
   service. Ni le domaine ni l'application ne changent.

5. Quand tous les tests passent, ceux de l'architecture compris, faites tourner l'application complète
   hors ligne : `index`, puis `ask` avec `--user alice`, puis avec `--user bruno`. L'énoncé donne les
   questions à poser et ce que la trace doit montrer.

6. Changez enfin de générateur sur le même index, sans toucher au cœur ni réindexer :

   ```bash
   dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail par semaine ?" --generation-model extractive-variante -v
   dotnet run --project src/Assistant.Cli -- ask "En combien de temps un incident P1 est-il pris en charge ?" --generation-model extractive-variante -v
   ```

   `extractive-variante` est un autre générateur hors ligne, qui choisit sa phrase autrement
   qu'`extractive`. L'énoncé vous fait comparer les deux générateurs sur ces deux questions, puis
   prédire par écrit ce que la variante donnera sur trois autres, avant de les poser. Avec Ollama,
   essayez de même `--generation-model llama3-2-3b` sur la question du P1.

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

Dans un second terminal, construisez l'index :

```bash
dotnet run --project src/Assistant.Cli -- index
```

`index` affiche le nombre de documents et de morceaux qu'il a indexés. Après chaque changement de
branche, reconstruisez l'index avec `index` : le dossier `data/` n'est pas versionné, et l'index d'une
autre étape n'a pas forcément le même format. Dans ce cas, l'application refuse de le lire et donne la
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
