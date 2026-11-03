# Cas pratique S5.1 — Rendre maintenable une application IA existante (C#)

Durée indicative : 1 h de travail, puis 30 min de correction commentée en vidéo (S5.2).

## Situation

Un collègue a écrit `depart/Program.cs` en un après-midi. C'est un assistant RAG d'environ 250 lignes,
tout dans un seul fichier, qui répond aux questions des salariés à partir des fiches Markdown de
`depart/corpus/`. Certaines fiches sont réservées aux RH ou à la direction.

Le programme fonctionne, et vous pouvez l'essayer. Il est même en production sur l'intranet. Trois
demandes arrivent maintenant :

1. L'équipe IA veut remplacer `bge-m3` par `nomic-embed-text` sur le service, pour économiser de la
   mémoire.
2. La DRH veut avoir la certitude qu'un salarié ne verra jamais un extrait de la grille des salaires.
3. Un audit demande de pouvoir dire, pour chaque réponse donnée, quel modèle, quel index et quelle
   version du prompt l'ont produite.

Votre travail consiste à rendre cette application maintenable. Elle doit pouvoir absorber ces trois
demandes sans être réécrite, et vous devez le prouver par des tests qui tournent sans IA.

## Ce qu'on vous demande

### 1. Diagnostic (15 min)

Lisez `depart/Program.cs` et son `.csproj`, puis dressez la liste de leurs défauts d'architecture au
regard du cours, en distinguant quatre catégories :

- Une règle métier mal placée.
- Une particularité de modèle qui a fui dans l'application.
- Une absence de frontière.
- Une absence de traçabilité.

Pour chaque défaut, indiquez aussi laquelle ou lesquelles des trois demandes il empêche de
satisfaire. Si un défaut n'en bloque aucune, dites-le simplement.

Un indice pour commencer : faites tourner
`ask "Quelle est la fourchette de salaire d'un consultant senior ?" --user alice`, puis la même
commande avec `--embed nomic`, sans réindexer. Regardez ce qui s'affiche, et aussi ce qui ne
s'affiche pas.

### 2. Refonte (45 min)

Réorganisez le code en Clean Architecture, dans un dossier `solution/` qui vous appartient. Ce qui
est évalué, c'est votre capacité à justifier chaque frontière, pas à la retaper.

Le temps prévu suppose donc que vous partiez du cœur fourni dans le dossier `coeur/`. Il reprend le
cœur du fil rouge sous une forme plus simple, réduite à ce dont le cas pratique a besoin et réunie en
un seul projet : la recherche, par exemple, reste dans `AskQuestion`. Il fournit déjà
le domaine, les ports et le cas d'usage `AskQuestion` avec ses refus, dont celui d'un index construit
avec un autre modèle. Il fournit aussi la trace `AnswerTrace`, un double pour chaque port et les
tests qui vont avec. Copiez le dossier `coeur/` dans votre `solution/` : vos 45 minutes vont alors
aux adaptateurs, à la racine de composition, au test de la règle de dépendance et à la note. Gardez
une dizaine de minutes pour la note. Vous pouvez aussi reprendre du code du dépôt principal.

#### Ce qu'on attend en 45 minutes

Ce minimum suffit pour obtenir tous les points du code et de la note d'architecture :

- Un domaine, avec les entités, la règle d'accès et la vérification des citations, le tout testé
  sans IA.
- Des ports définis par le cas d'usage, pour les embeddings, la génération, l'index et le corpus.
- Un cas d'usage `AskQuestion` qui filtre les droits **avant** de construire le prompt. Il refuse de
  répondre quand aucun passage n'est pertinent, vérifie les citations et refuse un index construit
  avec un autre modèle.
- Un manifeste d'index qui donne au moins le modèle et la dimension des vecteurs, et pour chaque
  réponse une trace qui donne l'index, les modèles et la version du prompt.
- Des adaptateurs pour le service IA, l'index et le corpus Markdown. Vous pouvez les tirer de
  `depart/Program.cs`, sans laisser remonter jusqu'au cas d'usage une particularité de modèle comme
  la balise `<think>`. Si vous partez du cœur fourni, ses ports demandent aussi trois petits
  adaptateurs : le découpage du texte (`ITextSplitter`), les prompts (`IPromptRepository`) et
  l'horloge (`IClock`).
- Une seule racine de composition, qui lit les modèles, le seuil et l'adresse du service dans la
  configuration.
- Un double de test pour chaque port, et au moins 8 tests xUnit qui passent sans service IA. L'un
  d'eux vérifie la règle de dépendance. Un autre inspecte le prompt envoyé au générateur, pour
  prouver que les droits sont filtrés avant. Le cœur fourni en donne un modèle,
  `Restricted_passages_never_reach_the_prompt`.
- Une note d'architecture d'une page. Pour chaque frontière, elle dit où elle passe, pourquoi à cet
  endroit et ce qu'elle ne parvient pas à isoler du modèle. Elle justifie votre découpage en projets
  et nomme ce que vous avez laissé de côté. Elle dit enfin ce que devient chacune des trois demandes
  une fois la refonte faite, et ce qui le montre : un test, un refus explicite ou une trace.

#### Pour aller plus loin

Les compléments suivants sont facultatifs. Ils ne rapportent pas de points, à part le bonus indiqué
sous la grille :

- L'empreinte du corpus et le découpage dans le manifeste, et une commande `status` qui dit si
  l'index est à refaire.
- Les passages retrouvés et leurs scores dans la trace.
- Le prompt rangé dans un fichier versionné, avec une empreinte de son contenu.
- Les options `Nullable` et `TreatWarningsAsErrors` réactivées.
- Des décorateurs autour des ports, pour le journal, le cache ou les nouvelles tentatives.
- Un test qui vérifie que seule la racine de composition connaît les adaptateurs.

Ne pas tout faire est aussi un choix d'architecture. Dites dans la note ce que vous avez laissé de
côté, et pourquoi.

#### Combien de projets ?

Le dépôt principal sépare quatre projets : domaine, application, infrastructure et composition. La
règle de dépendance s'écrit alors dans les `ProjectReference`, et c'est le compilateur qui la fait
respecter. C'est une solution défendable, mais ce n'est pas la seule.

Un découpage plus léger est accepté à deux conditions : la note le justifie, et un test vérifie la
règle de dépendance. Le cœur fourni en donne un exemple : un seul projet, `Coeur`, qui réunit le
domaine, les ports et les cas d'usage, et qui ne référence rien. Dans votre solution, les
adaptateurs et la composition iraient alors dans un second projet. Comme avec quatre projets, le
test vérifie ce que le compilateur ne voit pas, par exemple que le cœur n'utilise pas le réseau.
Les tests `ArchitectureTests.Domain_depends_on_nothing_but_the_runtime` et
`ArchitectureTests.The_core_does_not_touch_the_network` du dépôt principal montrent comment lire les
références d'un assembly. La note dit aussi ce que ce découpage ne protège plus : dans un même
projet, rien n'empêche le domaine d'appeler un cas d'usage.

### 3. Rendu

Déposez le dossier `solution/` sur Moodle, ou donnez l'URL de votre dépôt Git. Il contient le code,
les tests, la note d'architecture et un README qui explique comment lancer la solution et ses tests.

## Lancer la version de départ

```bash
python -m ai_service                      # dans un autre terminal, depuis la racine du dépôt
cd cas-pratique/depart
dotnet run -- index                       # avec bge-m3 par défaut
dotnet run -- ask "Combien de jours de télétravail par semaine ?" --user alice
```

Sans Ollama, indexez avec `index --embed hashing`, puis interrogez avec
`ask … --embed hashing --model extractive`. L'application répond alors « aucun document » à toutes
les questions. Le seuil de 0,65, écrit en dur, a été réglé pour `bge-m3`, et les scores obtenus par
hachage restent en dessous. C'est le premier défaut à noter dans votre diagnostic.

Le dossier `depart/corpus/` est une copie du corpus Solvéo : neuf fiches, dont trois réservées. Le
projet est volontairement laissé hors de la solution `FycAssistantRag.sln`. Il ne partage aucun code
avec le fil rouge, seulement le service IA qu'il appelle. Son `.csproj` fixe lui-même toutes ses
options : vous pouvez copier le dossier `depart/` hors du dépôt, il compile de la même façon.

## Vérifier le cœur fourni

```bash
cd cas-pratique/coeur
dotnet test
```

Les tests passent sans service IA ni réseau. Le dossier `coeur/` est lui aussi hors de la solution
`FycAssistantRag.sln`. Il a sa propre solution, `Coeur.sln`, et son propre `Directory.Build.props`,
qui fixe ses options : copié dans votre `solution/`, il compile et passe ses tests de la même façon.

## Grille d'évaluation (sur 20)

La justification compte autant que le résultat. La note d'architecture vaut 8 points, autant que le
code, et le diagnostic en vaut 4.

| Critère | Points | Ce qu'on regarde |
|---|---|---|
| Diagnostic | 4 | Chaque défaut est rangé dans la bonne catégorie et relié aux demandes qu'il bloque, s'il en bloque. Le corrigé donne les défauts attendus au minimum. |
| Code : règle de dépendance, ports et composition | 3 | Le cœur ne dépend ni des adaptateurs ni de la composition. Les `ProjectReference` montrent ce que le découpage choisi sépare, et un test vérifie le reste, par exemple que le cœur n'utilise pas le réseau. Les ports sont des interfaces définies par le besoin du cas d'usage, et un seul endroit assemble le tout. Le cas d'usage ne connaît aucune particularité de modèle, et on change de modèle par la configuration, sans toucher au cœur. |
| Code : règles métier dans le domaine, testées sans IA | 3 | L'accès, les citations et les refus sont des fonctions pures, avec des tests déterministes. Les droits sont appliqués avant le prompt : le test inspecte le prompt envoyé, pas la réponse. |
| Code : traçabilité | 2 | Le manifeste de l'index donne au moins le modèle et la dimension, et un index incompatible est refusé explicitement. Chaque réponse a sa trace, avec l'index, les modèles et la version du prompt. |
| Note d'architecture : justification des frontières | 6 | Pour chaque frontière, la note dit où elle passe, pourquoi à cet endroit et ce qu'elle ne parvient pas à isoler du modèle. Le découpage est argumenté, quel que soit le nombre de projets. Si le domaine et le cas d'usage partagent un projet, la note dit ce que ce découpage ne protège plus. Ce qui a été laissé de côté est nommé, avec sa raison. Un choix différent du corrigé n'est pas pénalisé s'il est défendu. |
| Note d'architecture : les trois demandes | 2 | La note dit concrètement ce que devient chaque demande après la refonte, et ce qui le montre : un test, un refus explicite ou une trace. |

Un point de bonus, qui peut porter la note au-delà de 20, récompense une commande `status` ou un
instantané qui montre qu'un changement de corpus, de découpage ou de modèle est bien détecté.

## Corrigé de référence

Le corrigé est dans [`corrige.md`](corrige.md). Lisez-le après votre rendu.
