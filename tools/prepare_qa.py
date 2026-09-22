#!/usr/bin/env python3
"""Clone the engine into this project's QA folder, then isolate its managed writes."""
from pathlib import Path
import hashlib,json,os,shutil,subprocess,sys
ROOT=Path(__file__).resolve().parents[1]
from build import DEFAULT_GAME
GAME=DEFAULT_GAME
QA=ROOT/'qa/runtime'
QA.mkdir(parents=True,exist_ok=True)
fixture=Path(os.environ.get('STUDENTAGE_QA_FIXTURE',str(QA/'fixture.save')))
if not fixture.is_file():raise SystemExit('Provide an isolated QA fixture through STUDENTAGE_QA_FIXTURE; never use a live player save.')
if fixture.resolve()!=(QA/'fixture.save').resolve():shutil.copy2(fixture,QA/'fixture.save')
def clone(src,dst):
    if not dst.exists(): subprocess.run(['/bin/cp','-cR',str(src),str(dst)],check=True)
for name in ['StudentAge.exe','UnityPlayer.dll','MonoBleedingEdge','StudentAge_Data','DLC','winhttp.dll','doorstop_config.ini']:
    clone(GAME/name,QA/name)
clone(GAME/'BepInEx/core',QA/'BepInEx/core') if (QA/'BepInEx').exists() else None
(QA/'BepInEx').mkdir(exist_ok=True)
clone(GAME/'BepInEx/core',QA/'BepInEx/core')
(QA/'BepInEx/plugins').mkdir(exist_ok=True)
(QA/'data').mkdir(exist_ok=True)
# Never leave a previous isolation proof valid while native assemblies are staged.
(QA/'isolation-verified.txt').unlink(missing_ok=True)
# Always start from untouched code before rewriting (data assets are cloned just once).
for src in (GAME/'StudentAge_Data/Managed').glob('*.dll'):shutil.copy2(src,QA/'StudentAge_Data/Managed'/src.name)
shutil.copy2(ROOT/'dist/StudentAgeDialogueSave/BepInEx/plugins/StudentAgeDialogueSave/StudentAgeDialogueSave.dll',QA/'BepInEx/plugins/StudentAgeDialogueSave.dll')
dotnet=shutil.which('dotnet') or '/usr/local/share/dotnet/dotnet'
sdk=subprocess.check_output([dotnet,'--list-sdks'],text=True).strip().splitlines()[-1]
csc=Path(sdk.split('[')[1].rstrip(']'))/sdk.split()[0]/'Roslyn/bincore/csc.dll'
refs=sorted((GAME/'StudentAge_Data/Managed').glob('*.dll'))+[GAME/'BepInEx/core/BepInEx.dll',GAME/'BepInEx/core/0Harmony.dll']
def compile(source,out,extra=[]):
    sources=source if isinstance(source,list) else [source]
    result=subprocess.run([dotnet,str(csc),'-nologo','-target:library','-nostdlib+','-langversion:latest','-out:'+str(out)]+['-r:'+str(p) for p in refs+extra]+[str(p) for p in sources])
    if result.returncode:raise SystemExit(result.returncode)
compile(ROOT/'tests/QaIsolation.cs',QA/'StudentAge_Data/Managed/QaIsolation.dll')
if (ROOT/'tests/RuntimeQA.cs').exists():compile(sorted((ROOT/'tests').glob('Runtime*QA.cs')),QA/'BepInEx/plugins/DialogueRuntimeQA.dll',[ROOT/'dist/StudentAgeDialogueSave/BepInEx/plugins/StudentAgeDialogueSave/StudentAgeDialogueSave.dll'])
subprocess.run([dotnet,'run','--project',str(ROOT/'tools/QaRewrite/QaRewrite.csproj'),'-p:GameDir='+str(GAME),'--',str(QA)],check=True)
qa_python=os.environ.get('STUDENTAGE_QA_PYTHON') or (str(ROOT/'qa/tool-env/bin/python') if (ROOT/'qa/tool-env/bin/python').exists() else sys.executable)
subprocess.run([qa_python,str(ROOT/'tools/rename_qa_player.py')],check=True)
manifest={str(p.relative_to(GAME)):hashlib.sha256(p.read_bytes()).hexdigest() for p in (GAME/'StudentAge_Data/Managed').glob('Assembly-CSharp*.dll')}
(ROOT/'qa/original-game-hashes.json').write_text(json.dumps(manifest,indent=2))
print('QA_COPY_READY',QA)
