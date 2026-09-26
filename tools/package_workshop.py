#!/usr/bin/env python3
"""Assemble the game's native plugins/ Mod layout. Never upload to Steam."""
from pathlib import Path
import hashlib
import json
import re
import shutil
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def main():
    version = re.search(r'Version = "([0-9.]+)"', (ROOT / 'src/Plugin.cs').read_text())[1]
    dll = ROOT / 'dist/StudentAgeDialogueSave/BepInEx/plugins/StudentAgeDialogueSave/StudentAgeDialogueSave.dll'
    build = json.loads((ROOT / 'qa/build-manifest.json').read_text())
    assert hashlib.sha256(dll.read_bytes()).hexdigest() == build['dll_sha256'], 'Build DLL differs from manifest'
    sources = {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted((ROOT / 'src').rglob('*.cs'))}
    assert sources == build['sources'], 'Rebuild after source changes'
    out = ROOT / 'dist/workshop' / f'StudentAge-DialogueSave-{version}'
    if out.exists():
        shutil.rmtree(out)
    (out / 'plugins').mkdir(parents=True)
    (out / 'readme').mkdir()
    shutil.copy2(dll, out / 'plugins/StudentAgeDialogueSave.dll')
    shutil.copy2(ROOT / 'distribution/workshop/preview.jpg', out / 'preview.jpg')
    shutil.copy2(ROOT / 'docs/WORKSHOP.md', out / 'readme/使用说明.md')
    shutil.copy2(ROOT / 'assets/ui-previews/README.md', out / 'readme/预览图片说明.md')
    files = {str(p.relative_to(out)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(out.rglob('*')) if p.is_file()}
    manifest = {'name': 'UI大修 · 对话中存档（仅测试版支持）', 'id': 'local.studentage.dialoguesave',
                'version': version, 'author': '360478712scy-gif', 'game': 'StudentAge 1.94',
                'requires': [], 'plugins': ['plugins/StudentAgeDialogueSave.dll'], 'files': files}
    (out / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n')
    archive = out.parent / f'{out.name}-Steam.zip'
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as z:
        for p in sorted(out.rglob('*')):
            if p.is_file():
                z.write(p, p.relative_to(out).as_posix())
    with zipfile.ZipFile(archive) as z:
        assert z.testzip() is None
        assert set(z.namelist()) == set(files) | {'manifest.json'}
        assert [n for n in z.namelist() if n.endswith('.dll')] == ['plugins/StudentAgeDialogueSave.dll']
        for name, digest in files.items():
            assert hashlib.sha256(z.read(name)).hexdigest() == digest
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    archive.with_suffix('.zip.sha256').write_text(f'{digest}  {archive.name}\n')
    print(json.dumps({'folder': str(out), 'zip': str(archive), 'sha256': digest, 'files': len(files)+1}, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
