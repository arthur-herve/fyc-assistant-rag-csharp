# Cas pratique S5.1 — Rendre maintenable une application IA existante (C#)

Durée indicative : 1 h de travail, 30 min de correction commentée en vidéo (S5.2).

## Situation

Un collègue a écrit en un après-midi `depart/Program.cs` : un assistant RAG d'environ 250 lignes,
un seul fichier, qui répond aux questions des salariés à partir des fiches Markdown de
`depart/corpus/`, avec des fiches réservées aux RH et à la direction. **Il fonctionne** (essayez-le),
il est en production sur l'intranet, et trois demandes arrivent :

1. l'équipe IA veut remplacer `bge-m3` par `nomic-embed-text` sur le service pour économiser de
   la mémoire ;
2. la DRH veut la certitude qu'un salarié ne voit jamais un extrait de la grille des salaires ;
3. un audit demande, pour chaque réponse donnée, de pouvoir dire quel modèle, quel index et
   quelle version du prompt l'ont produite.

Vous devez rendre cette application maintenable, c'est-à-dire capable d'absorber ces trois
demandes **sans réécriture**, et le prouver par des tests qui tournent sans IA.

## Ce qu'on vous demande

### 1. Diagnostic (15 min)

Lire `depart/Program.cs` (et son `.csproj`) et lister ses défauts d'architecture au regard du
cours, en distinguant : ce qui est une **règle métier mal placée**, ce qui est une **particularité
de modèle qui a fui**, ce qui est une **absence de frontière**, et ce qui est une **absence de
traçabilité**. Pour chaque défaut, dire quelle demande (1, 2 ou 3) il empêche de satisfaire.

Indice : faites tourner `ask "Quelle est la fourchette de salaire d'un consultant senior ?"
--user alice`, puis la même chose avec `--embed nomic` sans réindexer, et regardez ce qui
s'affiche — et ce qui ne s'affiche pas.

### 2. Refonte (45 min)

Réorganiser le code en Clean Architecture, dans un dossier `solution/` à vous :

- un **domaine** : entités, règle d'accès, vérification des citations — testés sans IA ;
- des **ports** définis par le cas d'usage (embeddings, génération, index, corpus, prompts) ;
- un **cas d'usage** `AskQuestion` qui filtre les droits **avant** le prompt, refuse sans passage
  pertinent, vérifie les citations, et refuse un index construit avec un autre modèle ;
- des **adaptateurs** (HTTP vers le service IA, index avec manifeste, corpus Markdown, prompt
  versionné) et une **racine de composition** unique ;
- des **doubles** de test pour chaque port, et au moins 8 tests xUnit qui passent sans service IA ;
- une **note d'architecture** d'une page : où passent les frontières, pourquoi là, et ce que
  chacune des trois demandes devient une fois la refonte faite.

Vous pouvez reprendre du code du dépôt principal ; ce qui est évalué est votre capacité à
**justifier** chaque frontière, pas à la retaper. En C#, la structure attendue est celle de
projets séparés (au moins domaine / application / infrastructure / composition) : la règle de
dépendance s'écrit dans les `ProjectReference`, et le compilateur la fait respecter.

### 3. Rendu

Le dossier `solution/` (code, tests, note) déposé sur Moodle, ou l'URL de votre dépôt Git.

## Grille d'évaluation (sur 20)

| Critère | Points | Ce qu'on regarde |
|---|---|---|
| Diagnostic | 4 | Les défauts sont nommés avec la bonne catégorie et reliés aux demandes. Au moins : droits filtrés après le top-k et confiés au prompt (et identifiant du morceau réservé, avec son score, écrit dans la console) ; citations non vérifiées contre les passages fournis ; changement de modèle d'embeddings sans réindexation silencieux ; balise `<think>` traitée dans l'application ; seuil, modèles et utilisateurs en champs statiques globaux ; aucun manifeste ni trace ; état global `INDEX` ; `Environment.Exit` dans la logique ; avertissements du compilateur désactivés ; aucun test. |
| Règle de dépendance | 4 | Le domaine ne référence rien ; l'application ne référence que le domaine ; les `ProjectReference` le disent et un test (réflexion sur les assemblies) ou, à défaut, la note l'explique. |
| Règles métier dans le domaine, testées sans IA | 4 | Accès, citations, refus : fonctions pures, tests déterministes. Les droits sont appliqués avant le prompt (le test inspecte le prompt envoyé, pas la réponse). |
| Ports, adaptateurs, composition | 3 | Les ports sont des interfaces définies par le besoin du cas d'usage ; un seul endroit assemble ; on change de modèle de génération par la configuration sans toucher au cœur. |
| Traçabilité | 3 | Manifeste de l'index (modèle concret, dimension, empreinte du corpus, découpage) ; refus explicite d'un index incompatible ; trace par réponse (index, modèles, version du prompt, passages, scores). |
| Note d'architecture et qualité | 2 | Une page claire ; les trois demandes ont une réponse concrète ; README de lancement ; noms et découpage lisibles ; avertissements traités en erreurs. |

Bonus (+1, non plafonné à 20) : une commande `status` ou un instantané qui montre qu'un changement
de corpus, de découpage ou de modèle est détecté.

## Corrigé de référence

Le dépôt principal (`src/`, `ai_service/`, `tests/`) est la solution de référence ; la
correction commentée (vidéo S5.2) parcourt le tableau suivant, défaut par défaut.

| Défaut dans `depart/Program.cs` | Catégorie | Où c'est corrigé dans le dépôt |
|---|---|---|
| Les droits sont appliqués **après** le top-k (`Search`) : un passage réservé consomme une place, puis est retiré — en écrivant son identifiant et son score dans la console de l'utilisateur ; et le prompt demande au modèle de « ne pas utiliser » les passages marqués (réservé) | règle métier mal placée | `AccessPolicy` (`Assistant.Domain/Rules.cs`) + prédicat passé à `IVectorIndex.Search` dans `SearchPassages` ; ADR 0006 |
| Les citations sont extraites par regex sans vérifier qu'elles renvoient à un passage fourni (`[7]` passe) ; une réponse sans citation est réessayée une fois puis affichée telle quelle | règle métier absente | `Citations.Check`, statut `Unsourced` ; `OutputRules` pour la forme |
| `--embed nomic` sans réindexer : `Zip` tronque les vecteurs, les scores deviennent du bruit, l'application répond « aucun document » sans erreur | absence de frontière + traçabilité | `IndexManifest` + `IndexModelMismatchException` ; ADR 0003 ; commandes `status` et `index --if-stale` |
| `Regex.Replace("<think>…")` dans `Generate` | particularité de modèle qui a fui | `ai_service/backends/ollama.py` (réflexion renvoyée à part, budget séparé) ; ADR 0001, 0009 |
| `THRESHOLD = 0.65` « ajusté à la main », valable pour un seul modèle et un seul corpus | règle métier mal placée | `"retrieval": { "min_score": … }` par modèle et par corpus, calibré par `benchmark` ; ADR 0004 |
| `PROMPT` constante, ni versionnée ni tracée | absence de traçabilité | `prompts/answer.json`, version + empreinte dans `AnswerTrace` ; ADR 0005 |
| `EMBED_MODEL`, `GEN_MODEL`, `USERS`, `AI_URL` en champs statiques globaux, dont deux réassignés par `Main` ; `INDEX` global ; `Environment.Exit` dans la logique | absence de frontière | `Composition.cs` (seul endroit qui connaît tout), `AppConfig`, exceptions typées attrapées dans `Program.Main` |
| `index.bin` sans manifeste : l'index ne sait pas de quoi il est dérivé | absence de traçabilité | `JsonVectorIndex` (JSON lisible + manifeste) ; `docs/artefacts.md` |
| Corpus parsé, découpé, vectorisé, recherché et généré dans la même classe statique ; aucun test possible sans service IA ; `TreatWarningsAsErrors` et `Nullable` désactivés dans le `.csproj` | absence de frontière | ports `IDocumentSource`, `ITextSplitter`, `IEmbedder`, `IVectorIndex`, `IGenerator` ; doubles dans `tests/Assistant.Tests/Fakes.cs` ; 91 tests sans réseau ; `Directory.Build.props` |

Les trois demandes, après refonte : (1) changer d'embeddings = changer un alias dans la
configuration, réindexer, et l'application refuse tant que ce n'est pas fait ; (2) la
confidentialité est une règle du domaine testée sur le prompt envoyé, fuites = 0 mesurées par le
banc d'essai ; (3) chaque réponse porte sa trace, chaque index son manifeste.

## Lancer la version de départ

```bash
python -m ai_service                      # dans un autre terminal, depuis la racine du dépôt
cd cas-pratique/depart
dotnet run -- index                       # avec bge-m3 par défaut
dotnet run -- ask "Combien de jours de télétravail par semaine ?" --user alice
```

Sans Ollama : `index --embed hashing` puis `ask … --embed hashing --model extractive`. L'application
répond alors « aucun document » à tout : le seuil `0.65` codé en dur a été réglé pour `bge-m3` et
les scores du hachage restent sous 0,65 (0,53 au mieux sur ces questions). C'est le premier défaut à noter dans le diagnostic.

`depart/corpus/` est une copie du corpus Solvéo (9 fiches, dont 3 réservées). Le projet est
volontairement hors de la solution `FycAssistantRag.sln` : il ne partage rien avec le fil rouge.
