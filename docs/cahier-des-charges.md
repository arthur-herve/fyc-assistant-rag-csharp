# Cahier des charges : l'assistant documentaire de Solvéo

Ce document décrit ce que doit faire le fil rouge du cours. Lisez-le avant d'ouvrir le code. La
séquence 2.1 vous demande d'en tirer un découpage en couches, à l'aide du gabarit
`docs/gabarit-decoupage.md`.

## Le contexte

Solvéo est une société de conseil imaginaire. Ses règles internes sont écrites dans neuf documents :
la politique de télétravail, les congés, les notes de frais, la charte informatique, le support,
l'accueil des nouveaux arrivants, et trois documents réservés aux ressources humaines ou à la
direction. Les salariés y trouvent mal ce qu'ils cherchent, et posent souvent les mêmes questions au
service RH et au support.

## Le besoin

Solvéo veut un assistant qui réponde aux questions de ses salariés à partir de ses documents
internes, en citant ses sources.

Un salarié pose sa question en français, avec ses mots, par exemple « Combien de jours de télétravail
par semaine ? ». L'assistant répond en quelques phrases et indique de quels documents vient la
réponse, pour que le salarié puisse vérifier. Il ne remplace pas les documents : il aide à trouver le
bon passage. Quand il ne trouve rien, il le dit.

## Les utilisateurs

Chaque salarié appartient à un ou plusieurs groupes, et le groupe `tous` réunit tout le monde. Un
document est soit public, soit réservé à certains groupes. Trois utilisateurs suffisent pour le cours :

| Utilisateur | Groupes | Ce qu'il peut lire |
|---|---|---|
| alice | `tous` | les six documents publics |
| bruno | `tous`, `rh` | les documents publics, le guide des entretiens annuels et la grille des rémunérations |
| claire | `tous`, `direction` | les documents publics, la grille des rémunérations et le plan stratégique |

Le salarié est désigné par son identifiant. L'authentification n'est pas demandée : ce n'est pas le
sujet du cours.

## Le corpus

Les documents sont des fichiers Markdown rangés dans `corpus/solveo/`. Chacun commence par un
en-tête qui donne son identifiant, son titre et les groupes autorisés à le lire. La valeur `tous`
désigne un document public. Le format est décrit dans `corpus/README.md`.

Le corpus est volontairement réduit : quelques dizaines de documents suffisent. Les neuf documents
de Solvéo servent aux tests et au travail hors ligne. Le dépôt contient aussi 50 fiches réelles de
Service-Public.gouv.fr, dans `corpus/service-public-reduit/`. Elles jouent le rôle d'un intranet
plus fourni quand on travaille avec de vrais modèles, avec les mêmes groupes et les mêmes
utilisateurs.

## Ce que fait l'assistant

Une réponse se construit en trois temps.

1. La recherche. Avant de répondre à quoi que ce soit, on découpe les documents en morceaux, et un
   modèle d'embeddings transforme chaque morceau en vecteur. L'ensemble de ces vecteurs forme
   l'index. À chaque question, l'assistant transforme la question en vecteur de la même façon, puis
   retient les morceaux les plus proches de la question.
2. La génération. Les morceaux retenus sont numérotés, puis donnés à un modèle de langage avec la
   question et des consignes. Le modèle rédige la réponse à partir de ces seuls passages.
3. La réponse citée. La réponse renvoie à ses passages par leur numéro, entre crochets : `[1]`, ou
   `[2, 3]`. L'assistant vérifie ces numéros, puis montre la réponse avec ses sources, c'est-à-dire
   le document et le passage qui se cachent derrière chaque numéro.

Aucun modèle n'est entraîné. L'assistant cherche dans un index et fait appel à des modèles qui
existent déjà.

## Les règles

Ces règles sont celles du métier. Elles doivent tenir quel que soit le modèle utilisé, et l'on doit
pouvoir les vérifier sans lui.

1. Les droits sont filtrés avant le prompt. Un document public se lit par tous, un document réservé
   par les membres de l'un de ses groupes, et un morceau a les droits de son document. Un passage
   que l'utilisateur n'a pas le droit de lire n'est jamais montré au modèle. Demander au modèle de
   « ne pas révéler » un document ne suffirait pas, car une consigne n'est pas une garantie.
2. La citation est obligatoire. Une réponse cite au moins un passage, et chaque numéro cité renvoie
   à un passage réellement fourni. Sinon, l'assistant ne montre pas le texte du modèle. Il dit qu'il
   n'a pas pu produire de réponse correctement sourcée, et donne les passages retrouvés, que le
   salarié lira lui-même.
3. Sans source pertinente, l'assistant refuse. La proximité entre la question et un passage se
   mesure par un score, comparé à un seuil de pertinence. Si aucun passage accessible n'atteint ce
   seuil, l'assistant répond « Je n'ai trouvé aucun document accessible qui réponde à cette
   question. » Il n'appelle pas le modèle, puisqu'il n'a rien à lui donner.
4. La réponse est courte et en français. Elle tient en trois phrases au plus, et ne s'appuie que sur
   les passages fournis.

Une question vide est refusée avant toute recherche.

Un exemple montre la portée de la première règle. Si alice demande la fourchette de salaire d'un
consultant senior, la grille des rémunérations ne doit jamais entrer dans le prompt. La règle
garantit qu'alice ne lira pas la grille. Elle ne garantit pas un refus : un document public voisin
peut très bien fournir une réponse, même sans rapport. bruno, lui, a accès à la grille.

## Ce que l'on attend aussi

Une question a donc trois issues possibles : une réponse citée, un refus faute de document
pertinent, ou un refus faute de réponse correctement sourcée.

Chaque réponse doit pouvoir s'expliquer, refus compris. On doit savoir quels passages ont été
retrouvés et avec quel score, quel seuil a été appliqué, et ce que le modèle a produit quand il a
été appelé.

Une application en ligne de commande suffit. Elle construit l'index, puis répond aux questions posées
au nom d'un utilisateur.

## Les contraintes techniques

Les modèles tournent dans un service à part, écrit en Python et fourni dans `ai_service/`. Ce service
transforme des textes en vecteurs et génère du texte à partir d'un prompt. On l'interroge en HTTP,
selon le contrat décrit dans `docs/contrat-http.md`, et on le lance avec `python -m ai_service`.

Le service a un mode hors ligne, avec des modèles de substitution qui ne demandent ni téléchargement
ni carte graphique. Tout le cours peut se faire ainsi. Avec Ollama, le même service sert aussi de
vrais modèles.

L'application est écrite en C# avec .NET 8, sans paquet tiers : la bibliothèque standard suffit. Seuls
les tests utilisent xUnit.

## Ce qui n'est pas demandé

- Entraîner ou affiner un modèle.
- Une interface graphique ou une authentification.
- Une base de données à installer. Pour quelques dizaines de documents, de simples fichiers
  suffisent.
