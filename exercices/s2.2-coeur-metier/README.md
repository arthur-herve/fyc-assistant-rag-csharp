# Exercice S2.2 — Construire un cœur métier sans IA (C#)

Durée indicative : 45 minutes · Exercice guidé, code à écrire, tests fournis · Corrigé dans `solution/`.

## Ce que vous avez

Le dossier `depart/` est une solution .NET autonome : aucune IA, aucun réseau, aucune référence
au fil rouge, aucun paquet en dehors de xUnit.

```
depart/
  Coeur.sln
  Coeur/
    Model.cs            les entités (Document, Chunk, User, Passage, Answer, AnswerTrace…)      — fourni
    Ports.cs            ce dont les cas d'usage ont besoin, sans dire comment (IEmbedder,
                        IGenerator, IVectorIndex, IPromptRepository…)                          — fourni
    Errors.cs           les erreurs du domaine et de l'application                              — fourni
    IndexCorpus.cs      le cas d'usage d'indexation, à lire comme exemple                       — fourni
    Access.cs           règle métier n° 1 : qui peut lire quoi                                  — À ÉCRIRE
    Citations.cs        règle métier n° 2 : toute réponse cite ses sources                      — À ÉCRIRE
    AskQuestion.cs      le cas d'usage central : répondre à une question                        — À ÉCRIRE
  Coeur.Tests/
    Fakes.cs            des doubles pour chaque port : embeddings « par mots-clés », générateur
                        scripté, index en mémoire, corpus en liste, prompts fixes               — fourni
    DomainTests.cs      7 tests des deux règles métier                                          — fournis
    AskQuestionTests.cs 13 tests qui décrivent le cas d'usage                                   — fournis
```

Lancer les tests depuis `depart/` :

```bash
dotnet test
```

Au départ, 19 tests échouent sur `NotImplementedException` (un seul passe : il ne teste que
`PromptTemplate.Render`, qui est fourni). À la fin, tout est vert — **sans avoir appelé un seul
modèle**.

## Ce qu'on vous demande

1. `Coeur/Access.cs` — `AccessPolicy.CanRead` : un morceau marqué `tous` est lisible par tout le
   monde ; sinon il faut un groupe en commun. Tests : `DomainTests.cs`, classe `AccessPolicyTests`.
2. `Coeur/Citations.cs` — `Citations.Check(text, passageCount)` : reconnaît `[1]`, `[2, 3]`,
   `[2,3]` ; renvoie les numéros valides sans doublon et les numéros invalides ; un nombre trop
   grand pour un `int` est invalide, pas une exception. Tests : `DomainTests.cs`, classe `CitationsTests`.
3. `Coeur/AskQuestion.cs` — `FormatPassages` puis `Execute`, en suivant le déroulé décrit dans le
   fichier (question vide → index absent → modèle incompatible → recherche filtrée par les droits →
   seuil → prompt → tentatives → réponse sourcée ou refus). Tests : `AskQuestionTests.cs` (13 tests).

Ordre conseillé : 1, 2, puis 3 en faisant passer les tests un par un, dans l'ordre du fichier
(`dotnet test --filter "FullyQualifiedName~Answers_with_cited_sources"` pour n'en lancer qu'un).

## Questions à se poser en chemin (reprises en correction)

- `AskQuestion` utilise `AccessPolicy` et `Citations`, mais le projet `Coeur` ne référence ni
  `Coeur.Tests` ni `System.Net.Http`. Comment les tests arrivent-ils quand même à faire répondre le
  cas d'usage ?
- Pourquoi le test `Restricted_passages_never_reach_the_prompt` inspecte-t-il le **prompt envoyé**
  au générateur, plutôt que la réponse ?
- Que se passerait-il si la vérification des citations était confiée au prompt (« cite tes
  sources ») au lieu d'être une fonction du domaine ?
- Les tests vérifient `IndexModelMismatchException`. Pourquoi le cas d'usage compare-t-il le modèle
  à **chaque** question, et pas seulement à l'indexation ?
- Bonus C# : `Coeur.csproj` ne contient aucune `ProjectReference`. Qu'est-ce que le compilateur
  vous interdit, et en quoi est-ce la règle de dépendance ?

## Corrigé

`solution/` contient les trois fichiers. Copiez-les dans `depart/Coeur/` pour vérifier : 20 tests
verts. Ils sont identiques, au namespace près, à `src/Assistant.Domain/Rules.cs` et
`src/Assistant.Application/AskQuestion.cs` du dépôt (le fil rouge délègue en plus la recherche à
un cas d'usage `SearchPassages`, réutilisé par le banc d'essai) : ce que vous venez d'écrire est
**le cœur réel** de l'assistant, celui qui tourne en séquence 2.3 derrière de vrais modèles.

Réponses aux questions :

- **Les doubles.** Les tests construisent `AskQuestion` avec des objets de `Fakes.cs` qui
  implémentent les *ports* (`KeywordEmbedder : IEmbedder`, `ScriptedGenerator : IGenerator`,
  `FakeIndex : IVectorIndex`, `StaticPrompts : IPromptRepository`). Le cas d'usage ne voit que les
  interfaces de `Ports.cs` : un vrai adaptateur HTTP ou un double en mémoire, c'est pareil pour
  lui. C'est l'inversion des dépendances : le cœur définit ce dont il a besoin, l'extérieur s'y
  conforme — et en C#, un double *déclare* qu'il implémente le port, le compilateur le vérifie.
- **Le prompt plutôt que la réponse.** La règle métier dit qu'un document interdit ne doit jamais
  *atteindre* le modèle, pas seulement ne jamais être cité. Un générateur scripté peut renvoyer
  n'importe quoi ; ce qui compte, c'est ce qu'il a reçu. Le test inspecte donc les requêtes
  enregistrées par `ScriptedGenerator.Requests`.
- **Citations par le prompt.** Le prompt est une consigne, pas une garantie : le modèle peut
  l'ignorer, inventer `[7]`, ou citer sans avoir lu. `Citations.Check` est déterministe, testable
  en microsecondes, et c'est elle qui décide du statut de la réponse. Le prompt tente d'obtenir
  une sortie acceptable ; le domaine vérifie qu'elle l'est.
- **À chaque question.** Le service IA peut changer de modèle entre deux appels (mise à jour des
  poids derrière le même alias, autre machine). L'index, lui, ne change pas. La seule vérité
  disponible est l'identifiant renvoyé *maintenant* par le service, comparé au manifeste de
  l'index construit *avant*. Voir ADR 0003.
- **Aucune référence.** `Coeur` ne peut utiliser ni `HttpClient` (il faudrait `System.Net.Http`,
  disponible mais ce serait visible dans le code), ni les doubles, ni un client Ollama. La règle
  de dépendance du fil rouge est faite de ces interdictions-là, projet par projet ; ici tout tient
  dans un seul projet, mais il ne dépend de rien.

## Pour aller plus loin

Écrire un test qui vérifie que la trace (`AnswerTrace`) d'un refus « aucune source pertinente »
contient bien les passages retrouvés et leurs scores : c'est ce qui permettra, en séquence 3.2,
de recalibrer le seuil sans rejouer les questions.
