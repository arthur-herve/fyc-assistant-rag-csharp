# ADR 0009 — Application en C#, modèles en Python : la frontière est le contrat, pas le langage

**Date** : 11/09/2026 (décision), 12/09/2026 (rédaction) · **Statut** : acceptée · errata du 21/09/2026 et du 04/10/2026 (en fin de document)

## Contexte

La problématique promet des exemples « sur des piles accessibles (C#/.NET et Python) » et le
cours accepte des apprenants venant de l'un ou l'autre langage. Le fil rouge existait en Python
(`fyc-assistant-rag-python-full`) ; il fallait décider ce que serait une « version C# » : une
traduction complète, ou autre chose. Par ailleurs, l'ADR 0001 pose que le service IA est un
déployable séparé — mais tant qu'il est écrit dans le même langage que l'application, un
apprenant *pourrait* tricher en important `ai_service` directement, et rien ne l'empêcherait.

## Décision

Ce dépôt garde le **service IA en Python, tel quel** (copie de `ai_service/` et de ses tests) et
réécrit **l'application en C# / .NET 8** : `Assistant.Domain`, `Assistant.Application`,
`Assistant.Infrastructure`, `Assistant.Cli`. Les deux ne partagent que le contrat HTTP
(`docs/contrat-http.md`), les fichiers de données (corpus, questions, prompts) et les formats
d'index et d'instantané. Il n'y a pas de service IA en C#.

Les ports restent dans la couche application (`Assistant.Application/Ports.cs`), pas dans le
domaine : ce sont les cas d'usage qui ont besoin d'un modèle, et `Assistant.Domain` ne
référence aucun autre projet — pas même une interface vers l'extérieur. L'exemple de la
séquence 1.3 (`exemples/s1.3-transfert-naif/`) montre la variante « port dans le domaine » et
pourquoi elle ne suffit pas.

## Conséquences

- **La règle de dépendance est imposée par le compilateur** (références de projets : Domain →
  rien, Application → Domain, Infrastructure → Application, Cli → tout) et vérifiée en plus par
  `ArchitectureTests`. En Python, elle n'était qu'une convention testée.
- **Tricher est impossible** : l'application ne peut pas importer le code du modèle, il n'existe
  pas dans son langage. La seule voie est le contrat.
- **Le couplage par les données, lui, ne disparaît pas** : le même fichier d'index se lit en C#
  et en Python (`JsonVectorIndexTests` sur une fixture produite par Python, même `index_id`), et
  un instantané C# se compare à un instantané Python avec 0 % de dérive à configuration égale.
  Ce qui doit correspondre entre l'index et le service, c'est le modèle d'embeddings, pas le
  langage. C'est la nuance de l'ADR 0001, rendue tangible.
- **Coût pour l'apprenant** : deux runtimes à installer (.NET 8 SDK **et** Python 3.11+), là où
  les rendus disaient « C# ou Python ». Le guide d'installation (`docs/installation.md`) mesure
  les deux ; le mode hors-ligne ne demande aucun modèle.
- **Deux dépôts pour un cours** : le dépôt Python reste la référence complète ; celui-ci porte
  la même application, le même banc d'essai, les mêmes expériences, les mêmes exercices, et en
  plus la démonstration que la frontière tient jusqu'au langage.

## Écarté

- **Un service IA en C#** (HttpListener + backend Ollama) : aurait rendu le dépôt autonome sans
  Python, mais aurait effacé l'argument central (deux langages, un contrat) et doublé le code
  à maintenir pour les particularités des modèles (préfixes, réflexion, empreintes).
- **Une bibliothèque .NET d'IA** (`Microsoft.Extensions.AI`, client Ollama) importée dans
  l'application : plus court, mais la frontière réseau de l'ADR 0001 aurait disparu dans un
  paquet, et l'ADR 0007 (aucun paquet tiers) aurait cédé.
- **Les ports dans le domaine**, comme le dit l'opinion admise : compile, fonctionne, et fait
  dépendre le domaine d'une interface vers le monde extérieur. Le domaine du fil rouge a des
  règles (droits, citations, forme) qui ne dépendent d'aucun modèle ; il n'a pas à connaître
  l'existence d'un générateur.

## Erratum (21/09/2026)

« Il n'a pas à connaître l'existence d'un générateur » est trop fort : le domaine ne dépend
d'aucun générateur ni d'aucune technologie d'IA, mais `AnswerTrace` (`Model.cs`) nomme le modèle
d'embeddings et le modèle de génération qui ont produit la réponse. C'est une fuite assumée : une
réponse d'un assistant RAG vient de modèles identifiés, et la tracer est une règle du métier
(S4.2). De même, la règle de forme (`OutputRules`, `Rules.cs`) contient une connaissance des modèles
rencontrés (marqueurs de raisonnement de qwen3), assumée et datée (ADR 0008) : les règles du domaine
ne dépendent d'aucune technologie d'IA, pas de toute connaissance des modèles. Les ports, eux,
restent hors du domaine, comme décidé ici.

## Erratum (04/10/2026)

Le point « Le couplage par les données, lui, ne disparaît pas » des Conséquences affirme ce qui
ne tient plus : le même `index_id` en C# et en Python (`JsonVectorIndexTests` sur une fixture
produite par Python), et un instantané C# qui se compare à un instantané Python avec 0 % de dérive
à configuration égale. Il n'y aura qu'une version du cours : cette comparaison ne sert plus.
L'identifiant d'un index se calcule désormais sur un JSON écrit par System.Text.Json
(`Fingerprints.IndexId` : empreinte du corpus, modèle d'embeddings, dimension, découpage aux clés
triées) au lieu d'imiter `json.dumps` de Python ; il n'est donc plus celui de la version Python, et
un index construit avant le 04/10/2026 est vu « à refaire » par `status`. La fixture et les tests
qui comparaient les deux versions sont retirés.

Ce qui tient toujours : la frontière est le contrat, pas le langage. L'application C# ne parle au
service IA en Python que par le contrat HTTP, et le couplage par les données demeure : ce qui doit
correspondre entre l'index et le service, c'est le modèle d'embeddings (vérifié à chaque question,
ADR 0003), pas le langage.
