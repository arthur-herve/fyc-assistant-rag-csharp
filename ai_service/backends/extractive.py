"""Génération hors-ligne : recopie la phrase la plus proche de la question.

C'est un double de test assumé. Il connaît le format des passages envoyés par
l'application, « [n] Titre » puis le texte : ce couplage est volontaire, et il
faut le signaler aux apprenants.

Avec `citation_failure_rate` > 0 et `randomize`, il imite les défauts d'un vrai
modèle, des réponses qui varient et des citations oubliées, sans aucun GPU.

Avec `randomize` et `default_seed`, il devient un autre modèle, tout aussi
déterministe : le tirage dépend de la graine et du prompt, si bien qu'il choisit
pour certaines questions une autre phrase que la plus proche. Avec `preamble`,
la réponse est précédée de ce texte, comme le ferait un modèle qui écrit son
raisonnement avant de répondre.
"""

from __future__ import annotations

import random
import re
import unicodedata

_MARKER = re.compile(r"^\[(\d+)\]\s", re.MULTILINE)
_QUESTION = re.compile(r"Question\s*:\s*(.+)", re.IGNORECASE)


def _words(text: str) -> set[str]:
    ascii_text = unicodedata.normalize("NFKD", text.lower()).encode("ascii", "ignore").decode()
    return {w[:6] for w in re.findall(r"[a-z0-9]+", ascii_text) if len(w) > 3}


class ExtractiveGenerationBackend:
    def __init__(self, citation_failure_rate: float = 0.0, randomize: bool = False,
                 model_id: str = "extractive", default_seed: int | None = None, preamble: str = "") -> None:
        self._failure_rate = citation_failure_rate
        self._randomize = randomize
        self._model_id = model_id
        self._default_seed = default_seed
        self._preamble = preamble

    def generate(self, system, prompt, temperature, max_tokens, seed):
        if self._default_seed is not None:
            # La graine de la requête, ou à défaut celle de l'alias, est mêlée au prompt. Avec une graine seule, le
            # tirage serait le même pour toutes les questions : toutes changeraient de phrase, ou aucune.
            seed = f"{self._default_seed if seed is None else seed}:{prompt}"
        text = self._answer(prompt, random.Random(seed))  # seed=None : aléatoire à chaque appel
        return self._model_id, f"{self._preamble}\n\n{text}" if self._preamble else text

    def _answer(self, prompt: str, rng: random.Random) -> str:
        match = _QUESTION.search(prompt)
        question_words = _words(match.group(1)) if match else set()
        body = prompt[: match.start()] if match else prompt

        candidates: list[tuple[int, int, str]] = []
        markers = list(_MARKER.finditer(body))
        for i, marker in enumerate(markers):
            end = markers[i + 1].start() if i + 1 < len(markers) else len(body)
            number = int(marker.group(1))
            lines = body[marker.end():end].strip().splitlines()[1:]  # la première ligne est le titre
            for line in lines:
                line = line.strip()
                if not line or line.startswith("#"):
                    continue  # on ignore les titres Markdown
                for sentence in re.split(r"(?<=[.!?])\s+", line):
                    sentence = sentence.strip(" -*")
                    if len(sentence) > 20:
                        overlap = len(question_words & _words(sentence))
                        candidates.append((overlap, number, sentence))

        if not candidates:
            return "Les documents fournis ne permettent pas de répondre."

        candidates.sort(key=lambda c: c[0], reverse=True)
        pool = candidates[:2] if self._randomize and len(candidates) > 1 else candidates[:1]
        _, number, sentence = rng.choice(pool)
        if rng.random() < self._failure_rate:
            return sentence
        return f"{sentence} [{number}]"
