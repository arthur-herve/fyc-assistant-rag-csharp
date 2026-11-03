# Fil rouge, étape S4.2 : versionner ensemble

> **Branche d'étape.** Cette branche est l'état du fil rouge au début de la séquence S4.2. Certains
> défauts y sont laissés exprès : la séquence les montre, et sa correction les pare. L'application
> complète, avec toute sa documentation, est sur la branche `final`.

## Où en est le fil rouge

Le fil rouge a toutes les parades vues jusqu'ici : les droits filtrés avant le prompt, les citations
vérifiées, les tentatives, le manifeste de l'index, les décorateurs et la validation de la sortie. La
séquence part de là pour montrer qu'un document, un découpage ou un prompt changés sur place passent
inaperçus.

## Ce que vous faites dans cette séquence

1. Comparez d'abord votre décorateur de validation au corrigé de S4.1 :

   ```bash
   git diff origin/etape/s4.1 corrige/s4.1
   ```

2. Provoquez la troisième rupture, sur votre propre branche
   (`git switch -c mon-s4.2 origin/etape/s4.2`). Le service IA lancé, construisez l'index et posez une
   question :

   ```bash
   dotnet run --project src/Assistant.Cli -- index
   dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail par semaine ?"
   ```

   L'assistant répond qu'on peut télétravailler « jusqu'à deux jours par semaine », et cite le passage
   [1]. Remplacez « deux jours » par « trois jours » dans `corpus/solveo/teletravail.md`, puis reposez la
   même question : la réponse dit toujours « deux jours », et rien ne le signale. Faites de même avec le
   découpage : passez `splitter.max_chars` de 800 à 300 dans `config/app.json`, sans réindexer. La
   réponse ne bouge pas davantage.

3. Passez au prompt. Enregistrez un instantané, remplacez « Question : » par « Demande : » dans
   `prompts/answer.json`, enregistrez un second instantané, puis comparez les deux :

   ```bash
   dotnet run --project src/Assistant.Cli -- snapshot record avant
   # remplacement dans prompts/answer.json
   dotnet run --project src/Assistant.Cli -- snapshot record apres
   dotnet run --project src/Assistant.Cli -- snapshot compare avant apres
   ```

   La comparaison mesure 62 % de dérive. Pourtant, sous « Différences de configuration », elle répond
   « aucune : même configuration des deux côtés ». Cette dérive vient d'un couplage du générateur de
   test : `extractive` cherche le mot « Question : » dans le prompt pour savoir quelle phrase recopier.
   Sans lui, il rend le plus souvent le titre du document. Un vrai modèle réagirait autrement. Ce qui
   compte ici, c'est que rien, ni dans la réponse ni dans l'instantané, ne dit que le prompt a changé.

4. Remplissez le gabarit [`docs/gabarit-artefacts.md`](docs/gabarit-artefacts.md) pour chaque
   artefact : le code, le corpus, le découpage, l'index, les prompts, le modèle d'embeddings et le
   modèle de génération. Pour chacun, dites où il vit, qui le change, ce qui en dépend, comment on le
   trace et dans quelle couche il se situe.

5. Remettez enfin les fichiers en état, puis réindexez :

   ```bash
   git restore corpus/solveo/teletravail.md config/app.json prompts/answer.json
   dotnet run --project src/Assistant.Cli -- index
   ```

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
  La sortie du modèle passe par un décorateur de l'application, qui la valide avec une règle du domaine.
