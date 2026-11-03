import importlib.util
import math
import unittest
from pathlib import Path
from unittest import mock

from ai_service.backends import ollama
from ai_service.backends.base import BackendError
from ai_service.backends.extractive import ExtractiveGenerationBackend
from ai_service.backends.hashing import HashingEmbeddingBackend
from ai_service.backends.http_json import request_json
from ai_service.backends.ollama import OllamaEmbeddingBackend, OllamaGenerationBackend
from ai_service.backends.openai_compatible import (
    OpenAICompatibleEmbeddingBackend, OpenAICompatibleGenerationBackend,
)
from ai_service.backends.sentence_transformers_backend import SentenceTransformersEmbeddingBackend
from ai_service.registry import ConfigError, ModelRegistry, UnknownModelError
from tests_python.ai_service.stub_servers import StubServer, ollama_routes, openai_routes, silent_after

PROMPT = """Passages :

[1] Télétravail
Télétravail
Chaque salarié peut télétravailler deux jours par semaine.

[2] Congés
Congés
Chaque salarié acquiert vingt-cinq jours de congés payés.

Question : Combien de jours de télétravail ?"""

PROJECT_CONFIG = Path(__file__).parents[2] / "config/ai_service.toml"


class HashingBackendTest(unittest.TestCase):
    def test_deterministic_and_normalized(self):
        backend = HashingEmbeddingBackend(dimension=64)
        first, second = backend.embed(["télétravail"]), backend.embed(["télétravail"])
        self.assertEqual(first.vectors, second.vectors)
        self.assertAlmostEqual(math.sqrt(sum(x * x for x in first.vectors[0])), 1.0)

    def test_accents_do_not_matter(self):
        backend = HashingEmbeddingBackend()
        self.assertEqual(backend.embed(["Congés"]).vectors, backend.embed(["Conges"]).vectors)

    def test_case_does_not_matter(self):
        backend = HashingEmbeddingBackend()
        self.assertEqual(backend.embed(["CONGES"]).vectors, backend.embed(["conges"]).vectors)


class ExtractiveBackendTest(unittest.TestCase):
    def test_picks_the_closest_sentence_and_cites_it(self):
        _, text = ExtractiveGenerationBackend().generate("", PROMPT, 0.2, 100, None)
        self.assertEqual(text, "Chaque salarié peut télétravailler deux jours par semaine. [1]")

    def test_noisy_mode_sometimes_forgets_citations(self):
        backend = ExtractiveGenerationBackend(citation_failure_rate=0.5, randomize=True)
        outputs = {backend.generate("", PROMPT, 0.2, 100, seed)[1] for seed in range(40)}
        self.assertTrue(any("[" not in o for o in outputs))
        self.assertTrue(any("[" in o for o in outputs))

    def test_same_seed_same_output(self):
        backend = ExtractiveGenerationBackend(citation_failure_rate=0.5, randomize=True)
        self.assertEqual(backend.generate("", PROMPT, 0.2, 100, 3), backend.generate("", PROMPT, 0.2, 100, 3))

    def test_the_variant_alias_is_deterministic_and_sometimes_picks_another_cited_sentence(self):
        """extractive-variante répond toujours pareil au même prompt, mais pas toujours comme extractive, et sa
        réponse est toujours citée, avec ou sans graine dans la requête."""
        questions = ("Combien de jours de télétravail ?", "Combien de jours de congés ?", "Combien de congés par an ?",
                     "Combien de jours de télétravail par semaine ?", "Combien de jours de congés payés ?",
                     "Que dit la politique de télétravail ?", "Que peut faire chaque salarié ?",
                     "Quels sont les droits de chaque salarié ?")
        prompts = [PROMPT.replace("Combien de jours de télétravail ?", question) for question in questions]
        registry = ModelRegistry.from_file(PROJECT_CONFIG)
        variant, plain = registry.generation("extractive-variante"), registry.generation("extractive")
        answers = [variant.generate("", prompt, 0.2, 100, None) for prompt in prompts]
        again = ModelRegistry.from_file(PROJECT_CONFIG).generation("extractive-variante")   # nouvelle instance
        self.assertEqual(answers, [again.generate("", prompt, 0.2, 100, None) for prompt in prompts])
        changed = [text != plain.generate("", prompt, 0.2, 100, None)[1] for prompt, (_, text) in zip(prompts, answers)]
        self.assertIn(True, changed)
        self.assertIn(False, changed)
        for seed in (None, *range(20)):
            for prompt in prompts:
                with self.subTest(seed=seed, prompt=prompt[-40:]):
                    self.assertRegex(variant.generate("", prompt, 0.2, 100, seed)[1], r"\. \[[12]\]$")

    def test_each_extractive_alias_answers_under_its_own_identifier(self):
        """Chaque alias répond sous son propre identifiant : la trace distingue ainsi des alias qui partagent le
        même moteur."""
        registry = ModelRegistry.from_file(PROJECT_CONFIG)
        for alias in ("extractive", "extractive-bruite", "extractive-variante", "extractive-bavard"):
            with self.subTest(alias):
                self.assertEqual(registry.generation(alias).generate("", PROMPT, 0.2, 100, None)[0], alias)

    def test_the_talkative_alias_writes_english_reasoning_before_its_cited_answer(self):
        """extractive-bavard écrit d'abord un raisonnement en anglais, sans balise, puis la réponse d'extractive."""
        registry = ModelRegistry.from_file(PROJECT_CONFIG)
        _, text = registry.generation("extractive-bavard").generate("", PROMPT, 0.2, 100, None)
        reasoning, _, answer = text.partition("\n\n")
        self.assertTrue(reasoning.startswith("Okay, let me check the documents. "), reasoning)
        self.assertEqual(answer, "Chaque salarié peut télétravailler deux jours par semaine. [1]")


class OllamaBackendTest(unittest.TestCase):
    def test_embeddings_request_format_and_model_digest(self):
        with StubServer(ollama_routes()) as stub:
            vectors = OllamaEmbeddingBackend("nomic-embed-text", base_url=stub.url).embed(["a", "b"])
            _, path, body = next(r for r in stub.requests if r[1] == "/api/embed")
        self.assertEqual(body, {"model": "nomic-embed-text", "input": ["a", "b"]})
        self.assertEqual(vectors.model_id, "ollama:nomic-embed-text@0a109f422b47")
        self.assertEqual(vectors.dimension, 3)

    def test_chat_request_format(self):
        with StubServer(ollama_routes()) as stub:
            OllamaGenerationBackend("qwen3:1.7b", base_url=stub.url, think=False).generate(
                "système", "prompt", 0.2, 150, 42)
            _, _, body = next(r for r in stub.requests if r[1] == "/api/chat")
        self.assertEqual(body["messages"][0], {"role": "system", "content": "système"})
        self.assertEqual(body["options"], {"temperature": 0.2, "num_predict": 150, "seed": 42})
        self.assertIs(body["think"], False)
        self.assertIs(body["stream"], False)

    def test_thinking_models_get_a_separate_token_budget_and_thinking_is_discarded(self):
        with StubServer(ollama_routes(thinking=True)) as stub:
            _, text = OllamaGenerationBackend(
                "qwen3:4b", base_url=stub.url, think=True, thinking_tokens=1000,
            ).generate("système", "prompt", 0.2, 150, None)
            _, _, body = next(r for r in stub.requests if r[1] == "/api/chat")
        self.assertEqual(text, "Deux jours [1].")
        self.assertIs(body["think"], True)
        self.assertEqual(body["options"]["num_predict"], 1150)

    def test_thinking_budget_is_ignored_when_thinking_is_off(self):
        backend = OllamaGenerationBackend("x", think=False, thinking_tokens=1000)
        self.assertEqual(backend.thinking_tokens, 0)

    def test_the_digest_is_read_with_a_short_timeout(self):
        """/api/tags répond vite : 10 s y suffisent, et un Ollama figé se voit sans attendre le délai du
        modèle (120 s pour les embeddings, 300 s pour la génération), qui ne vaut que pour l'inférence."""
        calls = []

        def recorded(method, url, payload, timeout):
            calls.append((method, url.removeprefix(stub.url), timeout))
            return request_json(method, url, payload, timeout)

        with StubServer(ollama_routes()) as stub, mock.patch.object(ollama, "request_json", recorded):
            OllamaEmbeddingBackend("nomic-embed-text", base_url=stub.url).embed(["a"])
            OllamaGenerationBackend("qwen3:1.7b", base_url=stub.url).generate("s", "p", 0.2, 10, None)
        self.assertEqual(calls, [("GET", "/api/tags", 10), ("POST", "/api/embed", 120), ("GET", "/api/tags", 10),
                                 ("GET", "/api/tags", 10), ("POST", "/api/chat", 300), ("GET", "/api/tags", 10)])

    def test_unreachable_ollama_is_a_retryable_backend_error(self):
        # Le refus est simulé là où il naît, dans socket.create_connection, car sous Windows un vrai refus coûte
        # 2 s. Le seul vrai refus de ces tests est dans test_backend_failure_is_502 (test_server.py).
        refused = mock.patch("socket.create_connection", side_effect=ConnectionRefusedError("connexion refusée (simulée)"))
        with refused, self.assertRaises(BackendError) as caught:
            OllamaEmbeddingBackend("x", base_url="http://127.0.0.1:1", timeout=2).embed(["a"])
        self.assertTrue(caught.exception.retryable)   # Ollama peut revenir : réessayer a un sens

    def test_a_refusal_by_the_engine_is_not_worth_retrying(self):
        """Un 4xx est un refus du moteur, et réessayer n'y changera rien. Sauf 408 (délai dépassé) et 429
        (trop de requêtes), qui demandent justement de réessayer plus tard."""
        for status, retryable in ((400, False), (404, False), (408, True), (429, True), (500, True), (503, True)):
            routes = ollama_routes(tags=lambda: [{"name": "bge-m3:latest", "digest": "0123456789ab0000"}])
            routes[("POST", "/api/embed")] = lambda body, status=status: (status, {"error": "model not found"})
            with self.subTest(status), StubServer(routes) as stub:
                with self.assertRaises(BackendError) as caught:
                    OllamaEmbeddingBackend("bge-m3", base_url=stub.url).embed(["a"])
                self.assertEqual(caught.exception.retryable, retryable)

    def test_an_answer_not_shaped_like_ollama_is_a_definitive_error(self):
        """Derrière un proxy ou une mauvaise base_url, la réponse n'a pas la forme d'Ollama : on veut une
        BackendError qui le dit, pas un AttributeError qui finirait en 500."""
        installed = [{"name": "bge-m3:latest", "digest": "0123456789ab0000"}]
        cases = {
            "/api/tags en liste": {("GET", "/api/tags"): lambda body: (200, [])},
            "/api/tags sans liste": {("GET", "/api/tags"): lambda body: (200, {"models": None})},
            "modèle qui n'est pas un objet": {("GET", "/api/tags"): lambda body: (200, {"models": ["bge-m3"]})},
            "/api/embed en liste": {("POST", "/api/embed"): lambda body: (200, [[0.1], [0.2]])},
            "vecteurs à plat": {("POST", "/api/embed"): lambda body: (200, {"embeddings": [0.1, 0.2]})},
            "vecteurs en moins": {("POST", "/api/embed"): lambda body: (200, {"embeddings": [[0.1]]})},
        }
        for case, overrides in cases.items():
            with self.subTest(case), StubServer({**ollama_routes(tags=lambda: installed), **overrides}) as stub:
                with self.assertRaises(BackendError) as caught:
                    OllamaEmbeddingBackend("bge-m3", base_url=stub.url).embed(["a", "b"])
                self.assertFalse(caught.exception.retryable)   # le moteur répondra toujours pareil
        chat = {**ollama_routes(tags=lambda: installed),
                ("POST", "/api/chat"): lambda body: (200, {"message": {"content": 42}})}
        with StubServer(chat) as stub, self.assertRaises(BackendError) as caught:
            OllamaGenerationBackend("bge-m3", base_url=stub.url).generate("s", "p", 0.2, 10, None)
        self.assertFalse(caught.exception.retryable)

    def test_a_weights_update_is_seen_without_restarting_the_service(self):
        """Un `ollama pull` derrière le même alias change l'identifiant. Le service le voit sans redémarrer, car il
        relit l'empreinte à chaque appel."""
        installed = {"digest": "aaaaaaaaaaaa0000"}
        routes = ollama_routes(tags=lambda: [{"name": "bge-m3:latest", **installed},
                                             {"name": "qwen3:4b", **installed}])
        with StubServer(routes) as stub:
            embedder = OllamaEmbeddingBackend("bge-m3", base_url=stub.url)
            generator = OllamaGenerationBackend("qwen3:4b", base_url=stub.url)
            before = embedder.embed(["a"]).model_id, generator.generate("s", "p", 0.2, 10, None)[0]
            installed["digest"] = "bbbbbbbbbbbb0000"   # ollama pull : nouvelle version
            after = embedder.embed(["a"]).model_id, generator.generate("s", "p", 0.2, 10, None)[0]
        self.assertEqual(before, ("ollama:bge-m3@aaaaaaaaaaaa", "ollama:qwen3:4b@aaaaaaaaaaaa"))
        self.assertEqual(after, ("ollama:bge-m3@bbbbbbbbbbbb", "ollama:qwen3:4b@bbbbbbbbbbbb"))

    def test_a_pull_during_the_call_is_a_retryable_error(self):
        """On ne sait pas quelle version a servi : on échoue plutôt que d'étiqueter au hasard."""
        calls = {
            "/api/embed": lambda url: OllamaEmbeddingBackend("bge-m3", base_url=url).embed(["a"]),
            "/api/chat": lambda url: OllamaGenerationBackend("bge-m3", base_url=url).generate("s", "p", 0.2, 10, None),
        }
        for path, call in calls.items():
            with self.subTest(path=path):
                installed = {"digest": "aaaaaaaaaaaa0000"}
                routes = ollama_routes(tags=lambda: [{"name": "bge-m3:latest", **installed}])
                inference = routes[("POST", path)]

                def pull_then_infer(body, inference=inference, installed=installed):
                    installed["digest"] = "bbbbbbbbbbbb0000"   # ollama pull pendant l'inférence
                    return inference(body)

                routes[("POST", path)] = pull_then_infer
                with StubServer(routes) as stub, self.assertRaises(BackendError) as caught:
                    call(stub.url)
                self.assertIn("a changé pendant l'appel", str(caught.exception))
                self.assertTrue(caught.exception.retryable)   # le contrat le promet : il suffit de réessayer

    def _no_digest_cases(self):
        """Les cas où l'empreinte de bge-m3 manque ou ne se lit pas, chacun avec les routes de son faux Ollama."""
        failing_tags = ollama_routes()
        failing_tags[("GET", "/api/tags")] = lambda body: (500, {"error": "chargement"})
        return {
            "modèle absent": ollama_routes(tags=lambda: []),
            "entrée sans empreinte": ollama_routes(tags=lambda: [{"name": "bge-m3:latest"}]),
            "empreinte nulle": ollama_routes(tags=lambda: [{"name": "bge-m3:latest", "digest": None}]),
            "/api/tags en erreur": failing_tags,
        }

    def test_without_a_digest_the_call_fails_before_inference(self):
        """Sans empreinte, le résultat ne se rattache à aucun modèle : on échoue avant même l'inférence."""
        for case, routes in self._no_digest_cases().items():
            with self.subTest(case), StubServer(routes) as stub:
                with self.assertRaises(BackendError):
                    OllamaEmbeddingBackend("bge-m3", base_url=stub.url).embed(["a"])
                self.assertNotIn("/api/embed", [path for _, path, _ in stub.requests])

    def test_a_missing_digest_is_definitive_but_a_failing_tags_list_is_retryable(self):
        for case, routes in self._no_digest_cases().items():
            with self.subTest(case), StubServer(routes) as stub:
                with self.assertRaises(BackendError) as caught:
                    OllamaEmbeddingBackend("bge-m3", base_url=stub.url).embed(["a"])
                # Un modèle absent donne une erreur définitive. Si Ollama est en erreur passagère, réessayer a un sens.
                self.assertEqual(caught.exception.retryable, case == "/api/tags en erreur")

    def test_the_model_name_is_compared_the_way_ollama_does(self):
        """Le nom est comparé comme le fait Ollama : sans tenir compte de la casse, avec le tag « latest » implicite,
        et sans exiger le registre ni l'espace « library »."""
        tags = [{"name": "bge-m3:latest", "digest": "0123456789ab0000"}]
        with StubServer(ollama_routes(tags=lambda: tags)) as stub:
            for name in ("bge-m3", "BGE-M3", "bge-m3:LATEST", "registry.ollama.ai/library/bge-m3"):
                with self.subTest(name):
                    model_id = OllamaEmbeddingBackend(name, base_url=stub.url).embed(["a"]).model_id
                    self.assertEqual(model_id, f"ollama:{name}@0123456789ab")
        only_model = [{"model": "bge-m3:latest", "digest": "0123456789ab0000"}]   # sans « name » : « model » suffit
        with StubServer(ollama_routes(tags=lambda: only_model)) as stub:
            self.assertEqual(OllamaEmbeddingBackend("bge-m3", base_url=stub.url).embed(["a"]).model_id,
                             "ollama:bge-m3@0123456789ab")


class SentenceTransformersBackendTest(unittest.TestCase):
    @unittest.skipIf(importlib.util.find_spec("sentence_transformers"), "sentence-transformers est installé")
    def test_a_missing_library_is_not_worth_retrying(self):
        with self.assertRaises(BackendError) as caught:
            SentenceTransformersEmbeddingBackend("modele").embed(["a"])
        self.assertFalse(caught.exception.retryable)
        self.assertIn("pip install", str(caught.exception))


class OpenAICompatibleBackendTest(unittest.TestCase):
    def test_embeddings_are_reordered_by_index(self):
        with StubServer(openai_routes()) as stub:
            vectors = OpenAICompatibleEmbeddingBackend("m", base_url=stub.url + "/v1").embed(["a", "b"])
        self.assertEqual(vectors.vectors, [[0.0, 1.0], [1.0, 1.0]])

    def test_chat(self):
        with StubServer(openai_routes()) as stub:
            _, text = OpenAICompatibleGenerationBackend("m", base_url=stub.url + "/v1").generate(
                "s", "p", 0.1, 50, None)
        self.assertEqual(text, "Réponse [1]")

    def test_an_unset_seed_is_not_sent(self):
        with StubServer(openai_routes()) as stub:
            OpenAICompatibleGenerationBackend("m", base_url=stub.url + "/v1").generate("s", "p", 0.1, 50, None)
            body = stub.requests[0][2]
        self.assertNotIn("seed", body)

    def test_a_cut_answer_is_a_retryable_backend_error(self):
        """Un moteur coupé en pleine réponse peut réussir au prochain essai. C'est 502 et non 500."""
        cut = b"HTTP/1.0 200 OK\r\nContent-Length: 500\r\n\r\n" + b'{"data": ['
        with StubServer({("POST", "/v1/embeddings"): lambda body: cut}) as stub, \
                self.assertRaises(BackendError) as caught:
            OpenAICompatibleEmbeddingBackend("m", base_url=stub.url + "/v1").embed(["a"])
        self.assertTrue(caught.exception.retryable)

    def test_a_non_http_answer_is_a_definitive_backend_error(self):
        """Un service qui ne parle pas HTTP, parce que base_url vise autre chose, répondra toujours pareil.
        C'est 502 et non 500."""
        with StubServer({("POST", "/v1/embeddings"): lambda body: b"garbage-not-http\r\n"}) as stub, \
                self.assertRaises(BackendError) as caught:
            OpenAICompatibleEmbeddingBackend("m", base_url=stub.url + "/v1").embed(["a"])
        self.assertFalse(caught.exception.retryable)

    def test_an_error_that_stops_or_is_cut_in_its_body_is_retryable(self):
        """Le moteur envoie ses en-têtes d'erreur, puis se tait jusqu'au délai ou coupe la connexion. Comme au
        milieu d'une réponse 200, on obtient une BackendError réessayable (502 backend_error), et non une
        exception de la bibliothèque standard, qui donnerait un 500 avec sa pile."""
        error = b"HTTP/1.1 500 Erreur\r\nContent-Length: 100\r\n\r\n{"
        cases = {
            "silence": (error, False, "injoignable : timed out"),
            "coupure": (error, True, "a coupé sa réponse : IncompleteRead("),
            "coupure en morceaux": (b"HTTP/1.1 500 Erreur\r\nTransfer-Encoding: chunked\r\n\r\n10\r\n{", True,
                                    "a coupé sa réponse : IncompleteRead("),
        }
        for case, (start, cut, message) in cases.items():
            with self.subTest(case), silent_after(start, cut) as url:
                with self.assertRaises(BackendError) as caught:
                    OpenAICompatibleEmbeddingBackend("m", base_url=url + "/v1", timeout=0.5).embed(["a"])
                self.assertIn(f"{url}/v1/embeddings {message}", str(caught.exception))
                self.assertTrue(caught.exception.retryable)


class RegistryTest(unittest.TestCase):
    def test_prefixes_are_applied_by_the_service_not_the_application(self):
        registry = ModelRegistry.from_dict({"embedding": {"e5": {
            "backend": "hashing", "dimension": 16, "query_prefix": "query: ", "document_prefix": "passage: "}}})
        model = registry.embedding("e5")
        direct = HashingEmbeddingBackend(dimension=16)
        self.assertEqual(model.embed(["bonjour"], "query").vectors, direct.embed(["query: bonjour"]).vectors)
        self.assertEqual(model.embed(["bonjour"], "document").vectors, direct.embed(["passage: bonjour"]).vectors)

    def test_prefixes_are_part_of_the_model_identity(self):
        """Changer un préfixe change les vecteurs : l'identifiant doit changer aussi."""
        def model_id(**prefixes):
            registry = ModelRegistry.from_dict({"embedding": {"e5": {"backend": "hashing", "dimension": 16, **prefixes}}})
            return registry.embedding("e5").embed(["bonjour"], "query").model_id

        plain = model_id()
        variants = [
            model_id(query_prefix="query: ", document_prefix="passage: "),
            model_id(query_prefix="search_query: ", document_prefix="passage: "),
            model_id(query_prefix="query: ", document_prefix="document: "),   # celui des documents compte aussi
            model_id(query_prefix="query: "),                                 # requête seule, comme mxbai
            model_id(document_prefix="passage: "),
        ]
        self.assertEqual(plain, "hashing-16-stem6")
        self.assertEqual(len({plain, *variants}), 6)
        self.assertTrue(all(v.startswith("hashing-16-stem6+prefixes-") for v in variants))

    def test_reasoning_tags_are_removed_whatever_the_engine(self):
        """Les balises <think> tiennent au modèle (qwen3…), pas au moteur : le registre les retire avec Ollama comme
        avec un serveur compatible OpenAI."""
        leaked = "<think>je réfléchis</think>\nRéponse [1]"
        ollama = ollama_routes()
        ollama[("POST", "/api/chat")] = lambda body: (200, {"message": {"role": "assistant", "content": leaked}})
        openai = openai_routes()
        openai[("POST", "/v1/chat/completions")] = lambda body: (200, {"choices": [{"message": {"content": leaked}}]})
        for backend, routes, suffix in (("ollama", ollama, ""), ("openai-compatible", openai, "/v1")):
            with self.subTest(backend), StubServer(routes) as stub:
                registry = ModelRegistry.from_dict({"generation": {"m": {
                    "backend": backend, "model": "qwen3:1.7b", "base_url": stub.url + suffix}}})
                _, text = registry.generation("m").generate("s", "p", 0.2, 50, None)
                self.assertEqual(text, "Réponse [1]")

    def test_unknown_model(self):
        with self.assertRaises(UnknownModelError):
            ModelRegistry.from_dict({}).generation("gpt-9")

    def test_unknown_backend(self):
        with self.assertRaises(ConfigError):
            ModelRegistry.from_dict({"generation": {"x": {"backend": "magie"}}})

    def test_project_configuration_is_valid(self):
        registry = ModelRegistry.from_file(PROJECT_CONFIG)
        self.assertIn("hashing", [m["alias"] for m in registry.describe()["embedding"]])


if __name__ == "__main__":
    unittest.main()
