from __future__ import annotations

import http.client
import json
import urllib.error
import urllib.request
from typing import Any

from .base import BackendError


def request_json(method: str, url: str, payload: dict[str, Any] | None, timeout: float,
                 headers: dict[str, str] | None = None) -> dict[str, Any]:
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    request = urllib.request.Request(
        url, data=data, method=method,
        headers={"Content-Type": "application/json", **(headers or {})},
    )
    try:
        try:
            response = urllib.request.urlopen(request, timeout=timeout)
        except urllib.error.HTTPError as error:   # une erreur HTTP est aussi une réponse, son corps se lit pareil
            response = error
        with response:
            body = response.read()   # un délai dépassé ou une coupure peuvent aussi survenir ici
    except (urllib.error.URLError, TimeoutError, ConnectionError) as error:
        raise BackendError(f"{url} injoignable : {error}") from error
    except http.client.IncompleteRead as error:   # moteur coupé en pleine réponse : le prochain essai peut réussir
        raise BackendError(f"{url} a coupé sa réponse : {error!r}") from error
    except http.client.HTTPException as error:   # pas une réponse HTTP : base_url vise peut-être un autre service
        raise BackendError(f"{url} n'a pas répondu en HTTP : {error!r}", retryable=False) from error
    if isinstance(response, urllib.error.HTTPError):
        detail = body.decode("utf-8", errors="replace")[:500]
        # Un 4xx est un refus du moteur, par exemple pour un modèle absent : réessayer n'y changera rien. Sauf
        # 408 (délai dépassé) et 429 (trop de requêtes), qui demandent justement de réessayer plus tard.
        raise BackendError(f"{url} a répondu HTTP {response.code} : {detail}",
                           retryable=response.code >= 500 or response.code in (408, 429)) from response
    try:
        return json.loads(body.decode("utf-8"))
    except ValueError as error:   # JSON invalide ou octets non UTF-8
        raise BackendError(f"{url} a répondu autre chose que du JSON : {error}", retryable=False) from error
