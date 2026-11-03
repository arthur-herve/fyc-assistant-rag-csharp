# Fil rouge, étape S3.1 : non-déterminisme

> **Branche d'étape.** Cette branche est l'état du fil rouge au début de la séquence S3.1. Certains
> défauts y sont laissés exprès : la séquence les montre, et sa correction les pare. L'application
> complète, avec toute sa documentation, est sur la branche `final`.

## Où en est le fil rouge

Le fil rouge est dans sa version idéale : le cœur ne connaît le modèle que par ses ports, les droits
sont filtrés avant le prompt, et chaque réponse doit citer ses sources. Cette étape n'y ajoute que des
instruments de mesure : les instantanés, qui figent les réponses à un jeu de questions, et l'expérience
`stabilite`, qui pose plusieurs fois les mêmes questions sans rien changer. La séquence part de là pour
montrer ce que devient un test quand le modèle tire au hasard.

## Ce que vous faites dans cette séquence

Toutes les manipulations se font hors ligne. Lancez le service IA dans un premier terminal avec
`python -m ai_service`, puis construisez l'index dans un second :

```bash
dotnet run --project src/Assistant.Cli -- index
```

1. Vérifiez d'abord que la promesse du cours est tenue. Les tests passent, ceux de la règle de
   dépendance compris, et le générateur se remplace sur le même index sans toucher au cœur :

   ```bash
   dotnet test
   dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail par semaine ?"
   dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail par semaine ?" --generation-model extractive-variante
   ```

   Les deux générateurs ne choisissent pas la même phrase, mais chacun cite sa source et redonne la
   même réponse à chaque fois.

2. Regardez ensuite la vidéo de la séquence.

3. Passez à un générateur qui tire au hasard. `extractive-bruite` imite un modèle imparfait : il choisit
   au hasard entre les deux phrases les plus proches de la question, et il oublie sa citation 3 fois sur
   10. Posez-lui cinq fois la même question :

   ```bash
   dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail par semaine ?" --generation-model extractive-bruite
   ```

   La phrase change d'une fois à l'autre, et la réponse devient parfois « non sourcée » : l'assistant
   refuse alors de répondre et renvoie aux passages. Mesurez ensuite le phénomène sur les 24 questions de
   `eval/questions.json`, posées trois fois de suite sans rien changer :

   ```bash
   dotnet run --project src/Assistant.Cli -- experience stabilite --runs 3 --generation-model extractive-bruite
   ```

   La commande affiche à la fin le chemin de son rapport. Celui-ci appelle chaque tour un « passage » et
   compare les suivants au premier. Environ une réponse sur deux change d'un passage à l'autre, avec une
   quinzaine de changements de statut entre `answered` et `unsourced`, et ces chiffres varient d'une
   série à l'autre. Relancez la même commande sans `--generation-model` : avec `extractive`, rien ne
   bouge.

4. Faites enfin l'exercice de
   [`exercices/s3.1-evaluation-statistique/README.md`](exercices/s3.1-evaluation-statistique/README.md),
   dans `tests/Assistant.Tests/`, sans rien restaurer. Vous y écrivez d'abord le test à assertion exacte
   qu'on écrirait sans y penser, et vous le lancez dix fois. Viennent ensuite un test déterministe de la
   règle métier, puis une évaluation statistique dont vous choisissez la tolérance et calculez le risque
   de faux échec. Vous comparez enfin les deux régimes de test.

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
ligne qui donne son statut. Avec `-v`, elle est suivie de sa trace : les passages retrouvés, leurs
scores et la sortie brute du modèle. Après chaque changement de branche, reconstruisez l'index avec
`index` : le dossier `data/` n'est pas versionné, et l'index d'une autre étape n'a pas forcément le même
format. Dans ce cas, l'application refuse de le lire et donne la commande qui le reconstruit.

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
