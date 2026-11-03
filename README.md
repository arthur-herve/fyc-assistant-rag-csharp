# Fil rouge, point de départ du cours

> **Point de départ du cours.** La branche `main`, celle que vous obtenez en clonant le dépôt, est l'état
> du fil rouge au début du cours. Elle sert aux séquences S1.1, S1.3 et S2.1. Il n'y a encore aucun code
> de l'application : la séquence S2.1 vous en fait concevoir le découpage, et sa correction l'écrit en
> code. L'application complète, avec toute sa documentation, est sur la branche `final`.

## Où en est le fil rouge

Le fil rouge n'a pas encore de code : ni solution, ni projets, ni tests de l'application. Le dépôt
contient ce dont il partira : le service IA en Python, avec ses modèles hors ligne, les corpus, le cahier
des charges et le gabarit de découpage. Il contient aussi l'exemple de la séquence 1.3, un petit
programme à part où l'on vérifie que changer de modèle ne touche pas au domaine. La séquence S2.1 part de
là : avant d'écrire une ligne, vous décidez des couches, des entités et des ports.

## Ce que vous faites dans cette séquence

Les exercices de cette branche sont aussi présentés dans [`exercices/README.md`](exercices/README.md), qui
explique comment travailler avec les branches.

1. En S1.1, vous préparez votre poste. Installez .NET 8, Python 3.11 ou plus et Git en suivant
   [`docs/installation.md`](docs/installation.md), et Ollama si vous le pouvez. Clonez ensuite le dépôt.
   Le clone vous place directement sur cette branche, vous n'avez pas à en changer :

   ```bash
   git clone https://github.com/arthur-herve/fyc-assistant-rag-csharp.git
   cd fyc-assistant-rag-csharp
   ```

   Vérifiez alors votre poste. Lancez les tests du service IA, puis le service lui-même dans un premier
   terminal, et laissez-le tourner :

   ```bash
   python -m unittest discover -s tests_python -t .
   python -m ai_service
   ```

   Dans un second terminal, faites répondre l'exemple de la séquence 1.3 par le service :

   ```bash
   dotnet run --project exemples/s1.3-transfert-naif -- generate extractive
   ```

   Vous devez obtenir la phrase sur le télétravail, suivie de sa citation `[1]`. Si ces trois commandes
   de vérification fonctionnent, votre poste est prêt.

2. En S1.3, vous regardez le transfert naïf à l'œuvre. Lisez le
   [README](exemples/s1.3-transfert-naif/README.md) de l'exemple et son port `ITextGenerator`, déclaré
   dans le domaine. Avant de lire son adaptateur HTTP, réécrivez-le d'après `docs/contrat-http.md`,
   comme le demande le README, puis comparez les deux. Changez ensuite de générateur par un argument,
   le service toujours lancé :

   ```bash
   dotnet run --project exemples/s1.3-transfert-naif -- generate fake
   dotnet run --project exemples/s1.3-transfert-naif -- generate extractive-variante
   ```

   Avec Ollama, essayez aussi `generate llama3-2-3b`. `Assistant.cs` ne bouge pas d'une commande à
   l'autre. Le troisième adaptateur que propose la partie « Pour aller plus loin » du README de
   l'exemple est facultatif : écrivez-le si le temps le permet.

3. En S2.1, vous concevez le découpage. Lisez [`docs/cahier-des-charges.md`](docs/cahier-des-charges.md)
   et parcourez les documents de `corpus/solveo/`. Remplissez ensuite
   [`docs/gabarit-decoupage.md`](docs/gabarit-decoupage.md), sur une copie ou sur papier : les couches,
   les entités, les ports, les frontières entre recherche, génération et orchestration, la place de
   l'index et celle du service IA. Comparez enfin votre gabarit à la correction, qui écrit le découpage
   en code et le décrit dans `docs/decoupage.md` :

   ```bash
   git diff origin/main corrige/s2.1
   ```

## Démarrer

Si votre poste n'est pas encore prêt, suivez [`docs/installation.md`](docs/installation.md). Vérifiez
ensuite le service IA et l'exemple :

```bash
python -m unittest discover -s tests_python -t .
dotnet build exemples/s1.3-transfert-naif -warnaserror
```

Les deux commandes doivent se terminer sans échec. Il n'y a pas encore de `dotnet test` : la solution et
ses tests arrivent avec la correction de S2.1. Pour faire répondre l'exemple, lancez le service IA hors
ligne dans un premier terminal avec `python -m ai_service`, puis l'exemple dans un second, comme à
l'étape 1.

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

- Les modèles tournent dans un programme à part, le service IA écrit en Python. L'application ne le
  joindra qu'en HTTP, selon le contrat de `docs/contrat-http.md`.
- Le service a des modèles hors ligne, qui suffisent pour tout le cours. Ollama est facultatif.
- L'application sera écrite en C# avec .NET 8, sans paquet tiers.
- L'exemple de S1.3 est un programme à part : il ne partage aucun code avec le fil rouge.
