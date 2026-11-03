# Gabarit de découpage (séquence 2.1)

Ce gabarit accompagne le cahier des charges, `docs/cahier-des-charges.md`. Lisez celui-ci, parcourez
les documents de `corpus/solveo/`, puis remplissez les sections ci-dessous. Travaillez sur une copie
de ce fichier, ou sur papier.

Il n'y a pas une seule bonne réponse. Ce qui compte, c'est de pouvoir justifier chacun de vos choix,
et en premier lieu le sens des dépendances.

## 1. Les couches

Proposez vos couches, du centre vers l'extérieur. Pour chacune, dites ce qu'elle contient et de quoi
elle a le droit de dépendre.

| Couche | Ce qu'elle contient | Elle dépend de |
|---|---|---|
| | | |
| | | |
| | | |
| | | |

Comment ferez-vous respecter le sens des dépendances autrement que par la discipline ?

> Votre réponse :

## 2. Les entités

Relevez dans le cahier des charges ce que manipule le métier, et notez ce que porte chaque entité.

| Entité | Ce qu'elle porte |
|---|---|
| | |
| | |
| | |
| | |
| | |
| | |

## 3. Les règles métier

Pour chaque règle du cahier des charges, dites dans quelle couche elle vit, et comment vous la
testeriez sans aucun modèle.

| Règle | Où elle vit | Comment la tester sans modèle |
|---|---|---|
| Les droits sont filtrés avant le prompt | | |
| La citation est obligatoire | | |
| Sans source pertinente, l'assistant refuse | | |
| La réponse est courte et en français | | |

## 4. Les ports

Un port décrit ce dont le cœur de l'application a besoin du monde extérieur, sans dire comment c'est
fait. Listez les vôtres.

| Port | Ce que le cœur lui demande | Qui le déclare | Qui l'implémente |
|---|---|---|---|
| | | | |
| | | | |
| | | | |
| | | | |
| | | | |
| | | | |

## 5. Recherche, génération et orchestration

Le cahier des charges décrit une chaîne : la recherche, la génération, puis la réponse citée. Placez
une frontière entre chaque maillon.

| | Ce qu'elle reçoit | Ce qu'elle rend | Les ports qu'elle utilise | Ce qu'elle ne doit pas savoir |
|---|---|---|---|---|
| Recherche | | | | |
| Génération | | | | |
| Orchestration | | | | |

À quel endroit de la chaîne appliquez-vous les droits d'accès, et pourquoi là ?

> Votre réponse :

## 6. Où vit l'index ?

L'index réunit les morceaux du corpus et leurs vecteurs. Il faut le construire avant la première
question.

> Dans quel programme, quelle couche, derrière quel port :
>
> Ce que ce choix permet :
>
> Ce qu'il coûte :
>
> L'option que vous écartez, et pourquoi :

## 7. Où vit le service IA ?

Le service IA est fourni dans `ai_service/`, et son contrat est décrit dans `docs/contrat-http.md`.

> Comment l'application le joint :
>
> Quelle partie de l'application le connaît :
>
> Ce qui reste de son côté, et ce qui reste du côté de l'application :

## 8. Le schéma

Dessinez vos couches et le service IA, avec les flèches de dépendance. Une flèche part de ce qui
dépend et va vers ce dont il dépend.

## Comparer avec la correction

La correction est le tag `corrige/s2.1`. Elle écrit le découpage en code et le décrit dans
`docs/decoupage.md`. Pour voir tout ce qu'elle ajoute :

```bash
git diff origin/main corrige/s2.1
```

Vous y reviendrez au début de la séquence 2.2 : vous comparerez votre gabarit à cette correction, et
vous justifierez chacun de ses ports.
