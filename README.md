# Fil rouge, étape S3.3 : frontière métier et modèle

> **Branche d'étape.** Cette branche est l'état du fil rouge au début de la séquence S3.3. Cette
> séquence est un débat : elle n'a pas de correction. L'application complète, avec toute sa
> documentation, est sur la branche `final`.

## Où en est le fil rouge

Le fil rouge a les parades des séquences précédentes : les droits filtrés avant le prompt, les citations
vérifiées, les tentatives et la graine, le manifeste de l'index et un seuil par modèle d'embeddings. Il a
aussi un banc d'essai et des expériences qui rejouent les ruptures. Cette étape n'ajoute presque rien au
code : un second prompt, `prompts/answer-v2.json`, l'option `--prompt` qui choisit le prompt, et
l'expérience `prompt-v2` qui compare les deux. Le débat porte sur la frontière entre le métier et le
modèle, et rien dans cette branche ne tranche la question à votre place.

## Ce que vous faites dans cette séquence

1. Relisez la parade de S3.2 sur les seuls fichiers qui la montrent :

   ```bash
   git diff origin/etape/s3.2 corrige/s3.2 -- src/Assistant.Application/Ports.cs src/Assistant.Application/SearchPassages.cs src/Assistant.Application/IndexCorpus.cs src/Assistant.Domain
   ```

   Regardez ce que le port a dû changer pour que l'index sache quel modèle l'a construit. Sur tout `src`,
   ce diff serait noyé dans le banc d'essai, `src/Assistant.Cli/Benchmark.cs`, qui compte à lui seul plus
   de 600 lignes.

2. Le service IA lancé, rejouez hors ligne les deux expériences de S3.2 :

   ```bash
   dotnet run --project src/Assistant.Cli -- experience changement-embeddings --other hashing-512
   dotnet run --project src/Assistant.Cli -- experience cace-decoupage
   ```

   La première montre le refus explicite : l'index construit avec `hashing-256-stem6` n'est pas interrogé
   avec `hashing-512-stem6`, et l'application demande de réindexer. La seconde ne change que le découpage :
   les morceaux passent de 800 à 300 caractères, et leur recouvrement de 120 à 50. Un tiers des réponses
   changent, et la part des refus justes tombe de 0,80 à 0,40. Chaque expérience écrit un rapport et en
   affiche le chemin à la fin.

3. Lisez le support de la séquence et les extraits proposés, dont *Software 2.0* d'Andrej Karpathy.

4. Regardez où vivent les règles du fil rouge, et demandez-vous de quel côté de la frontière chacune se
   trouve :
   - le seuil de pertinence, dans la table `retrieval.min_score` de chaque fichier `config/*.json`, dit
     quand l'assistant doit refuser de répondre. C'est une règle métier, mais sa valeur dépend des poids du
     modèle d'embeddings ;
   - les consignes de `prompts/answer.json` demandent de répondre en français, en trois phrases au plus,
     en citant les passages. Ce sont des exigences du métier, écrites dans un texte réglé pour un modèle.

5. Comparez les deux prompts. `prompts/answer-v2.json` demande une seule phrase, qui commence par la
   valeur demandée. Avec Ollama, lancez l'expérience avec un vrai générateur. Les embeddings restent
   ceux de `config/app.json`, et les deux prompts sont comparés sur le même index :

   ```bash
   dotnet run --project src/Assistant.Cli -- experience prompt-v2 --generation-model llama3-2-3b
   ```

   Regardez la dérive et la longueur moyenne des réponses. Avec un vrai modèle, changer les consignes
   change les réponses, sans qu'une ligne de code ait bougé. Hors ligne, la même commande sans
   `--generation-model` donne 0 % de dérive, parce qu'`extractive` recopie une phrase des passages et
   ignore les consignes. L'effet du prompt ne se voit donc qu'avec un vrai modèle. Pour une seule
   question, l'option `--prompt answer-v2` de `ask` fait la même substitution.

6. Prenez position par écrit, en quelques lignes. Le prompt est-il de la configuration ou de la logique
   métier ? Le modèle est-il un détail, une entité, ou une catégorie que le schéma classique de la Clean
   Architecture ne prévoit pas ? Confrontez ensuite votre position au QCM, sur Moodle.

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
- Les droits sont filtrés avant le prompt, et le prompt est construit dans l'application.
- La recherche, déterministe, se teste par assertion. La génération a droit à une seconde tentative,
  et une graine peut la rendre reproductible.
- Le prompt se choisit par son nom, dans la configuration ou avec `--prompt`. Ce choix ne dit pas encore
  si le prompt relève de la configuration ou du métier : c'est la question de la séquence.
