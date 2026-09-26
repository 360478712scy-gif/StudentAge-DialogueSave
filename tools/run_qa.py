"""Run only the isolated player, focusing its own window once. No installation or live-save writes."""
from pathlib import Path
import os,subprocess,time,datetime,sys
ROOT=Path(__file__).resolve().parents[1]
QA=ROOT/'qa/runtime'
assert (QA/'isolation-verified.txt').exists()
assert (QA/'StudentAge_Data/app.info').read_text()=='DlgSaveQA\nDialogSave'
processes=subprocess.check_output(['ps','-axo','pid,command'],text=True)
if any('StudentAge.exe' in x and 'winewrapper' not in x and 'exec_command' not in x and 'rg ' not in x for x in processes.splitlines()):raise SystemExit('Another game process is running')
storage_perf=QA/'storage-perf.txt'
if '--storage-perf' in sys.argv:storage_perf.write_text('Real-size dialogue save/load timing on seeded copies')
else:storage_perf.unlink(missing_ok=True)
routing=QA/'archive-routing.txt'
if '--archive-routing' in sys.argv:routing.write_text('Save/load tab dispatch and overwrite regression')
else:routing.unlink(missing_ok=True)
resume_sisi=QA/'sisi-after-funeral.txt'
if '--sisi-after-funeral' in sys.argv:resume_sisi.write_text('Resume after verified funeral, respecting authored rewards')
else:resume_sisi.unlink(missing_ok=True)
sisi=QA/'sisi-up-mode.txt'
if '--sisi-up' in sys.argv:sisi.write_text('Real installed Sisi story with UP')
else:sisi.unlink(missing_ok=True)
perf_sweep=QA/'perf-sweep.txt'
final_batch=QA/'final-batch.txt'
if '--final-batch' in sys.argv:final_batch.write_text('Consolidated final checks')
else:final_batch.unlink(missing_ok=True)
final_unload=QA/'final-unload.txt'
if '--final-unload' in sys.argv:final_unload.write_text('Rollback exit unload regression')
else:final_unload.unlink(missing_ok=True)
final_extra=QA/'final-extra.txt'
if '--final-extra' in sys.argv:final_extra.write_text('Final nonhome note animation geometry checks')
else:final_extra.unlink(missing_ok=True)
final_resume=QA/'final-resume.txt'
if '--final-resume' in sys.argv:final_resume.write_text('Continue after passed settings archive modal checks')
else:final_resume.unlink(missing_ok=True)
modal_eight=QA/'modal-eight.txt'
if '--modal-eight' in sys.argv:modal_eight.write_text('Focused CG roundtrip and modal state checks')
else:modal_eight.unlink(missing_ok=True)
archive_controls=QA/'archive-controls.txt'
if '--archive-controls' in sys.argv:archive_controls.write_text('12 slots, wheel, speaker, choices, native and title routes')
else:archive_controls.unlink(missing_ok=True)
archive_six=QA/'archive-six.txt'
if '--archive-six' in sys.argv:archive_six.write_text('Six archive fixes and all edit modes')
else:archive_six.unlink(missing_ok=True)
archive_refine=QA/'archive-refine.txt'
archive_edits=QA/'archive-edit-regression.txt'
if '--archive-edit-regression' in sys.argv:archive_edits.write_text('Resume storage/rail checks after passed archive visuals')
else:archive_edits.unlink(missing_ok=True)
if '--archive-refine' in sys.argv:archive_refine.write_text('Generated themes, gender-specific archives and native previews')
else:archive_refine.unlink(missing_ok=True)
if '--perf-sweep' in sys.argv:perf_sweep.write_text('Long history frame cost and reuse benchmark')
else:perf_sweep.unlink(missing_ok=True)
visual_flag=QA/'presentation-visual.txt'
if '--presentation-visual' in sys.argv:visual_flag.write_text('Focused rendering only')
else:visual_flag.unlink(missing_ok=True)
lifecycle=QA/'lifecycle-mode.txt'
if '--lifecycle' in sys.argv:lifecycle.write_text('Unload with archive and confirmation active')
else:lifecycle.unlink(missing_ok=True)
confirm_resume=QA/'confirmation-resume.txt'
if '--confirmation-resume' in sys.argv:confirm_resume.write_text('Resume after passed settings layout and defaults')
else:confirm_resume.unlink(missing_ok=True)
batch_visual=QA/'batch-visual.txt'
if '--batch-visual' in sys.argv:batch_visual.write_text('Settings, backlog and CG in one isolated session')
else:batch_visual.unlink(missing_ok=True)
interaction_only=QA/'interaction-only.txt'
if '--interactions' in sys.argv:interaction_only.write_text('Resume after passed archive edits')
else:interaction_only.unlink(missing_ok=True)
native_only=QA/'native-archive-only.txt'
if '--native-archive' in sys.argv:native_only.write_text('Continue passed mod archive suite at native operations')
else:native_only.unlink(missing_ok=True)
archive_flag=QA/'archive-mode.txt'
if '--archive' in sys.argv:archive_flag.write_text('12 slot archive runtime operations')
else:archive_flag.unlink(missing_ok=True)
presentation='--presentation' in sys.argv
presentation_flag=QA/'presentation-mode.txt'
if presentation:presentation_flag.write_text('Event context, intertitle and click-to-complete performance')
else:presentation_flag.unlink(missing_ok=True)
choices='--choices' in sys.argv
cg='--cg' in sys.argv
cg_flag=QA/'cg-visual.txt'
if cg:cg_flag.write_text('CG presentation and normal-mode restoration')
else:cg_flag.unlink(missing_ok=True)
backlog='--backlog' in sys.argv
backlog_flag=QA/'backlog-visual.txt'
if backlog:backlog_flag.write_text('Backlog source layout and shutters')
else:backlog_flag.unlink(missing_ok=True)
choice_flag=QA/'choice-visual.txt'
if choices:choice_flag.write_text('Focused choice skin, native eligibility and dialogue visibility')
else:choice_flag.unlink(missing_ok=True)
feedback='--feedback' in sys.argv or presentation or choices
feedback_flag=QA/'feedback-mode.txt'
if feedback:feedback_flag.write_text('Rich choices and manual history return')
else:feedback_flag.unlink(missing_ok=True)
comic_flag=QA/'comic-only.txt'
if '--comic-only' in sys.argv:comic_flag.write_text('Resume comic gate check without repeating passed performance tests')
else:comic_flag.unlink(missing_ok=True)
hotfix='--hotfix' in sys.argv
hotfix_flag=QA/'hotfix-mode.txt'
if hotfix:hotfix_flag.write_text('Compatibility, comic reuse, tooltip and exit save regression')
else:hotfix_flag.unlink(missing_ok=True)
preview='--preview' in sys.argv
controls='--controls' in sys.argv
plane='--plane' in sys.argv
plane_flag=QA/'plane-animation-mode.txt'
if plane:plane_flag.write_text('Focused stroke animation check')
else:plane_flag.unlink(missing_ok=True)
settings='--settings' in sys.argv or '--settings-visual' in sys.argv
settings_visual=QA/'settings-visual.txt'
if '--settings-visual' in sys.argv:settings_visual.write_text('Focused redrawn settings appearance')
else:settings_visual.unlink(missing_ok=True)
settings_flag=QA/'settings-mode.txt'
if settings:settings_flag.write_text('Settings layout, native persistence, previews and skip read policy')
else:settings_flag.unlink(missing_ok=True)
local_font_flag=QA/'local-font-preview.txt'
if '--local-font' in sys.argv:local_font_flag.write_text('Explicit machine-local font preview only')
else:local_font_flag.unlink(missing_ok=True)
skin='--skin' in sys.argv or plane or settings
skin_flag=QA/'skin-mode.txt'
if skin:skin_flag.write_text('Extracted skin and logo focused validation')
else:skin_flag.unlink(missing_ok=True)
adv_only='--adv-only' in sys.argv or skin or choices or backlog or cg
if '--first-run' in sys.argv:adv_only=False
adv='--adv' in sys.argv or adv_only or '--first-run' in sys.argv
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
if '--storage-perf' in sys.argv:mode='ADV'
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
