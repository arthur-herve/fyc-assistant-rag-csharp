# Fil rouge, correction de S2.2 : le cœur sans IA

> **Correction de la séquence S2.2.** Ce tag, `corrige/s2.2`, est l'état du fil rouge à la fin de la
> séquence. Il sert à comparer votre travail et à récupérer les tests attendus. L'application complète,
> avec toute sa documentation, est sur la branche `final`.

## Où en est le fil rouge

Le cœur est écrit : le domaine, avec ses deux règles métier, et les deux cas d'usage de l'application,
qui ne connaissent le modèle, l'index et les fichiers que par leurs ports. Il se teste en entier avec
les doubles de `tests/Assistant.Tests/Fakes.cs`, sans modèle, sans réseau et sans disque. Tout ce qui
l'entoure manque encore : `Assistant.Infrastructure` est vide, et `Assistant.Cli` ne fait qu'annoncer
l'application. La séquence S2.3 branche ce cœur sur le corpus, l'index et le service IA. Elle pose
une question : peut-on ensuite changer de modèle sans toucher au cœur ?

## Ce que la correction a changé

Au départ de la séquence, les deux règles métier de `src/Assistant.Domain/Rules.cs` et le cas d'usage
de `src/Assistant.Application/AskQuestion.cs` levaient `NotImplementedException`. La correction les
écrit dans leur version la plus simple. La question vide est refusée, et la recherche est filtrée par
les droits, puis par le seuil de pertinence, avant la construction du prompt. Sans passage pertinent, le
cas d'usage refuse sans appeler le modèle. Sinon, il appelle le générateur, et une réponse aux citations
invalides devient une réponse non sourcée, accompagnée des passages.

Les tests attendus sont dans `DomainTests.cs` et `AskQuestionTests.cs`, et le test de référence de la
trace d'un refus dans `AnswerTraceTests.cs`. Pour lire la correction dans le code et dans les tests :

```bash
git diff origin/etape/s2.2 corrige/s2.2 -- src tests
```

Le corrigé de l'énoncé, à la fin de
[`exercices/s2.2-coeur-metier/README.md`](exercices/s2.2-coeur-metier/README.md), répond aux questions
de la séquence.

## Démarrer

Si votre poste n'est pas encore prêt, suivez [`docs/installation.md`](docs/installation.md). Vérifiez
ensuite les deux programmes :

```bash
dotnet test
python -m unittest discover -s tests_python -t .
```

Les deux commandes doivent se terminer sans échec. Pour ne lancer qu'une classe de tests, filtrez-la
par son nom :

```bash
dotnet test --filter "FullyQualifiedName~AskQuestionTests"
```

Il n'y a pas encore d'application à lancer. Le service IA, `python -m ai_service`, ne sert pour
l'instant qu'à l'exemple de S1.3.

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

- Le domaine ne dépend de rien. Les ports sont déclarés dans l'application, à côté des cas d'usage qui
  s'en servent.
- L'index vit dans l'application, derrière le port `IVectorIndex`, et le service IA reste un programme
  à part, joint par le contrat de `docs/contrat-http.md`.
- Le seuil de pertinence est un réglage du cas d'usage.
- Les droits sont filtrés avant le prompt, et le prompt est construit dans l'application.
