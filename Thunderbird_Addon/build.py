"""Package the Thunderbird MailExtension; no message data or local settings included."""
from pathlib import Path
from zipfile import ZipFile, ZipInfo, ZIP_DEFLATED
import json
root=Path(__file__).resolve().parent
version=json.loads((root/'extension/manifest.json').read_text())['version']
output=root/'packages'/f'listener-external-mail-{version}.xpi'
output.parent.mkdir(exist_ok=True)
with ZipFile(output,'w',ZIP_DEFLATED) as archive:
    for path in sorted((root/'extension').rglob('*')):
        if path.is_file():
            info=ZipInfo(str(path.relative_to(root/'extension')),date_time=(2026,1,1,0,0,0))
            info.compress_type=ZIP_DEFLATED
            info.external_attr=0o644<<16
            archive.writestr(info,path.read_bytes())
print(output)
