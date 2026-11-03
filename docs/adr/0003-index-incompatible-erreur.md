# ADR 0003 — Un index construit avec un autre modèle est une erreur, pas un avertissement

Date : 11/09/2026

Statut : acceptée. Reprise dans la version C# le 12/09/2026, avec des références de fichiers et des
mesures adaptées. Complétée par l'ADR 0010 le 21/09/2026, puis par l'ADR 0011 le 01/11/2026.

## Contexte

Les vecteurs d'un index vivent dans l'espace du modèle qui les a produits. Interroger un index
construit avec `bge-m3` à l'aide de vecteurs produits par `nomic` ne provoque pas toujours d'erreur.
À dimension égale, la recherche rend des scores plausibles et des résultats sans rapport avec la
question : c'est une panne silencieuse. `AssistantQR` avait choisi d'avertir par défaut et de
n'échouer qu'en mode strict, pour que la panne reste observable pendant le cours.

## Décision

À chaque question, `SearchPassages`, appelé par `AskQuestion`, compare l'identifiant et la dimension
du modèle renvoyés par le service à ceux qu'enregistre le manifeste de l'index. S'ils diffèrent, il
lève `IndexModelMismatchException`, et aucune option ne permet de désactiver ce contrôle. C'est la
démonstration du verdict « changer le modèle d'embeddings impose de réindexer ».

## Conséquences

- La panne silencieuse ne peut pas se produire dans l'application. En S3.2, on la raconte au lieu de
  la subir.
- Mettre à jour les poids derrière le même alias, avec `ollama pull`, change l'empreinte du modèle,
  donc son identifiant, et déclenche l'erreur. C'est voulu.
- Choisir avec `--embedding-model` un autre modèle que celui de l'index oblige à réindexer avant de
  poser une question. La commande `status` le signale avant que l'erreur ne survienne.

## Écarté

Avertir par défaut aurait été plus pédagogique pour montrer la panne. Mais un dépôt public, accessible
pendant cinq ans, ne doit pas contenir un comportement dangereux sous prétexte de démonstration.
