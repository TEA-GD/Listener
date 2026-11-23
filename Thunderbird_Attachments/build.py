from pathlib import Path
from zipfile import ZipFile,ZIP_DEFLATED
import json
root=Path(__file__).resolve().parent
version=json.loads((root/'extension/manifest.json').read_text())['version']
output=root/'packages'/f'listener-attachment-mail-{version}.xpi';output.parent.mkdir(exist_ok=True)
with ZipFile(output,'w',ZIP_DEFLATED) as archive:
 for file in sorted((root/'extension').glob('*')):
  if file.is_file():archive.write(file,file.name)
print(output)
