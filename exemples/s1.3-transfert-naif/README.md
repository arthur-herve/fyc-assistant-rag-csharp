# Séquence 1.3 — Le transfert naïf : « le modèle est un détail d'infrastructure »

Un programme de moins de 300 lignes, indépendant du fil rouge, qui fait **exactement** ce que dit
l'opinion couramment admise : déclarer un port dans le domaine, l'implémenter dans
l'infrastructure, substituer un second fournisseur. Puis qui montre où cela casse.

## Ce que vous avez

```
Domaine/Ports.cs            ITextGenerator, IEmbeddingProvider — les ports, déclarés dans le domaine
Domaine/Assistant.cs        Assistant (répond à partir de passages), NaiveIndex (index en mémoire)
Infrastructure/Providers.cs FakeGenerator, KeywordEmbeddings (sans réseau) ; HttpGenerator, HttpEmbeddings (service IA du fil rouge)
Program.cs                  generate · index · search
```

Le service IA est celui du fil rouge : `python -m ai_service` depuis la racine du dépôt (port 8100).
Sans lui, tout ce qui suit fonctionne avec l'alias `fake`.

## Partie 1 — la promesse tient

```bash
dotnet run --project exemples/s1.3-transfert-naif -- generate fake
dotnet run --project exemples/s1.3-transfert-naif -- generate extractive          # service IA, hors-ligne
dotnet run --project exemples/s1.3-transfert-naif -- generate llama3-2-3b         # service IA, Ollama
```

Trois fournisseurs, un argument, et `Assistant.cs` n'a pas changé d'une ligne. Pour le
**générateur**, « le modèle est un détail » est vrai : il ne laisse aucune trace dans ce que
l'application stocke.

## Partie 2 — la promesse casse

```bash
dotnet run --project exemples/s1.3-transfert-naif -- index hashing
dotnet run --project exemples/s1.3-transfert-naif -- search hashing "jours de télétravail"        # 0,43  Télétravail
dotnet run --project exemples/s1.3-transfert-naif -- search hashing-512 "jours de télétravail"    # Erreur : dimensions 512 et 256
dotnet run --project exemples/s1.3-transfert-naif -- search hashing-stem4 "remboursement du repas" # 0,21  Télétravail — faux, et aucune erreur
```

Même port, même substitution, même domaine intact. Mais l'index a été construit dans
l'espace vectoriel d'un modèle, et le programme ne s'en souvient pas :

- avec un modèle d'une **autre dimension**, il plante (au moins on le sait) ;
- avec un modèle de **même dimension** (`hashing-stem4` : mêmes 256 dimensions, autres
  vecteurs), il répond à côté **sans rien signaler**. « Remboursement du repas » retrouve
  la fiche télétravail. En production, avec `bge-m3` et `mxbai` (1 024 dimensions tous les
  deux), c'est le même piège.

Le modèle d'embeddings n'est pas un détail : ses sorties sont **stockées**, et ce qui est
stocké dépend de lui. C'est le second verdict de la problématique.

## Ce que le fil rouge fait de différent

| Le jouet | Le fil rouge |
|---|---|
| Port `ITextGenerator` déclaré dans le domaine | Ports `IEmbedder`, `IGenerator` déclarés dans **la couche application** (`src/Assistant.Application/Ports.cs`) : ce sont les cas d'usage qui ont besoin d'un modèle, pas les entités. Le domaine ne dépend de rien, pas même d'une interface vers l'extérieur. |
| L'index stocke des vecteurs | L'index stocke aussi un **manifeste** (`IndexManifest`) : identifiant concret du modèle, dimension, empreinte du corpus, découpage, date. |
| Le fournisseur renvoie un tableau de nombres | Le service IA renvoie l'**identifiant concret** du modèle (`ollama:bge-m3@790764…`) et la dimension ; l'application les compare au manifeste à chaque question. |
| Une substitution silencieuse | `IndexModelMismatchException` : refus explicite, il faut réindexer (ADR 0003). Et `status` le dit avant même la première question. |
| Un seul programme | Deux déployables, deux langages, un contrat HTTP : la substitution est possible jusqu'au langage, mais le couplage par les données, lui, ne disparaît pas (ADR 0009). |

Retenir : le transfert naïf **n'est pas faux**, il est incomplet. Il traite le modèle comme un
service sans état ; or un modèle d'embeddings laisse un état dans l'application — l'index.
Le reste du cours est l'inventaire de ces états (séquences 3.2 et 4.2) et de ce qu'il faut
pour les tenir (séquence 4.1).

## Questions à se poser

1. Où, dans ce jouet, faudrait-il ajouter une ligne pour que la panne silencieuse devienne
   une erreur ? Combien de fichiers touche-t-on ? (Réponse : deux — l'index doit se souvenir,
   le fournisseur doit se nommer. C'est un contrat, pas une ligne.)
2. Le même raisonnement s'applique-t-il au générateur ? Qu'est-ce qui, dans ses sorties,
   est stocké ? (Rien. Mais les réponses changent quand même : voir `experience changement-generateur`.)
3. Pourquoi le fil rouge ne met-il pas ses ports dans le domaine, alors que « ça marche » ici ?
   (Parce que le domaine du fil rouge a des règles — droits, citations, forme — qui ne dépendent
   d'aucun modèle, et que la règle de dépendance vaut aussi pour les interfaces.)
