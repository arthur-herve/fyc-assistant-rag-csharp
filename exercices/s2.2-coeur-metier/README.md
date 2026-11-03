# Exercice S2.2 — Construire le cœur métier sans IA (C#)

Durée indicative : 45 minutes. L'exercice se fait dans le fil rouge, sur la branche `etape/s2.2`. Vous
écrivez les deux règles métier et le cas d'usage central. Vous écrivez aussi un test, avant tout le
reste, puis vous récupérez les tests attendus depuis la correction.

## Ce que vous avez

La branche contient la correction de S2.1 : la solution `FycAssistantRag.sln`, ses quatre projets et
leurs références, le modèle du domaine et les ports. `Assistant.Infrastructure` et `Assistant.Cli` sont
encore presque vides, puisque l'application n'arrive qu'en S2.3. Le départ de S2.2 y ajoute les erreurs,
les règles et les cas d'usage à compléter, et les doubles de test. Voici les fichiers qui comptent pour
l'exercice.

```
src/Assistant.Domain/
  Model.cs             les entités : Document, Chunk, User, Passage, Answer, AnswerTrace…
  Rules.cs             les règles métier : AccessPolicy.CanRead et Citations.Check sont à écrire
src/Assistant.Application/
  Ports.cs             ce dont les cas d'usage ont besoin, sans dire comment : IEmbedder, IGenerator,
                       IVectorIndex, IPromptRepository…
  Errors.cs            les erreurs de l'application
  IndexCorpus.cs       le cas d'usage d'indexation, à lire comme exemple
  AskQuestion.cs       le cas d'usage central : AskSettings est fourni, FormatPassages et Execute
                       sont à écrire
tests/Assistant.Tests/
  Fakes.cs             un double pour chaque port : embeddings par mots-clés, générateur scripté,
                       index en mémoire, corpus en liste, prompts fixes
  IndexCorpusTests.cs  des tests du cas d'usage d'indexation, écrits avec ces doubles
  ArchitectureTests.cs la règle de dépendance, vérifiée sur les projets et sur le code compilé
```

Au départ, `dotnet test` passe : les tests des règles et du cas d'usage ne sont pas encore là. À la fin,
tout doit passer, et vous n'aurez appelé aucun modèle, ni le réseau, ni le disque.

## Ce qu'on vous demande

1. Relisez votre découpage de S2.1 à la lumière de `docs/decoupage.md`, des quatre projets et de leurs
   références. Pour chaque port de `src/Assistant.Application/Ports.cs`, dites qui le déclare, qui
   l'implémente et pourquoi il vit dans l'application plutôt que dans le domaine.

2. Créez votre branche :

   ```bash
   git switch -c mon-s2.2 origin/etape/s2.2
   ```

3. Écrivez d'abord votre propre test, dans le fichier `tests/Assistant.Tests/MesTests.cs`, classe
   `MesTests`. Il vérifie que la trace d'un refus « aucune source pertinente » garde les passages
   retrouvés et leurs scores. Construisez le cas d'usage avec `Build.Ask` et un `ScriptedGenerator`, et
   choisissez une question qui partage un mot avec un document, mais trop peu pour atteindre le seuil.
   Trois mots de `Fakes.Vocabulary`, dont un seul figure dans le document, conviennent. Avec deux mots,
   le score tombe juste sous le seuil de 0,5, mais la trace l'arrondit et affiche 0,5. Vérifiez le
   statut, les passages de la trace avec leur score, et que le modèle n'a pas été appelé.
   Pour l'instant, le test échoue sur une `NotImplementedException` : c'est normal.

   Le fichier et la classe sont imposés. L'étape suivante écrit `DomainTests.cs` et
   `AskQuestionTests.cs` : un fichier à vous qui porterait l'un de ces noms serait écrasé sans un mot.
   Et une classe qui porterait le nom d'une classe restaurée, comme `AskQuestionTests`, ferait échouer
   la compilation même dans un autre fichier (erreur CS0101 : l'espace de noms contient déjà cette
   définition). Aucune classe `MesTests` n'existe dans le dépôt.

4. Récupérez les tests attendus, en nommant les fichiers :

   ```bash
   git restore --source corrige/s2.2 -- tests/Assistant.Tests/DomainTests.cs tests/Assistant.Tests/AskQuestionTests.cs
   ```

   Ils échouent tous, sauf ceux qui ne dépendent pas de ce que vous avez à écrire.

5. Faites-les passer un par un. Commencez par les deux règles métier, dans
   `src/Assistant.Domain/Rules.cs`. Leurs tests sont dans `DomainTests.cs`.

   - `AccessPolicy.CanRead(user, chunk)` : un morceau ouvert au groupe `tous` se lit par tout le monde.
     Pour les autres, l'utilisateur doit avoir au moins un groupe en commun avec le morceau.
   - `Citations.Check(text, passageCount)` reconnaît les citations de la forme `[1]`, `[2, 3]` ou
     `[2,3]`. Elle rend d'un côté les numéros valides, sans doublon, de l'autre les numéros qui ne
     renvoient à aucun passage fourni. Le type `CitationCheck` est fourni. Une sortie inattendue ne doit
     jamais faire lever d'exception : un numéro trop grand pour un `int` est une citation invalide.
     Seuls les chiffres de 0 à 9 comptent : un numéro écrit dans une autre écriture, comme `[١]`, n'est
     pas une citation.

   Passez ensuite au cas d'usage, dans `src/Assistant.Application/AskQuestion.cs`. Ses tests sont dans
   `AskQuestionTests.cs`.

   - `FormatPassages` numérote les passages à partir de 1. Chacun commence sur sa ligne par son numéro
     entre crochets et le titre du document, `[1] Politique de télétravail`, puis vient son texte à la
     ligne suivante. Une ligne vide sépare deux passages.
   - `Execute(user, question)` refuse une question vide (`EmptyQuestionException`), puis un index qui
     n'a pas été construit (`IndexNotBuiltException`). Elle cherche ensuite les passages proches de la
     question, en ne gardant que ceux que l'utilisateur a le droit de lire, et applique le seuil de
     pertinence d'`AskSettings`. Sans passage pertinent, elle rend un refus,
     `AnswerStatus.NoRelevantSource`, sans appeler le modèle. Sinon, elle construit le prompt, appelle
     le générateur et vérifie les citations. Une réponse correctement citée est rendue avec ses
     sources. Une autre est remplacée par le message `Messages.Unsourced`, accompagné des passages. Dans
     tous les cas, la trace garde les passages retrouvés, même sous le seuil, avec leur score, le seuil
     et les sorties brutes du modèle.

   Pour ne lancer qu'une classe de tests :

   ```bash
   dotnet test --filter "FullyQualifiedName~CitationsTests"
   ```

6. Vérifiez que le cœur ne dépend de rien d'extérieur :

   ```bash
   dotnet test --filter "FullyQualifiedName~ArchitectureTests"
   ```

   Ajoutez ensuite `using var client = new System.Net.Http.HttpClient();` au début d'`Execute` et
   relancez : `The_core_does_not_touch_the_network` échoue. Retirez la ligne.

7. Lisez la correction, puis comparez-la à votre travail :

   ```bash
   git diff origin/etape/s2.2 corrige/s2.2
   git diff corrige/s2.2
   ```

## Questions à se poser en chemin

Gardez-les en tête pendant que vous codez : le corrigé y répond.

- `AskQuestion` se sert d'`AccessPolicy` et de `Citations`, mais `Assistant.Application` ne référence ni
  les tests ni aucun adaptateur HTTP. Comment les tests arrivent-ils quand même à faire répondre le cas
  d'usage ?
- Le test des droits d'`AskQuestionTests.cs` inspecte le prompt envoyé au générateur plutôt que la
  réponse. Pourquoi ?
- Que se passerait-il si la vérification des citations était confiée au prompt (« cite tes sources »)
  au lieu d'être une fonction du domaine ?
- Votre test regarde la trace d'un refus. Qu'est-ce qu'elle permet de dire, sans reposer la question ?
- Bonus C# : `Assistant.Domain.csproj` ne contient aucune `ProjectReference`, et
  `Assistant.Application.csproj` une seule, vers le domaine. Qu'est-ce que le compilateur interdit
  alors au cœur ? Et pourquoi cela ne suffit-il pas, comme l'a montré l'étape 6 ?
