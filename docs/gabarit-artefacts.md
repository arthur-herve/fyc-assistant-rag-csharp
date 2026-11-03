# Gabarit : les artefacts du fil rouge (séquence 4.2)

Une application de gestion classique, c'est du code et une base de données. Un assistant RAG dépend de
bien plus d'artefacts. Ils changent à des rythmes différents, et ce ne sont pas les mêmes personnes
qui les entretiennent. Ce gabarit vous fait dresser leur inventaire.

Remplissez-le après la manipulation de la séquence, une fois que vous avez modifié un document,
changé le découpage et réécrit le prompt. Travaillez sur une copie de ce fichier, ou sur papier.

## Le tableau des artefacts

Pour chaque artefact, dites où il vit, dans le dépôt ou ailleurs, qui le change, ce qui en dépend,
comment on le trace, et dans quelle couche du découpage il se situe. Dans la colonne « Comment on le
trace », notez d'abord ce que la branche trace déjà, puis ce qui manque.

| Artefact | Où il vit | Qui le change | Ce qui en dépend | Comment on le trace | Dans quelle couche |
|---|---|---|---|---|---|
| Code | | | | | |
| Corpus | | | | | |
| Découpage | | | | | |
| Index | | | | | |
| Prompts | | | | | |
| Modèle d'embeddings | | | | | |
| Modèle de génération | | | | | |

## Deux réglages

Le seuil de pertinence et les paramètres de génération ne sont pas des fichiers à part. Ils dépendent
pourtant des artefacts du tableau.

| Réglage | Il dépend de | Comment on le trace |
|---|---|---|
| Seuil de pertinence | | |
| Paramètres de génération | | |

## Qui dépend de qui

Dessinez les artefacts et les réponses de l'assistant, avec une flèche de chaque artefact vers ce qui
en dépend. Puis répondez à deux questions.

> Quels changements imposent de reconstruire l'index ?
>
> Lesquels n'agissent que sur les réponses ?

## Quand un artefact change

Notez ce que vous avez observé pendant la manipulation.

| Changement | Ce qui le signale sur cette branche | Ce qu'il faudrait pour le détecter |
|---|---|---|
| Un document du corpus modifié | | |
| Le découpage changé, sans réindexer | | |
| Le prompt réécrit sur place | | |
| Le modèle d'embeddings changé, sans réindexer | | |
| Le modèle de génération changé | | |

## Comparer avec la correction

La correction de la séquence, le tag `corrige/s4.2`, ajoute au dossier `docs/` le tableau rempli,
avec ce que le code détecte et à quel moment. Pour le comparer au vôtre :

```bash
git diff origin/etape/s4.2 corrige/s4.2 -- docs
```
