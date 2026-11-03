# Contrat HTTP du service IA — version 1

Le service IA ne connaît rien au métier. Il reçoit des textes et renvoie des vecteurs ou du texte.
À chaque réponse, il dit aussi **quel modèle a réellement servi**.

Ce contrat est la seule chose que partagent l'application C# et le service IA écrit en Python.
C'est lui, et non le langage, qui fait la frontière entre les deux. Il en est à sa
version 1, figée le 11/09/2026.

Le service expose quatre routes. Deux servent à vérifier qu'il répond et à savoir ce qu'il sert,
les deux autres font le travail : l'une produit des vecteurs, l'autre du texte. L'index, lui, reste
du côté de l'application.

Une exploration antérieure de l'équipe faisait l'inverse : l'index vivait dans le service, avec les
routes `/index/reset`, `/index/upsert` et `/index/search`, cette dernière filtrant par droits
d'accès. Ce contrat ne reprend pas ces routes, et c'est voulu. La règle d'accès reste dans le
domaine, et l'index reste une donnée de l'application.

## `GET /health` (ou `HEAD`)

Cette route permet de vérifier que le service répond, et de savoir quelle version du contrat il
parle.

```json
{"status": "ok", "contract_version": "1"}
```

Comme `/v1/models`, elle accepte aussi `HEAD`. La réponse porte alors les mêmes en-têtes qu'avec
`GET`, sans le corps.

## `GET /v1/models` (ou `HEAD`)

Elle donne la liste des alias servis, rangés par type, avec pour chacun le moteur qui le fait
tourner (`backend`) et une courte description.

```json
{
  "embedding":  [{"alias": "nomic", "backend": "ollama", "description": "..."}],
  "generation": [{"alias": "qwen3-4b", "backend": "ollama", "description": "..."}]
}
```

## `POST /v1/embeddings`

Requête :

```json
{"model": "nomic", "input_type": "query", "inputs": ["Combien de jours de télétravail ?"]}
```

- `model` : un alias déclaré dans `config/ai_service.toml`.
- `input_type` : `"query"` pour une question, `"document"` pour un passage à indexer. La valeur par
  défaut est `"document"`. L'application déclare ainsi une intention, et c'est le service qui
  applique les préfixes propres au modèle (`search_query: `, `query: `…).
- `inputs` : de 1 à 256 textes.

Réponse :

```json
{
  "model": "ollama:nomic-embed-text@0a109f422b47+prefixes-03aa22a9",
  "alias": "nomic",
  "dimension": 768,
  "vectors": [[0.012, -0.034, ...]],
  "duration_ms": 41
}
```

Le champ `model` donne l'identifiant concret du modèle, pas seulement son alias. Quand le moteur
fournit une empreinte du modèle, elle en fait partie. C'est le cas avec Ollama : le service relit
alors cette empreinte avant et après chaque inférence. Quand l'alias déclare des préfixes, leur
propre empreinte s'ajoute à la fin (`+prefixes-…`), parce qu'ils changent les vecteurs.

Concrètement, si l'équipe qui exploite le service met à jour le modèle derrière le même alias,
`model` change.

## `POST /v1/generate`

Requête :

```json
{
  "model": "qwen3-4b",
  "system": "Tu es l'assistant documentaire…",
  "prompt": "Passages : … Question : …",
  "temperature": 0.2,
  "max_tokens": 400,
  "seed": null
}
```

`seed` est facultatif : absent ou `null`, il ne fixe aucune graine.

Réponse :

```json
{"model": "ollama:qwen3:4b@a383baf4993b", "alias": "qwen3-4b", "text": "Deux jours par semaine [1].", "duration_ms": 5230}
```

Le prompt est construit côté application, parce qu'il dépend du métier : numérotation des passages,
obligation de citer. Le service se contente de le transmettre au modèle.

`max_tokens` fixe le budget de la réponse. Un modèle à réflexion, déclaré avec `think = true` dans
`config/ai_service.toml`, reçoit en plus un budget de réflexion (`thinking_tokens`) que
l'application ne voit pas. La réponse peut donc dépasser `max_tokens` si le modèle réfléchit peu.

## Erreurs

Une erreur a toujours la même forme : un objet `error` avec un `code`, fait pour être testé par un
programme, et un `message`, fait pour être lu.

```json
{"error": {"code": "unknown_model", "message": "modèle de génération inconnu : gpt-9 (…)"}}
```

Pour une erreur 502, l'objet `error` porte aussi un champ `retryable`, qui dit si réessayer a une
chance de réussir. Il vaut `true` quand le moteur est injoignable, surchargé, ou s'est coupé en
pleine réponse. Il vaut `false` quand le modèle manque, ou quand le moteur renverra toujours la même
réponse, par exemple une réponse qui n'est pas du JSON. Un client ne réessaie donc que les erreurs
5xx qui ne portent pas `"retryable": false`.

```json
{"error": {"code": "backend_error", "message": "empreinte de bge-m3 introuvable dans … (ollama pull bge-m3) ?", "retryable": false}}
```

Cet exemple vient d'Ollama. Sans empreinte, le service ne peut pas dire quel modèle a servi : plutôt
que de renvoyer un identifiant incertain, il répond par cette erreur, dont le message invite à
vérifier que le modèle a bien été téléchargé. À l'inverse, une empreinte qui change pendant l'appel
donne une erreur passagère (`"retryable": true`) : il suffit de réessayer.

| HTTP | `code` | Signification |
|---|---|---|
| 400 | `invalid_request` | requête mal formée : champ manquant, de mauvais type ou de valeur invalide, corps qui n'est pas un objet JSON strict, `Content-Length` invalide, envoi en morceaux, requête illisible |
| 404 | `unknown_model` | l'alias demandé n'est pas dans la configuration du service |
| 404 | `not_found` | route inconnue, quelle que soit la méthode |
| 405 | `method_not_allowed` | route connue, mais pas avec cette méthode : l'en-tête `Allow` donne celles qui sont permises (`GET, HEAD` ou `POST`), et le message les rappelle |
| 413 | `payload_too_large` | corps de plus de 16 Mio sur `/v1/embeddings` ou `/v1/generate`, refusé sans être lu |
| 414, 431, 505 | `invalid_request` | requête refusée avant d'être lue : ligne de requête trop longue (414), en-têtes trop longs ou trop nombreux (431), version HTTP non prise en charge (505) |
| 502 | `backend_error` | le moteur qui fait tourner le modèle (Ollama, serveur compatible OpenAI…) est injoignable ou a échoué, et `retryable` dit s'il vaut la peine de réessayer |
| 500 | `internal_error` | erreur imprévue dans le service |

Le service ne lit pas les envois en morceaux (`Transfer-Encoding`). Un client doit donc annoncer la
taille de son corps avec `Content-Length`. Le JSON est lu de façon
stricte : une clé en double ou une valeur comme `NaN`, que certains analyseurs tolèrent, est
refusée.

Le service répond en HTTP/1.0 : une connexion par requête, fermée après la réponse. Si le client
n'envoie plus rien pendant 30 secondes, alors que le service attend la requête ou son corps, la
connexion est fermée sans réponse.

Au démarrage, si le port est déjà pris, `python -m ai_service` s'arrête avec le message « Erreur :
impossible d'écouter (port déjà pris, adresse inconnue ou non autorisée) — … » et le code de
retour 1.

## Évolution du contrat

Une nouvelle route, ou toute modification incompatible comme un champ renommé ou un sens changé,
passe par une nouvelle version du contrat, sous le préfixe `/v2/`.

Du côté du service, le contrat est vérifié par `tests_python/ai_service/test_server.py`, qui
contrôle ce que le service accepte et renvoie.
