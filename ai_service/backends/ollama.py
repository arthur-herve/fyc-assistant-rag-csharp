"""Backends Ollama (https://ollama.com) : modèles ouverts exécutés en local.

API utilisée : POST /api/embed, POST /api/chat, GET /api/tags.
"""

from __future__ import annotations

from typing import Callable, Sequence, TypeVar

from .base import BackendError, Vectors
from .http_json import request_json

T = TypeVar("T")


def _canonical(name: str) -> str:
    """Nom tel qu'Ollama le compare : sans tenir compte de la casse, sans le registre
    ni l'espace « library » par défaut, avec le tag « latest » s'il n'y en a pas."""
    name = name.strip().casefold().removeprefix("registry.ollama.ai/").removeprefix("library/")
    return name if ":" in name.rsplit("/", 1)[-1] else f"{name}:latest"


class _OllamaModel:
    def __init__(self, model: str, base_url: str, timeout: float) -> None:
        self.model = model
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout

    def model_id(self) -> str:
        """Identifiant qui porte l'empreinte du modèle installé, c'est-à-dire de ses poids, de son gabarit
        et de ses paramètres. Elle est relue à chaque appel : un `ollama pull` qui la modifie change
        l'identifiant renvoyé, sans qu'il faille redémarrer le service. Sans empreinte, pas d'identifiant."""
        tags = _call(self, "GET", "/api/tags", None, timeout=10)
        wanted = _canonical(self.model)
        try:
            installed = tags.get("models", [])
            for entry in installed:
                if wanted in (_canonical(entry.get("name") or ""), _canonical(entry.get("model") or "")):
                    digest = str(entry.get("digest") or "")[:12]
                    if digest:
                        return f"ollama:{self.model}@{digest}"
            names = ", ".join(str(entry.get("name")) for entry in installed) or "aucun"
        except (AttributeError, TypeError):   # pas la forme d'Ollama (proxy, mauvaise base_url) : définitif
            raise BackendError(f"Réponse /api/tags inattendue : {str(tags)[:300]}", retryable=False) from None
        raise BackendError(
            f"empreinte de {self.model} introuvable dans {self.base_url}/api/tags "
            f"(modèles installés : {names}) : le modèle est-il téléchargé (ollama pull {self.model}) ?",
            retryable=False,
        )

    def _identified(self, call: Callable[[], T]) -> tuple[str, T]:
        """Exécute `call` et renvoie (identifiant du modèle qui a servi, résultat).

        L'empreinte est lue avant et après l'appel. Si un `ollama pull` l'a changée
        entre-temps, on ne sait pas quelle version a servi, et on échoue plutôt que
        de deviner. L'erreur est passagère : il suffit de réessayer."""
        before = self.model_id()
        result = call()
        after = self.model_id()
        if after != before:
            raise BackendError(f"{self.model} a changé pendant l'appel ({before} → {after}) : réessayer")
        return before, result


def _call(model: _OllamaModel, method: str, path: str, payload: dict | None,
          timeout: float | None = None) -> dict:
    try:
        return request_json(method, f"{model.base_url}{path}", payload, timeout or model.timeout)
    except BackendError as error:
        raise BackendError(
            f"{error} — Ollama est-il lancé sur {model.base_url} ? "
            f"Le modèle est-il téléchargé (ollama pull {model.model}) ?",
            retryable=error.retryable,
        ) from error


class OllamaEmbeddingBackend(_OllamaModel):
    def __init__(self, model: str, base_url: str = "http://127.0.0.1:11434",
                 timeout: float = 120.0) -> None:
        super().__init__(model, base_url, timeout)

    def embed(self, texts: Sequence[str]) -> Vectors:
        model_id, data = self._identified(
            lambda: _call(self, "POST", "/api/embed", {"model": self.model, "input": list(texts)}))
        vectors = data.get("embeddings") if isinstance(data, dict) else None
        if not isinstance(vectors, list) or len(vectors) != len(texts) or not all(isinstance(v, list) for v in vectors):
            raise BackendError(f"Ollama n'a pas renvoyé {len(texts)} embeddings", retryable=False)
        return Vectors(model_id=model_id, dimension=len(vectors[0]), vectors=vectors)


class OllamaGenerationBackend(_OllamaModel):
    """Génération par /api/chat.

    Pour les modèles à « réflexion », comme qwen3, `think = true` demande à Ollama
    de renvoyer le raisonnement à part, dans `message.thinking`, et la réponse dans
    `message.content`. On garde la réponse, on ignore le raisonnement, et on lui
    accorde un budget de jetons séparé (`thinking_tokens`). Sans lui, la réflexion
    consommerait tout `max_tokens` et la réponse serait vide.
    """

    def __init__(self, model: str, base_url: str = "http://127.0.0.1:11434",
                 timeout: float = 300.0, think: bool | None = None,
                 thinking_tokens: int = 0, keep_alive: str | None = None) -> None:
        super().__init__(model, base_url, timeout)
        self.think = think
        self.thinking_tokens = thinking_tokens if think else 0
        self.keep_alive = keep_alive

    def generate(self, system, prompt, temperature, max_tokens, seed):
        # Le budget de réflexion s'ajoute à max_tokens. La réponse peut donc dépasser max_tokens
        # si le modèle réfléchit peu : c'est assumé, et documenté dans docs/contrat-http.md.
        options = {"temperature": temperature, "num_predict": max_tokens + self.thinking_tokens}
        if seed is not None:
            options["seed"] = seed
        payload = {
            "model": self.model,
            "messages": [
                {"role": "system", "content": system},
                {"role": "user", "content": prompt},
            ],
            "stream": False,
            "options": options,
        }
        if self.think is not None:
            payload["think"] = self.think  # modèles à « réflexion » (qwen3…)
        if self.keep_alive is not None:
            payload["keep_alive"] = self.keep_alive
        model_id, data = self._identified(lambda: _call(self, "POST", "/api/chat", payload))
        try:
            content = data["message"]["content"].strip()
        except (KeyError, TypeError, AttributeError):
            raise BackendError(f"Réponse Ollama inattendue : {str(data)[:300]}", retryable=False) from None
        # Le raisonnement qu'Ollama renvoie à part, dans `message.thinking`, est ignoré. Les balises
        # <think> restées dans la réponse sont retirées par le registre, quel que soit le moteur.
        return model_id, content
