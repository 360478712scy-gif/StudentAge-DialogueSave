#!/usr/bin/env python3
"""Build only this plugin against the locally installed game's reference assemblies."""
from pathlib import Path
import argparse, hashlib, json, os, shutil, subprocess

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_GAME = Path(os.environ.get('STUDENTAGE_GAME_DIR', str(Path.home()/'Library/Application Support/CrossOver/Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/StudentAge')))

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--game', type=Path, default=DEFAULT_GAME)
    args = parser.parse_args()
    dotnet = shutil.which('dotnet') or '/usr/local/share/dotnet/dotnet'
    if not (args.game/'StudentAge_Data/Managed/Assembly-CSharp.dll').is_file():
        parser.error('Set --game or STUDENTAGE_GAME_DIR to a StudentAge installation with BepInEx 5.')
    (ROOT/'qa').mkdir(exist_ok=True)
    sdk = subprocess.check_output([dotnet, '--list-sdks'], text=True).strip().splitlines()[-1]
    csc = Path(sdk.split('[')[1].rstrip(']')) / sdk.split()[0] / 'Roslyn/bincore/csc.dll'
    refs = sorted((args.game/'StudentAge_Data/Managed').glob('*.dll'))
    refs += [args.game/'BepInEx/core'/name for name in ('BepInEx.dll','0Harmony.dll')]
    missing = [str(p) for p in refs if not p.exists()]
    if missing: raise SystemExit('Missing references: ' + ', '.join(missing))
    out = ROOT/'dist/StudentAgeDialogueSave/BepInEx/plugins/StudentAgeDialogueSave'
    out.mkdir(parents=True, exist_ok=True)
    sources = sorted((ROOT/'src').rglob('*.cs'))
    command = [dotnet, str(csc), '-nologo', '-target:library', '-nostdlib+', '-langversion:latest', '-optimize+', '-deterministic+', '-pathmap:'+str(ROOT)+'=/_/', '-out:'+str(out/'StudentAgeDialogueSave.dll')]
    previews = sorted((ROOT/'assets/ui-previews').glob('*.jpg'))
    if {p.stem for p in previews} != {'original','adv'}: raise SystemExit('Both UI preview images are required')
    command += ['-resource:'+str(p)+',DialogueSave.Preview.'+p.name for p in previews]
    skin = sorted(p for p in (ROOT/'assets/adv-skin').iterdir() if p.suffix in ('.png','.json','.wav'))
    command += ['-resource:'+str(p)+',DialogueSave.Skin.'+p.name for p in skin]
    command += ['-r:'+str(p) for p in refs] + [str(p) for p in sources]
    result = subprocess.run(command, capture_output=True, text=True)
    (ROOT/'qa/build.log').write_text(result.stdout + result.stderr)
    print(result.stdout + result.stderr)
    if result.returncode: raise SystemExit(result.returncode)
    manifest = {'dll_sha256':hashlib.sha256((out/'StudentAgeDialogueSave.dll').read_bytes()).hexdigest(), 'game_assembly_sha256':hashlib.sha256((args.game/'StudentAge_Data/Managed/Assembly-CSharp.dll').read_bytes()).hexdigest(), 'sources':{str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in sources}}
    manifest['preview_assets'] = {str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in previews}
    manifest['skin_assets'] = {str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in skin}
    (ROOT/'qa/build-manifest.json').write_text(json.dumps(manifest, indent=2))
    print('BUILD_OK', out/'StudentAgeDialogueSave.dll')

if __name__ == '__main__': main()
