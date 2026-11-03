"""L'importateur Service-Public : une fiche XML devient un document du corpus."""

import contextlib
import io
import re
import sys
import tempfile
import unittest
import urllib.error
import zipfile
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).parents[2] / "tools"))

from import_service_public import DOSSIERS, main, parse_fiche, to_markdown  # noqa: E402

FICHE = """<?xml version="1.0" encoding="UTF-8"?>
<Publication xmlns:dc="http://purl.org/dc/elements/1.1/" ID="F9999" type="Fiche d'information"
             spUrl="https://www.service-public.gouv.fr/particuliers/vosdroits/F9999">
  <dc:title>Congé pour événement familial</dc:title>
  <dc:subject>Travail - Formation</dc:subject>
  <dc:date>modified 2026-03-02</dc:date>
  <dc:identifier>F9999</dc:identifier>
  <SousThemePere ID="N1">Congés</SousThemePere>
  <DossierPere ID="N2"><Titre>{dossier}</Titre></DossierPere>
  <Introduction><Texte><Paragraphe>Le salarié a droit à un congé.</Paragraphe></Texte></Introduction>
  <Texte>
    <Chapitre><Titre><Paragraphe>Durée</Paragraphe></Titre>
      <Paragraphe>Elle dépend de l'événement&#160;:</Paragraphe>
      <Liste type="puce"><Item><Paragraphe>mariage : 4 jours</Paragraphe></Item>
        <Item><Paragraphe>naissance : 3 jours</Paragraphe></Item></Liste>
      <SousChapitre><Titre><Paragraphe>Cas du 2<Exposant>e</Exposant> mariage</Paragraphe></Titre>
        <Paragraphe>Mêmes droits.</Paragraphe></SousChapitre>
    </Chapitre>
    <ANoter><Titre>À noter</Titre><Paragraphe>Le congé est rémunéré.</Paragraphe></ANoter>
    <Tableau><Rangée type="header"><Cellule><Paragraphe>Événement</Paragraphe></Cellule><Cellule><Paragraphe>Jours</Paragraphe></Cellule></Rangée>
      <Rangée><Cellule><Paragraphe>Décès</Paragraphe></Cellule><Cellule><Paragraphe>3</Paragraphe></Cellule></Rangée></Tableau>
    <OuSAdresser ID="R1"><Titre>Contact</Titre><Paragraphe>Ne doit pas apparaître.</Paragraphe></OuSAdresser>
  </Texte>
  <Reference type="Texte de référence" URL="https://www.legifrance.gouv.fr/x"><Titre>Code du travail</Titre></Reference>
</Publication>
"""


def _archive(directory, *fiches):
    """Construit une archive rangée comme celle de la DILA, avec les fiches F*.xml à la racine."""
    path = directory / "vosdroits.zip"
    with zipfile.ZipFile(path, "w") as zf:
        for number, xml in enumerate(fiches):
            zf.writestr(f"F{number}.xml", xml)
    return path


class ImportServicePublicTest(unittest.TestCase):
    def _fiche(self, dossier="Congés dans le secteur privé"):
        return parse_fiche(FICHE.format(dossier=dossier).encode("utf-8"))

    def test_fiche_is_converted_with_headings_lists_tables_and_callouts(self):
        body = self._fiche().body
        self.assertIn("Le salarié a droit à un congé.", body)
        self.assertIn("## Durée", body)
        self.assertIn("### Cas du 2e mariage", body)
        self.assertIn("- mariage : 4 jours\n- naissance : 3 jours", body)
        self.assertIn("À noter : Le congé est rémunéré.", body)
        self.assertIn("| Événement | Jours |\n|---|---|\n| Décès | 3 |", body)
        self.assertIn("Elle dépend de l'événement :", body)  # espace insécable normalisée

    def test_the_modification_date_is_read(self):
        self.assertEqual(self._fiche().modified, "2026-03-02")

    def test_contacts_and_legal_references_are_dropped(self):
        body = self._fiche().body
        self.assertNotIn("Ne doit pas apparaître", body)
        self.assertNotIn("Code du travail", body)

    def test_restricted_dossiers_get_simulated_groups(self):
        self.assertEqual(self._fiche("Licenciement économique").groups, ("rh",))
        self.assertEqual(self._fiche("Conflits du travail dans le secteur privé").groups, ("direction",))

    def test_out_of_scope_fiche_is_skipped(self):
        self.assertIsNone(self._fiche("Congés dans la fonction publique"))

    def test_markdown_has_the_header_the_corpus_adapter_reads(self):
        """L'en-tête est celui que décrit corpus/README.md : id, titre et groupes, entre deux lignes « --- »."""
        lines = to_markdown(self._fiche("Licenciement économique")).split("\n")
        self.assertEqual(lines[0], "---")
        end = lines.index("---", 1)
        self.assertTrue(all(":" in line for line in lines[1:end] if line.strip()))
        header = {key.strip(): value.strip() for key, _, value in (line.partition(":") for line in lines[1:end])}
        self.assertEqual(header["id"], "F9999")
        self.assertEqual(header["titre"], "Congé pour événement familial")
        self.assertEqual(header["groupes"], "rh")

    def test_the_document_starts_with_the_title_of_the_fiche(self):
        lines = to_markdown(self._fiche()).split("\n")
        end = lines.index("---", 1)
        self.assertEqual(lines[end + 1], "# Congé pour événement familial")

    def test_every_simulated_group_is_a_valid_group_name(self):
        # Tout groupe du tableau DOSSIERS doit être un nom de groupe bien formé, sinon le corpus est refusé.
        for group in {g for groups in DOSSIERS.values() for g in groups}:
            self.assertIsNotNone(re.fullmatch(r"[a-z0-9][a-z0-9_-]*", group), group)

    def test_an_extract_never_overwrites_the_reference_corpus(self):
        """--limit sans --out écraserait corpus/service-public : c'est refusé avant de lire l'archive."""
        with self.assertRaises(SystemExit), contextlib.redirect_stderr(io.StringIO()):
            main(["--limit", "5"])

    def test_the_limit_is_a_number_of_fiches(self):
        """--limit 0, qui importerait tout, et --limit -5, qui retirerait les 5 dernières fiches, sont refusés.

        Le refus a lieu avant de lire l'archive."""
        for limit in ("0", "-5"):
            with self.subTest(limit), self.assertRaises(SystemExit), contextlib.redirect_stderr(io.StringIO()):
                main(["--limit", limit, "--out", "essai"])

    def test_an_archive_is_imported_into_its_folder(self):
        with tempfile.TemporaryDirectory() as tmp:
            archive = _archive(Path(tmp), FICHE.format(dossier="Congés dans le secteur privé"),
                               FICHE.format(dossier="Congés dans la fonction publique"))
            out = Path(tmp) / "corpus"
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(main(["--archive", str(archive), "--out", str(out)]), 0)
            self.assertEqual([p.name for p in out.iterdir()], ["F9999.md"])
            self.assertEqual((out / "F9999.md").read_text(encoding="utf-8"), to_markdown(self._fiche()))

    def test_the_report_names_the_archive_it_read(self):
        with tempfile.TemporaryDirectory() as tmp:
            archive = _archive(Path(tmp), FICHE.format(dossier="Congés dans le secteur privé"))
            out = Path(tmp) / "corpus"
            with contextlib.redirect_stdout(io.StringIO()) as stdout:
                self.assertEqual(main(["--archive", str(archive), "--out", str(out)]), 0)
        self.assertIn(f"archive {archive}", stdout.getvalue())
        self.assertNotIn("téléchargée", stdout.getvalue())  # rien n'a été téléchargé

    def test_an_archive_without_any_fiche_leaves_the_corpus_untouched(self):
        """Le corpus en place n'est pas vidé quand l'archive n'apporte aucune fiche retenue.

        Cela arrive avec une mauvaise archive, ou si la DILA renomme le thème ou les dossiers."""
        with tempfile.TemporaryDirectory() as tmp:
            archive = _archive(Path(tmp), FICHE.format(dossier="Congés dans la fonction publique"))
            out = Path(tmp) / "corpus"
            out.mkdir()
            (out / "F1.md").write_text("fiche en place", encoding="utf-8")
            with self.assertRaises(SystemExit) as refused:
                main(["--archive", str(archive), "--out", str(out)])
            self.assertIn("n'est pas modifié", str(refused.exception))
            self.assertEqual((out / "F1.md").read_text(encoding="utf-8"), "fiche en place")

    def test_a_failed_download_or_an_unreadable_archive_is_a_message_and_imports_nothing(self):
        with tempfile.TemporaryDirectory() as tmp:
            # Une ancienne archive valide est déjà là : un téléchargement raté ne doit pas l'importer à sa place.
            archive = _archive(Path(tmp), FICHE.format(dossier="Congés dans le secteur privé"))
            out = str(Path(tmp) / "corpus")
            offline = urllib.error.URLError("hors ligne")
            with mock.patch("urllib.request.urlretrieve", side_effect=offline), \
                    contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()) as stderr:
                self.assertEqual(main(["--download", "--archive", str(archive), "--out", out]), 1)
            self.assertIn("Téléchargement impossible", stderr.getvalue())
            self.assertFalse(Path(out).exists())
            archive.write_bytes(b"pas une archive zip")  # par exemple un téléchargement interrompu
            with contextlib.redirect_stderr(io.StringIO()) as stderr:
                self.assertEqual(main(["--archive", str(archive), "--out", out]), 1)
            self.assertIn("Archive illisible", stderr.getvalue())
            self.assertFalse(Path(out).exists())


if __name__ == "__main__":
    unittest.main()
