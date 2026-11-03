# Exercices

Le fil rouge est livré en branches d'étape. La branche `etape/sX.Y` est l'état de l'assistant au début de
la séquence X.Y, et sa correction est marquée par un tag, `corrige/sX.Y`. Les exercices de code se font
donc dans le fil rouge lui-même, sur la branche de leur séquence.

Leurs énoncés sont dans ce dossier. La section « Corrigé » d'un énoncé n'est pas sur la branche de
départ : elle arrive avec la correction. L'exemple de S1.3 a son énoncé à côté de son programme. Les
exercices de S2.1 et de S4.2 partent d'un gabarit rangé dans `docs/`.

| Séquence | Exercice | Forme | Où l'on travaille | Correction |
|---|---|---|---|---|
| S1.3 | [Le transfert naïf](../exemples/s1.3-transfert-naif/README.md) | l'adaptateur HTTP à réécrire d'après le contrat, une lecture guidée et un changement de générateur, puis, pour aller plus loin, un adaptateur sans réseau à écrire | `main`, dans `exemples/s1.3-transfert-naif/`, un programme à part | `HttpGenerator` pour l'adaptateur HTTP, et pour l'adaptateur sans réseau une solution possible, dans le README de l'exemple |
| S2.1 | Le découpage de l'application, d'après le [cahier des charges](../docs/cahier-des-charges.md) | [gabarit](../docs/gabarit-decoupage.md) à remplir | `main` | `corrige/s2.1`, avec `docs/decoupage.md` |
| S2.2 | [Construire le cœur métier sans IA](s2.2-coeur-metier/README.md) | code à écrire, tests à récupérer | `etape/s2.2` | `corrige/s2.2` |
| S2.3 | [Écrire l'adaptateur de génération](s2.3-adaptateur-de-generation/README.md) | code à écrire, tests à récupérer | `etape/s2.3` | `corrige/s2.3` |
| S3.1 | [Un test déterministe et une évaluation statistique](s3.1-evaluation-statistique/README.md) | tests à écrire, calcul de tolérance | `etape/s3.1` | `corrige/s3.1` |
| S4.1 | [Ajouter un décorateur de validation](s4.1-decorateur-de-validation/README.md) | code à écrire, tests à récupérer | `etape/s4.1` | `corrige/s4.1` |
| S4.2 | Le tableau des artefacts | [gabarit](../docs/gabarit-artefacts.md) à remplir | `etape/s4.2` | `corrige/s4.2`, avec `docs/artefacts.md` |

S4.2 n'a pas d'énoncé à part : le gabarit `docs/gabarit-artefacts.md` dit ce qu'il faut remplir, et
`docs/artefacts.md` en est le corrigé.

Les QCM et le test final ne sont pas dans le dépôt : vous les trouverez sur Moodle.

## Travailler avec les branches

Après un clone, seule la branche `main`, le point de départ du cours, existe chez vous. Les branches
d'étape et `final` restent sur le dépôt distant : on les désigne donc par `origin/etape/sX.Y` et
`origin/final`. Les tags, eux, arrivent avec le clone, et `corrige/sX.Y` s'écrit sans préfixe. Si le dépôt
a changé depuis votre clone, `git fetch origin` met à jour les branches et les tags.

Pour un exercice, créez d'abord votre propre branche à partir de la branche de sa séquence. Vous y
travaillez et vous y commitez librement :

```bash
git switch -c mon-s2.2 origin/etape/s2.2
```

Quand l'énoncé vous le demande, récupérez les tests attendus depuis la correction, en nommant les
fichiers :

```bash
git restore --source corrige/s2.2 -- tests/Assistant.Tests/DomainTests.cs tests/Assistant.Tests/AskQuestionTests.cs
```

La commande de restauration nomme toujours des fichiers, jamais un dossier. Restauré en entier, un
dossier perd les fichiers suivis qui n'existent pas dans la correction : un fichier de test que vous
auriez commité disparaîtrait sans prévenir. Nommer les fichiers laisse aussi de côté les tests qui ne
font pas partie de l'exercice.

Une fois l'exercice fini, comparez votre travail à la correction, puis lisez la correction seule :

```bash
git diff corrige/s2.2
git diff origin/etape/s2.2 corrige/s2.2
```

La première commande compare vos fichiers à ceux de la correction. La seconde montre ce que la correction
ajoute à la branche de départ, sans votre travail.

Avant de passer à une autre étape, commitez votre travail sur votre branche. Sinon, `git switch` refuse
de partir dès qu'un de vos fichiers entre en conflit avec la branche d'arrivée.

## Réindexer après un changement de branche

Le dossier `data/`, où l'application range son index, n'est pas versionné. Il ne bouge donc pas quand
vous changez de branche, et l'index construit sur l'étape précédente reste en place, alors qu'il a pu
être fait avec un autre découpage ou dans un autre format. À partir d'`etape/s2.3`, reconstruisez-le
après chaque `git switch`, le service IA lancé :

```bash
dotnet run --project src/Assistant.Cli -- index
```
