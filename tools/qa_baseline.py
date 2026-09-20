"""Read-only baseline of live game code, saves and native preference namespace."""
from pathlib import Path
import hashlib,json,os,re,sys
ROOT=Path(__file__).resolve().parents[1]
BOTTLE=Path(os.environ.get('STUDENTAGE_BOTTLE_DIR',str(Path.home()/'Library/Application Support/CrossOver/Bottles/Steam')))
GAME=BOTTLE/'drive_c/Program Files (x86)/Steam/steamapps/common/StudentAge'
SAVES=BOTTLE/'drive_c/users/crossover/AppData/LocalLow/PakyiGame/StudentAge'
def sha(data):return hashlib.sha256(data).hexdigest()
state={}
for folder in [GAME/'BepInEx/plugins',GAME/'StudentAge_Data/Managed',SAVES]:
    for p in sorted(folder.rglob('*')):
        if p.is_file() and p.suffix.lower() not in ('.log','.bak'):
            state[str(p.relative_to(BOTTLE))]=[p.stat().st_size,sha(p.read_bytes())]
reg=(BOTTLE/'user.reg').read_text(errors='replace')
blocks=re.split(r'(?m)(?=^\[)',reg)
state['player-registry']=[b for b in blocks if b.startswith('[Software\\\\PakyiGame\\\\StudentAge]')]
target=Path(sys.argv[2]) if len(sys.argv)>2 else ROOT/'qa/live-baseline.json'
if sys.argv[1]=='verify':
    old=json.loads(target.read_text());changed=[p for p in set(state)|set(old) if state.get(p)!=old.get(p)]
    target.with_name(target.stem+'-verification.json').write_text(json.dumps({'changed':changed,'checked':len(state)},ensure_ascii=False,indent=2))
    print('LIVE_BASELINE_UNCHANGED' if not changed else 'LIVE_BASELINE_DIFFERENT',len(state),changed)
    if changed:raise SystemExit(1)
else:
    assert state['player-registry'], 'native registry namespace not found'
    target.write_text(json.dumps(state,ensure_ascii=False,indent=2));print('LIVE_BASELINE_RECORDED',len(state))
