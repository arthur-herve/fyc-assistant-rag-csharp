# ADR 0010 — Le cache d'embeddings dérive de l'index ; l'identité d'un modèle Ollama est relue à chaque appel

Date : 21/09/2026

Statut : acceptée. Elle complète les ADR 0003 et 0008. C'est la même décision que l'ADR 0009 de la
version Python. Un erratum du 01/10/2026 figure en fin de document.

## Contexte

Une revue de code a trouvé deux trous dans la promesse de l'ADR 0003 : « un index construit avec un
autre modèle est une erreur ».

Le premier venait du cache d'embeddings (ADR 0008), actif dans les configurations livrées. Sa clé
était le texte seul, et il gardait aussi les vecteurs des documents. Dans un processus long comme
`serve`, une réindexation faite après un changement de modèle derrière l'alias reprenait donc les
anciens vecteurs. L'index restait sur l'ancien modèle, et l'application refusait toute question
nouvelle jusqu'à son redémarrage.

Le second tenait au service IA, qui ne lisait l'empreinte d'un modèle Ollama qu'une fois, au
premier appel. Un `ollama pull` n'était donc vu qu'au redémarrage du service, et un échec de
`/api/tags` donnait un identifiant sans empreinte.

## Décision

- Le cache d'embeddings ne sert que l'index courant. Il reçoit le manifeste de l'index et se vide
  dès que ce manifeste change, puisqu'un nouvel index est un nouvel objet manifeste. Il ne garde que
  des vecteurs du modèle et de la dimension de cet index, et il ne met jamais les documents en
  cache.
- Le backend Ollama du service IA relit l'empreinte du modèle dans `/api/tags` avant et après
  chaque inférence. Sans empreinte, le service répond par une erreur 502 définitive
  (`retryable: false`) : le modèle manque, et réessayer n'y changerait rien. Si l'empreinte change
  pendant l'appel, l'erreur 502 est passagère, et il suffit de réessayer. Le service ne renvoie
  jamais un identifiant incertain. Les noms de modèles sont comparés comme Ollama les compare.
- Les préfixes d'un alias (`query_prefix`, `document_prefix`) entrent dans l'identifiant renvoyé,
  sous la forme `…+prefixes-<empreinte>`. Changer un préfixe change les vecteurs, donc
  l'identifiant, et oblige à réindexer, quel que soit le moteur.

## Conséquences

- Ce qui est garanti : le cache ne mélange jamais deux index. Il se vide à chaque réindexation,
  même quand l'identifiant du modèle ne change pas (préfixes modifiés, moteur qui ne fournit pas
  d'empreinte). Après une réindexation, l'index et les questions viennent donc du même modèle. De
  plus, une question contrôle le modèle sur l'index même qu'elle interroge, y compris quand un
  autre processus reconstruit cet index pendant la recherche : `SearchPassages` recommence alors.
- Ce qui ne l'est pas : avec le cache, une question déjà posée ne repart pas au service. Elle
  continue d'être servie avec les vecteurs du modèle de l'index, ce qui reste cohérent. C'est une
  question nouvelle, ou `status`, qui révèle un changement du modèle servi, car `status` n'utilise
  pas le cache. Avec `serve`, il faut donc surveiller `/v1/status`.
- L'identifiant n'est jamais plus précis que ce que fournit le moteur. Pour Ollama, c'est
  l'empreinte des poids. Pour un serveur compatible OpenAI ou pour sentence-transformers, c'est le
  simple nom du modèle. Les préfixes en font partie dans tous les cas. Avec un moteur du second
  type, des poids changés sous le même nom passent inaperçus jusqu'à la réindexation, et d'ici là
  l'application compare sans le savoir des vecteurs issus de deux modèles. C'est la limite de la
  garantie. Le cache se vide à chaque réindexation pour ne pas l'aggraver.
- Chaque inférence Ollama coûte deux appels à `/api/tags`. En local, c'est négligeable : quelques
  millisecondes.

## Écarté

- Une clé de cache fondée sur le nom du modèle. C'était la première version du correctif, et elle
  ne suffit pas quand les vecteurs changent sans que le nom change.
- Une clé fondée sur le modèle réellement servi. Il faudrait alors interroger le service à chaque
  question, ce que le cache cherche justement à éviter.
- Supprimer le cache. C'est l'exemple de décorateur technique de la séquence 4.1, et la leçon
  « un cache est un artefact dérivé » mérite d'être montrée plutôt qu'évitée.

## Erratum (01/10/2026)

Deux faits énoncés plus haut sont faux.

Dans le Contexte, un redémarrage ne suffisait pas. La réindexation faite dans `serve` reprenait les
anciens vecteurs du cache et les écrivait sur le disque, si bien que l'index périmé était
enregistré. Après un redémarrage, toutes les questions, même celles déjà posées, restaient refusées
avec une erreur 409. Il fallait une réindexation par un processus au cache vide : la commande
`index`, ou `serve` redémarré. Une fois écrit, l'artefact dérivé survivait au processus qui l'avait
produit.

Dans les Conséquences, la garantie « une question contrôle le modèle sur l'index même qu'elle
interroge » ne tenait pas. `SearchPassages` relisait le manifeste après la recherche. Un index
remplacé puis rétabli pendant la recherche (A, puis B, puis A) passait donc inaperçu, et la réponse
citait des passages de B sous la trace de A. Désormais, l'index vérifie son identifiant au moment
même de la recherche (`Search(…, indexId)`). S'il a été remplacé, il lève `IndexReplacedException`,
et `SearchPassages` recommence une fois.
