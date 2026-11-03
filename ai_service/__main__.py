"""Point d'entrée du service IA : python -m ai_service [--config config/ai_service.toml]"""

from __future__ import annotations

import argparse
import json
import sys
import tomllib

from .registry import ConfigError, ModelRegistry
from .server import create_server


def _error(message: str) -> int:
    """Écrit « Erreur : … » sur la sortie d'erreur, sans pile, et renvoie le code 1."""
    print(f"Erreur : {message}", file=sys.stderr)
    return 1


def main() -> int:
    parser = argparse.ArgumentParser(description="Service IA : embeddings et génération en HTTP")
    parser.add_argument("--config", default="config/ai_service.toml")
    parser.add_argument("--host", help="adresse d'écoute (défaut : celle du fichier)")
    parser.add_argument("--port", type=int, help="port d'écoute (défaut : celui du fichier)")
    args = parser.parse_args()

    try:
        with open(args.config, "rb") as handle:
            config = tomllib.load(handle)
    except OSError as error:   # fichier absent, chemin vers un dossier, droits insuffisants
        return _error(f"fichier de configuration inaccessible — {error}")
    except ValueError as error:   # TOMLDecodeError, mais aussi UnicodeDecodeError si le fichier n'est pas en UTF-8
        return _error(f"{args.config} : TOML invalide — {error}")
    try:
        registry = ModelRegistry.from_dict(config)
    except ConfigError as error:   # moteur inconnu ou champ « backend » absent
        return _error(f"{args.config} : {error}")
    server_config = config.get("server", {})
    host = args.host or server_config.get("host", "127.0.0.1")
    port = args.port if args.port is not None else server_config.get("port", 8100)
    # Vérifié ici, sinon l'ouverture du port finirait sur une pile (OverflowError, TypeError). Le booléen est
    # écarté à part, car un « port = true » dans le TOML serait pris pour le port 1.
    if isinstance(port, bool) or not isinstance(port, int) or not 0 <= port <= 65535:
        # Le message dit d'où vient la valeur : de l'option, ou du fichier et de sa section [server].
        if args.port is not None:
            return _error(f"l'option --port attend un port entre 0 et 65535, pas « {port} »")
        return _error(f"{args.config} [server] : « port » = {json.dumps(port)}, doit être un entier compris entre 0 et "
                      "65535")

    try:
        server = create_server(registry, host, port)
    except OSError as error:   # port déjà pris, adresse inconnue ou interdite
        return _error(f"impossible d'écouter (port déjà pris, adresse inconnue ou non autorisée) — {error}")
    models = registry.describe()
    # On annonce une adresse où se connecter : l'hôte demandé, sauf 0.0.0.0, qui désigne toutes les interfaces et où
    # l'on ne se connecte pas. On annonce alors 127.0.0.1, en le précisant.
    everywhere = host == "0.0.0.0"
    address = f"http://{'127.0.0.1' if everywhere else host}:{server.server_address[1]}"
    listening = ", à l'écoute sur toutes les interfaces" if everywhere else ""
    # Le flush rend l'adresse lisible tout de suite, même quand la sortie est redirigée : avec --port 0, c'est le
    # seul moyen de connaître le port. Il suffit sur la dernière ligne, car les trois lignes partent ensemble.
    print(f"Service IA sur {address}{listening}")
    print("  embeddings :", ", ".join(m["alias"] for m in models["embedding"]))
    print("  génération :", ", ".join(m["alias"] for m in models["generation"]), flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nArrêt du service IA.")
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
