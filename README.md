# Fil rouge, correction de S2.1 : le découpage

> **Correction de la séquence S2.1.** Ce tag, `corrige/s2.1`, est l'état du fil rouge à la fin de la
> séquence. Il sert à comparer votre découpage à celui que retient le cours. L'application complète,
> avec toute sa documentation, est sur la branche `final`.

## Où en est le fil rouge

Le découpage est écrit en code : quatre projets, dont chacun ne référence que la couche suivante vers
l'intérieur, les entités du domaine et les ports de l'application. Il n'y a encore ni règle métier, ni
cas d'usage, ni adaptateur. `Assistant.Infrastructure` est vide, et `Assistant.Cli` ne fait qu'annoncer
l'application. La séquence S2.2 part de là, avec une question : peut-on écrire le cœur et le tester
sans modèle et sans réseau ?

## Ce que la correction a changé

Au départ de la séquence, le dépôt n'avait aucun code de l'application. La correction crée la solution
`FycAssistantRag.sln` et ses quatre projets, avec leurs références : le domaine ne référence rien,
l'application référence le domaine, l'infrastructure l'application, et la ligne de commande
l'infrastructure. Les entités sont dans `src/Assistant.Domain/Model.cs`, et les ports dans
`src/Assistant.Application/Ports.cs`. Des tests d'architecture vérifient la règle de dépendance, sur les
`.csproj` et sur le code compilé. `docs/decoupage.md` décrit ce découpage et justifie ses choix :
comparez-le à votre gabarit.

Pour lire la correction dans le code, dans les tests et dans sa description :

```bash
git diff origin/main corrige/s2.1 -- src tests docs/decoupage.md
```

## Démarrer

Si votre poste n'est pas encore prêt, suivez [`docs/installation.md`](docs/installation.md). Vérifiez
ensuite les deux programmes :

```bash
dotnet test
python -m unittest discover -s tests_python -t .
```

Les deux commandes doivent se terminer sans échec. Les tests C# vérifient pour l'instant la règle de
dépendance, sur les projets et sur le code compilé.

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

- Chaque couche est un projet. C'est le compilateur qui fait respecter la règle de dépendance, et des
  tests d'architecture la vérifient en plus.
- Le domaine ne dépend de rien. Les ports sont déclarés dans l'application, à côté des cas d'usage qui
  s'en serviront.
- L'index vit dans l'application, derrière le port `IVectorIndex`, et le service IA reste un programme
  à part, joint par le contrat de `docs/contrat-http.md`.
- Les droits sont filtrés pendant la recherche, donc avant le prompt : le port de l'index accepte un
  filtre. Le prompt est construit dans l'application.
