# ADR 0009 — Application en C#, modèles en Python : la frontière est le contrat, pas le langage

Date : décision prise le 11/09/2026, rédigée le 12/09/2026

Statut : acceptée. Quatre errata, du 21/09/2026, du 04/10/2026, du 08/10/2026 et du 01/11/2026,
figurent en fin de document.

## Contexte

La problématique du cours promet des exemples « sur des piles accessibles (C#/.NET et Python) », et
le cours accueille des apprenants venus de l'un ou l'autre langage. Le fil rouge existait en Python
(`fyc-assistant-rag-python-full`). Il fallait décider ce que serait une « version C# » : une
traduction complète, ou autre chose.

L'ADR 0001 pose par ailleurs que le service IA est un déployable séparé. Mais tant qu'il est écrit
dans le même langage que l'application, un apprenant pourrait tricher en important `ai_service`
directement, et rien ne l'en empêcherait.

## Décision

Ce dépôt garde le service IA en Python, tel quel, avec une copie de `ai_service/` et de ses tests.
Il réécrit l'application en C# avec .NET 8, en quatre projets : `Assistant.Domain`,
`Assistant.Application`, `Assistant.Infrastructure` et `Assistant.Cli`. Les deux parties ne
partagent que le contrat HTTP (`docs/contrat-http.md`), les fichiers de données (corpus, questions,
prompts) et les formats d'index et d'instantané. Il n'y a pas de service IA en C#.

Les ports restent dans la couche application (`Assistant.Application/Ports.cs`), pas dans le
domaine. Ce sont les cas d'usage qui ont besoin d'un modèle, et `Assistant.Domain` ne référence
aucun autre projet : il ne contient même pas d'interface vers l'extérieur. L'exemple de la séquence
1.3 (`exemples/s1.3-transfert-naif/`) montre la variante où le port est déclaré dans le domaine, et
pourquoi elle ne suffit pas.

## Conséquences

- La règle de dépendance est imposée par le compilateur, au moyen des références entre projets.
  Domain ne dépend de rien, Application dépend de Domain, Infrastructure d'Application, et Cli de
  tout le reste. `ArchitectureTests` la vérifie en plus. En Python, ce n'était qu'une convention
  vérifiée par des tests.
- Tricher est impossible. L'application ne peut pas importer le code du modèle, puisqu'il n'existe
  pas dans son langage. La seule voie est le contrat.
- Le couplage par les données, lui, ne disparaît pas. Le même fichier d'index se lit en C# et en
  Python (`JsonVectorIndexTests`, sur une fixture produite par Python, avec le même `index_id`), et
  un instantané C# se compare à un instantané Python avec 0 % de dérive à configuration égale. Ce
  qui doit correspondre entre l'index et le service, c'est le modèle d'embeddings, pas le langage.
  C'est la nuance de l'ADR 0001, rendue tangible.
- Le coût pour l'apprenant : deux environnements à installer, le SDK .NET 8 et Python 3.11+, là où
  les rendus annonçaient « C# ou Python ». Le guide d'installation (`docs/installation.md`) détaille
  les deux. Le mode hors-ligne ne demande aucun modèle.
- Deux dépôts pour un cours. Le dépôt Python reste la référence complète. Celui-ci porte la même
  application, le même banc d'essai, les mêmes expériences et les mêmes exercices, et il montre en
  plus que la frontière tient jusqu'au langage.

## Écarté

- Un service IA en C#, avec HttpListener et un backend Ollama. Le dépôt serait devenu autonome,
  sans Python, mais l'argument central aurait disparu : deux langages, un seul contrat. Il aurait
  aussi fallu maintenir deux fois le code qui gère les particularités des modèles, comme les
  préfixes, la réflexion ou les empreintes.
- Une bibliothèque .NET d'IA importée dans l'application, comme `Microsoft.Extensions.AI` ou un
  client Ollama. Le code aurait été plus court, mais la frontière réseau de l'ADR 0001 aurait
  disparu dans un paquet, et l'ADR 0007, qui n'admet aucun paquet tiers, aurait cédé.
- Les ports dans le domaine, comme le veut l'opinion admise. Cela compile et cela fonctionne, mais
  le domaine dépend alors d'une interface tournée vers le monde extérieur. Le domaine du fil rouge a
  des règles (droits, citations, forme) qui ne dépendent d'aucun modèle. Il n'a pas à connaître
  l'existence d'un générateur.

## Erratum (21/09/2026)

La phrase « Il n'a pas à connaître l'existence d'un générateur » va trop loin. Le domaine ne dépend
d'aucun générateur ni d'aucune technologie d'IA, mais `AnswerTrace` (`Model.cs`) nomme le modèle
d'embeddings et le modèle de génération qui ont produit la réponse. C'est une fuite assumée : une
réponse d'un assistant RAG vient de modèles identifiés, et la tracer est une règle du métier (S4.2).

De même, la règle de forme (`OutputRules`, dans `Rules.cs`) contient une connaissance des modèles
rencontrés : les marqueurs de raisonnement de qwen3. Elle est assumée et datée (ADR 0008). Les
règles du domaine ne dépendent d'aucune technologie d'IA, ce qui ne veut pas dire qu'elles ignorent
tout des modèles. Les ports, eux, restent hors du domaine, comme décidé ici.

## Erratum (04/10/2026)

Le point « Le couplage par les données, lui, ne disparaît pas » des Conséquences affirme deux
choses qui ne tiennent plus : le même `index_id` en C# et en Python (`JsonVectorIndexTests` sur une
fixture produite par Python), et un instantané C# comparable à un instantané Python avec 0 % de
dérive à configuration égale. Il n'y aura qu'une version du cours, et cette comparaison ne sert
plus.

L'identifiant d'un index se calcule désormais sur un JSON écrit par System.Text.Json, au lieu
d'imiter le `json.dumps` de Python. `Fingerprints.IndexId` y place l'empreinte du corpus, le modèle
d'embeddings, la dimension et le découpage, dont les clés sont triées. Cet identifiant n'est donc plus
celui de la version Python, et `status` voit « à refaire » tout index construit avant le
04/10/2026. La fixture et les tests qui comparaient les deux versions ont été retirés.

Ce qui tient toujours : la frontière est le contrat, pas le langage. L'application C# ne parle au
service IA en Python que par le contrat HTTP. Et le couplage par les données demeure : ce qui doit
correspondre entre l'index et le service, c'est le modèle d'embeddings, pas le langage. Ce modèle
est vérifié à chaque question (ADR 0003).

## Erratum (08/10/2026)

Le point « Deux dépôts pour un cours » des Conséquences ne tient plus. Le cours n'aura qu'un dépôt,
celui-ci, et il ne s'appuie plus sur le dépôt Python. L'outil qui importe les fiches Service-Public
dans le corpus y a été repris, en Python comme le service IA (`tools/import_service_public.py`,
avec ses tests dans `tests_python/tools/`).

Les mesures prises le 11/09/2026 avec la version Python ne sont pas reprises : les seuils de
`nomic` et de `bge-m3` sur Solvéo et sur le corpus complet, et la réflexion de qwen3. Elles restent
citées avec leur date, et le banc d'essai permet de les refaire (`config/app.json` pour Solvéo,
`config/app-ollama-complet.json` pour le corpus complet).

Le Contexte et la Décision restent en l'état, parce qu'ils disent d'où vient ce dépôt. De même, les
mentions de la version Python dans les ADR 0001 à 0008 et 0010 disent d'où viennent ces décisions.
Elles ne renvoient à aucun fichier dont ce dépôt aurait besoin.

## Erratum (01/11/2026)

La Décision dit que l'exemple de la séquence 1.3 montre la variante où le port est déclaré dans le
domaine, « et pourquoi elle ne suffit pas ». La fin de la phrase ne tient plus. L'exemple est réduit
à sa première partie, où cette variante fonctionne : on y change de générateur sans toucher au
domaine. La panne silencieuse d'un changement de modèle d'embeddings n'y est plus montrée, parce que
le fil rouge la montre lui-même, sur la branche d'étape de la séquence 3.2 (ADR 0011).

La décision ne change pas : les ports du fil rouge restent dans la couche application, parce que ce
sont ses cas d'usage qui ont besoin d'un modèle. L'exemple pose désormais la question à l'apprenant :
où placer le port quand les règles métier et le cas d'usage vivent dans deux couches distinctes ?
