"""Run only the isolated player, focusing its own window once. No installation or live-save writes."""
from pathlib import Path
import os,subprocess,time,datetime,sys
ROOT=Path(__file__).resolve().parents[1]
QA=ROOT/'qa/runtime'
assert (QA/'isolation-verified.txt').exists()
assert (QA/'StudentAge_Data/app.info').read_text()=='DlgSaveQA\nDialogSave'
processes=subprocess.check_output(['ps','-axo','pid,command'],text=True)
if any('StudentAge.exe -screen' in x and 'winewrapper' not in x for x in processes.splitlines()):raise SystemExit('Another game process is running')
preview='--preview' in sys.argv
controls='--controls' in sys.argv
adv_only='--adv-only' in sys.argv
adv='--adv' in sys.argv or adv_only
only_flag=QA/'adv-only.txt'
if adv_only:only_flag.write_text('ADV from launch, without original-mode comparison')
else:only_flag.unlink(missing_ok=True)
adv_flag=QA/'adv-mode.txt'
if adv:adv_flag.write_text('ADV presentation, interaction and frame budget QA')
else:adv_flag.unlink(missing_ok=True)
# QA config only; production preferences and first-run choice are untouched.
import re
cfg=QA/'BepInEx/config/local.studentage.dialoguesave.cfg'
contents=cfg.read_text() if cfg.exists() else '[Saving]\nAutoSave = false\n'
mode='ADV' if adv_only else 'Ask' if adv else 'Original'
if re.search(r'^DialogueStyle\s*=.*$',contents,re.M):contents=re.sub(r'^DialogueStyle\s*=.*$', 'DialogueStyle = '+mode,contents,flags=re.M)
else:contents+='\n[Interface]\nDialogueStyle = '+mode+'\n'
if adv:
    # A failed coroutine can leave its QA-only remember-choice test persisted.
    contents=re.sub(r'^ConfirmStorySkip\s*=.*$', 'ConfirmStorySkip = true',contents,flags=re.M)
    contents=re.sub(r'^ConfirmHistoryJump\s*=.*$', 'ConfirmHistoryJump = true',contents,flags=re.M)
cfg.parent.mkdir(parents=True,exist_ok=True);cfg.write_text(contents)
recovery='--recovery' in sys.argv
recovery_flag=QA/'recovery-mode.txt'
if recovery:recovery_flag.write_text('Storage recovery and timing regression')
else:recovery_flag.unlink(missing_ok=True)
font='--font' in sys.argv
font_flag=QA/'font-mode.txt'
if font:font_flag.write_text('Dialogue toolbar font-only visual QA')
else:font_flag.unlink(missing_ok=True)
controls_flag=QA/'controls-mode.txt'
if controls:controls_flag.write_text('Dialogue controls focused QA')
else:controls_flag.unlink(missing_ok=True)
preview_flag=QA/'preview-mode.txt'
if preview:preview_flag.write_text('Manual UI preview; leave game open.')
else:preview_flag.unlink(missing_ok=True)
if (QA/'results').exists():(QA/'results').rename(QA/('results-'+datetime.datetime.now().strftime('%H%M%S')))
exe='Z:'+str(QA/'StudentAge.exe').replace('/','\\')
log='Z:'+str(QA/'player.log').replace('/','\\')
env=dict(os.environ,SteamAppId='1991040',SteamGameId='1991040')
wine=os.environ.get('STUDENTAGE_CROSSOVER_WINE')
if not wine:
    candidates=[p/'Contents/SharedSupport/CrossOver/bin/wine' for directory in [Path.home()/'Applications',Path('/Applications')] for p in directory.glob('*CrossOver*.app')]
    wine=next((str(p) for p in candidates if p.is_file()),None)
if not wine:raise SystemExit('Set STUDENTAGE_CROSSOVER_WINE to the CrossOver wine executable.')
bottle=os.environ.get('STUDENTAGE_BOTTLE','Steam')
with (QA/'wine.log').open('w') as output:
    child=subprocess.Popen([wine,'--bottle',bottle,'--dll','winhttp=n,b','--workdir',str(QA),exe,'-screen-fullscreen','0','-screen-width','1920','-screen-height','1080','-logFile',log],env=env,stdout=output,stderr=subprocess.STDOUT)
    for _ in range(40):
        time.sleep(.25)
        processes=subprocess.check_output(['ps','-axo','pid,command'],text=True)
        matches=[x for x in processes.splitlines() if exe+' -screen' in x and 'winewrapper' not in x]
        if matches:
            pid=matches[0].strip().split()[0]
            subprocess.run(['osascript','-e',f'tell application "System Events" to set frontmost of (first process whose unix id is {pid}) to true'],capture_output=True)
            print('QA_RUNNING',pid,flush=True);break
    try:child.wait(timeout=None if preview else 300)
    except subprocess.TimeoutExpired:
        print('QA_TIMEOUT: inspect the isolated process before continuing',flush=True);raise SystemExit(2)
print('QA_PLAYER_EXITED',child.returncode,flush=True)
if (QA/'results/failed.txt').exists():print((QA/'results/failed.txt').read_text());raise SystemExit(1)
if not (QA/'results/success.txt').exists():raise SystemExit('QA success marker absent')
print((QA/'results/success.txt').read_text())
