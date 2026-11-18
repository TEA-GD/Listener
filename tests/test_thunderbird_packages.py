import json
from pathlib import Path
import unittest
import zipfile
ROOT=Path(__file__).resolve().parents[1]
class Packages(unittest.TestCase):
    def test_both_packages_match_current_sources(self):
        ids=[]
        for folder,prefix in [('Thunderbird_Addon','listener-external-mail'),('Thunderbird_Addon_Metadata','listener-mail-metadata')]:
            extension=ROOT/folder/'extension'
            manifest=json.loads((extension/'manifest.json').read_text())
            ids.append(manifest['browser_specific_settings']['gecko']['id'])
            with zipfile.ZipFile(ROOT/folder/'packages'/f"{prefix}-{manifest['version']}.xpi") as archive:
                self.assertEqual(sorted(p.name for p in extension.iterdir()),sorted(archive.namelist()))
                for path in extension.iterdir():self.assertEqual(path.read_bytes(),archive.read(path.name))
        self.assertEqual(2,len(set(ids)))
if __name__=='__main__':unittest.main()
