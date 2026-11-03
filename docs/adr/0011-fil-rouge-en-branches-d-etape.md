# ADR 0011 — Le fil rouge est livré en branches d'étape

Date : 01/11/2026

Statut : acceptée. Elle complète l'ADR 0003.

## Contexte

La problématique du cours demande jusqu'où l'on peut isoler le modèle. Pour y répondre, le cours part
d'une version idéale du fil rouge, où le modèle est un détail que l'on remplace sans toucher au
métier. Il la met ensuite sous tension par trois ruptures : un test qui cesse d'être déterministe en
S3.1, une dépendance de données instable en S3.2, et un index qu'il faut reconstruire en S4.2.
Chaque rupture doit se voir au début de sa séquence, puis être parée dans sa correction.

Un exercice, lui, part d'un état précis du code. Or le dépôt ne montrait que l'état final, où toutes
les parades sont en place, si bien que l'apprenant voyait les réponses avant les questions. Le cœur
de S2.2 s'écrivait dans un kit autonome, qui contenait déjà les parades des séquences suivantes.
L'exercice de S4.1 commençait par retirer du dépôt le décorateur qu'il demandait d'écrire. Et la
panne silencieuse de S3.2 ne pouvait pas se montrer dans le fil rouge, puisque l'ADR 0003 l'en
écarte : on la racontait, ou on la montrait dans le programme jouet de S1.3.

## Décision

Le fil rouge est livré en branches d'étape.

- Le cours commence sur `main`, la branche par défaut, celle qu'on obtient en clonant le dépôt.
  C'est l'état du fil rouge au début de S2.1, et S1.1 et S1.3 s'y font aussi.
- La branche `etape/sX.Y` est l'état du fil rouge au début de la séquence X.Y. Une séquence n'a sa
  branche que si son état de départ diffère de celui de la précédente. Il y en a sept, de
  `etape/s2.2` à `etape/s4.2`.
- La version complète est sur la branche `final`. S4.3 et le module 5 s'y font.
- La correction d'une séquence est un commit posé sur sa branche de départ et marqué par le tag
  `corrige/sX.Y`. La branche de la séquence suivante ajoute par-dessus un commit « Départ de la
  séquence ». Sept séquences ont ainsi leur tag. S3.3 n'en a pas : c'est un débat, que le cours ne
  tranche pas.
- Les corrections sont des tags, et non des branches. Un tag existe en local dès le clone, et il ne
  bouge plus.
- La chaîne de commits est linéaire, de `main` à `final`. Chaque branche contient les précédentes
  et leurs corrections. `final` est au bout : la correction de S4.2, plus un commit « Compléments »
  qui apporte l'API HTTP, les ADR, les mesures de référence et le cas pratique.
- `final` est la seule référence complète. Les autres branches, `main` comprise, n'ont qu'un README
  court, qui dit en quelques lignes les choix déjà pris à cette étape.
- Les tests attendus d'un exercice ne sont jamais dans sa branche de départ. Ils sont dans la
  correction, et l'apprenant les récupère en nommant les fichiers :
  `git restore --source corrige/sX.Y -- <fichiers>`.

## Conséquences

- Les branches d'étape contiennent exprès des défauts que `final` n'a pas. Le plus sérieux est la
  panne silencieuse que l'ADR 0003 refuse. Jusqu'à la correction de S3.2, l'index ne connaît pas le
  modèle qui l'a produit, et un autre modèle d'embeddings de même dimension donne des réponses
  fausses ou des refus, sans aucune erreur. C'est la démonstration de S3.2, faite sur le fil rouge
  lui-même. L'ADR 0003 reste la règle de `final`, et elle s'applique dès `corrige/s3.2`.
- Ces défauts sont signalés. Un bandeau, en tête du README de chaque branche d'étape et de `main`,
  dit ce qui y manque exprès ou ce que la séquence y montre. Il renvoie à `final`, qui porte
  l'application complète et toute sa documentation. La branche par défaut est `main`, le point de
  départ : en clonant, l'apprenant arrive au début du cours, et non sur une version qui donne les
  réponses avant les questions.
- Les branches et les tags sont protégés. Après publication, on ne réécrit pas l'historique, puisque
  les apprenants ont cloné le dépôt. Un correctif se fait sur la première branche concernée, puis se
  fusionne vers l'avant, de branche en branche jusqu'à `final`. Un tag publié ne bouge pas : si un
  correctif touche une correction, il reçoit un nouveau tag, `corrige/s2.3-2` par exemple, et
  l'énoncé est mis à jour.
- Les ADR ne sont que sur `final`. Elles disent pourquoi le code final est ainsi, et se liraient mal
  sur une branche où ce code n'existe pas encore.
- Chaque état compile sans avertissement, passe ses propres tests et tourne hors ligne. Une
  fonctionnalité absente d'une étape l'est proprement : ni code, ni test, ni clé de configuration,
  ni mention dans la documentation de l'étape.
- Le cœur que l'apprenant écrit en S2.2 est celui du fil rouge, et non une copie. L'exemple de S1.3
  n'a plus à montrer la panne silencieuse : il est réduit à sa première partie, où la promesse tient.
- Les tags sont figés, si bien que les mesures du cours se refont à l'identique chez chacun. En S4.3
  et en S5.3, on compare deux corrections, par exemple avec `git diff corrige/s2.2 corrige/s4.2`.
- Le dossier `data/` n'est pas versionné. Après un changement de branche, il faut reconstruire
  l'index.
- Après un clone, seule `main` existe en local. Une branche d'étape, ou `final`, n'y existe qu'une
  fois qu'on y est passé. Les commandes des documents désignent donc les branches par leur nom
  distant, comme `origin/etape/sX.Y`.
- Le coût : seize états à construire, à vérifier et à tenir à jour, et des correctifs qui peuvent
  traverser plusieurs branches.

## Écarté

- Un kit par exercice : un dossier autonome, avec un départ et une solution, comme l'était celui de
  S2.2. Chaque kit est une copie du cœur à tenir à jour, et le cœur qu'écrit l'apprenant n'est pas
  celui du fil rouge. Pour garder la progression, il aurait fallu un kit par séquence, chacun sans
  les parades à venir. Le kit de S2.2 a quitté `exercices/` : complété par sa solution, il sert
  désormais de cœur fourni au cas pratique.
- Un seul état, la version complète, avec des instructions de retrait : l'énoncé dit quoi retirer avant
  de commencer, comme le faisait celui de S4.1. Le retrait touche plusieurs fichiers, la composition
  et les tests, et il est fragile. L'apprenant a la solution sous les yeux pendant qu'il la retire.
  Enfin, une rupture ne se montre pas sur un état qui la pare déjà : la panne silencieuse de S3.2
  serait restée racontée.
