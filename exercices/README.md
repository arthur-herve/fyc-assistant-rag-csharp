# Exercices

Le fil rouge est livré en branches d'étape. La branche `etape/sX.Y` est l'état de l'assistant au début de
la séquence X.Y, et sa correction est marquée par un tag, `corrige/sX.Y`. Les exercices de code se font
donc dans le fil rouge lui-même, sur la branche de leur séquence.

L'exemple de S1.3 a son énoncé à côté de son programme.
L'exercice de S2.1 part d'un gabarit rangé dans `docs/`.

| Séquence | Exercice | Forme | Où l'on travaille | Correction |
|---|---|---|---|---|
| S1.3 | [Le transfert naïf](../exemples/s1.3-transfert-naif/README.md) | l'adaptateur HTTP à réécrire d'après le contrat, une lecture guidée et un changement de générateur, puis, pour aller plus loin, un adaptateur sans réseau à écrire | `main`, dans `exemples/s1.3-transfert-naif/`, un programme à part | `HttpGenerator` pour l'adaptateur HTTP, et pour l'adaptateur sans réseau une solution possible, dans le README de l'exemple |
| S2.1 | Le découpage de l'application, d'après le [cahier des charges](../docs/cahier-des-charges.md) | [gabarit](../docs/gabarit-decoupage.md) à remplir | `main` | `corrige/s2.1`, avec `docs/decoupage.md` |

Les QCM et le test final ne sont pas dans le dépôt : vous les trouverez sur Moodle.

## Travailler avec les branches

Après un clone, seule la branche `main`, le point de départ du cours, existe chez vous. Les branches
d'étape et `final` restent sur le dépôt distant : on les désigne donc par `origin/etape/sX.Y` et
`origin/final`. Les tags, eux, arrivent avec le clone, et `corrige/sX.Y` s'écrit sans préfixe. Si le dépôt
a changé depuis votre clone, `git fetch origin` met à jour les branches et les tags.

Pour un exercice, créez d'abord votre propre branche à partir de la branche de sa séquence. Vous y
travaillez et vous y commitez librement :

```bash
git switch -c mon-s2.1 origin/main
```

Une fois l'exercice fini, comparez votre travail à la correction, puis lisez la correction seule :

```bash
git diff corrige/s2.1
git diff origin/main corrige/s2.1
```

La première commande compare vos fichiers à ceux de la correction. La seconde montre ce que la correction
ajoute à la branche de départ, sans votre travail.

Avant de passer à une autre étape, commitez votre travail sur votre branche. Sinon, `git switch` refuse
de partir dès qu'un de vos fichiers entre en conflit avec la branche d'arrivée.
