# Fil rouge, étape S3.2 : les données sont du code

> **Branche d'étape.** Cette branche est l'état du fil rouge au début de la séquence S3.2. Certains
> défauts y sont laissés exprès : la séquence les montre, et sa correction les pare. L'application
> complète, avec toute sa documentation, est sur la branche `final`.

## Où en est le fil rouge

Le fil rouge a les parades des séquences précédentes : les droits filtrés avant le prompt, les citations
vérifiées, les tentatives et la graine. La séquence part de là pour montrer qu'un découpage, un modèle
d'embeddings ou un seuil changés font bouger les réponses, souvent sans la moindre erreur.

## Ce que vous faites dans cette séquence

Toutes les manipulations se font hors ligne. Lancez le service IA dans un premier terminal avec
`python -m ai_service`, puis construisez l'index dans un second :

```bash
dotnet run --project src/Assistant.Cli -- index
```

1. Prenez d'abord une ligne de base. Posez trois fois les 24 questions de `eval/questions.json`, sans
   rien changer :

   ```bash
   dotnet run --project src/Assistant.Cli -- experience stabilite --runs 3
   ```

   La commande affiche à la fin le chemin de son rapport. Avec `extractive`, aucune réponse ne bouge
   d'un passage à l'autre : la dérive moyenne vaut 0. Toute dérive mesurée ensuite viendra donc de ce
   que vous aurez changé.

2. Changez ensuite le découpage. Enregistrez un instantané, changez seulement la taille des morceaux,
   puis comparez :

   ```bash
   dotnet run --project src/Assistant.Cli -- snapshot record avant
   # dans config/app.json, splitter.max_chars passe de 800 à 300
   dotnet run --project src/Assistant.Cli -- index
   dotnet run --project src/Assistant.Cli -- snapshot record apres
   dotnet run --project src/Assistant.Cli -- snapshot compare avant apres
   ```

   Le recouvrement, `splitter.overlap_chars`, reste à 120. La comparaison ne trouve qu'une différence
   de configuration, le découpage. Pourtant, 8 réponses sur 24 changent, soit 33 %, et 5 changent de
   statut. Pour cinq questions, la réponse n'est pas accessible à l'utilisateur qui les pose : elle
   manque au corpus, ou elle se trouve dans un document réservé à d'autres groupes. L'assistant
   refusait quatre de ces questions, il n'en refuse plus qu'une. À la question sur la capitale de
   l'Australie, il répond maintenant par le titre de la fiche du télétravail.

   C'est le principe CACE, « Changing Anything Changes Everything », que Sculley et al. ont décrit en
   2015 dans « Hidden Technical Debt in Machine Learning Systems » : dans un système qui dépend d'un
   modèle et de ses données, changer une seule chose peut changer tout le reste.

3. Provoquez maintenant la panne silencieuse. Remettez d'abord le découpage d'origine et reconstruisez
   l'index :

   ```bash
   git restore config/app.json
   dotnet run --project src/Assistant.Cli -- index
   ```

   Posez ensuite la même question hors corpus, une fois avec le modèle de l'index, une fois avec un
   autre modèle d'embeddings, sans réindexer :

   ```bash
   dotnet run --project src/Assistant.Cli -- ask "Quelle est la capitale de l'Australie ?" -v
   dotnet run --project src/Assistant.Cli -- ask "Quelle est la capitale de l'Australie ?" -v --embedding-model hashing-stem4
   ```

   Avec `hashing`, l'assistant refuse à juste titre : le meilleur passage n'atteint que 0,121, sous le
   seuil de 0,15. `hashing-stem4` donne des vecteurs de la même dimension, mais calculés autrement.
   Aucune erreur n'apparaît, et l'assistant répond « Charte d'utilisation des outils informatiques [1] »,
   avec un score de 0,168. Pour la vue d'ensemble, enregistrez un instantané avec ce modèle et
   comparez-le à `avant` :

   ```bash
   dotnet run --project src/Assistant.Cli -- snapshot record stem4 --embedding-model hashing-stem4
   dotnet run --project src/Assistant.Cli -- snapshot compare avant stem4
   ```

   La seule différence de configuration est `embedding_model`, et 18 réponses sur 24 changent.
   Quinze deviennent des refus, la question de la capitale reçoit une réponse, et deux autres citent
   une fiche sans rapport avec la question.

4. Passez à la panne bruyante, en changeant de dimension :

   ```bash
   dotnet run --project src/Assistant.Cli -- ask "Quelle est la capitale de l'Australie ?" -v --embedding-model hashing-512
   ```

   La commande s'arrête sur « Erreur : vecteur de requête de 512 dimensions, l'index en attend 256 ».
   Cette fois, l'erreur est franche.

5. Reste le seuil. Celui de `config/app-ollama.json`, 0,65, a été calibré pour `bge-m3`. Essayez-le
   avec `hashing`, sur le corpus réduit et ses 42 questions. Attention : la première commande écrase
   `data/index-service-public-reduit.json`. Si vous aviez construit cet index avec `bge-m3`, il faudra
   le reconstruire ensuite, Ollama lancé.

   ```bash
   dotnet run --project src/Assistant.Cli -- index --config config/app-ollama.json --embedding-model hashing --generation-model extractive
   dotnet run --project src/Assistant.Cli -- snapshot record seuil-065 --config config/app-ollama.json --embedding-model hashing --generation-model extractive --questions eval/questions-service-public.json
   ```

   L'assistant ne répond plus qu'à 9 des 32 questions qui ont une réponse accessible, et refuse les
   23 autres. Essayez ensuite le seuil dans l'autre sens. `snapshot record` n'a pas d'option pour changer
   le seuil : mettez donc 0,15 dans `retrieval.min_score` de `config/app-ollama.json`, puis
   recommencez et comparez les deux instantanés :

   ```bash
   dotnet run --project src/Assistant.Cli -- snapshot record seuil-015 --config config/app-ollama.json --embedding-model hashing --generation-model extractive --questions eval/questions-service-public.json
   dotnet run --project src/Assistant.Cli -- snapshot compare seuil-065 seuil-015
   ```

   Les 32 questions reçoivent maintenant une réponse, mais aussi 9 des 10 questions sans réponse
   accessible. 0,15 est pourtant le seuil de `config/app.json`, avec le même modèle : sur un autre
   corpus, il ne convient plus.

6. Répondez au QCM de la séquence, sur Moodle. Remettez ensuite les fichiers de configuration en état :

   ```bash
   git restore config/app.json config/app-ollama.json
   ```

   L'index du corpus Solvéo a retrouvé son découpage d'origine à l'étape 3. Celui du corpus réduit,
   `data/index-service-public-reduit.json`, reste construit avec `hashing`, car le dossier `data/`
   n'est pas versionné. Avant de vous en resservir avec `bge-m3`, reconstruisez-le, Ollama lancé :

   ```bash
   dotnet run --project src/Assistant.Cli -- index --config config/app-ollama.json
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
