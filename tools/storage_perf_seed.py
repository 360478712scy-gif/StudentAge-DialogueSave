#!/usr/bin/env python3
"""Seed the isolated QA save folder with copies of a long dialogue-save history, then verify or restore it.

seed <source-folder>  back up qa/runtime/data once, copy dialogue_*.dsav (read-only source) and record
                      which files must survive: everything that is not a checkpoint superseded in its own slot.
verify                compare the isolated folder with the recorded expectation (hashes, not names only).
restore               put the backed-up qa/runtime/data back.
Only qa/runtime is written.
"""
from pathlib import Path
import hashlib, json, shutil, subprocess, sys

ROOT = Path(__file__).resolve().parents[1]
QA = ROOT/'qa/runtime'
DATA, BACKUP, EXPECT = QA/'data', QA/'data.storage-perf-backup', QA/'storage-perf-expected.json'

def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()

def classify(folder):
    rows = {}
    for p in folder.glob('dialogue_*.dsav'):
        try: j = json.loads(p.read_text(encoding='utf-8'))
        except Exception: rows[p.name] = None; continue
        rows[p.name] = j
    superseded = set()
    ids = {j['Header']['RevisionId']: n for n, j in rows.items() if j}
    for n, j in rows.items():
        if not j or j.get('Kind') != 'checkpoint' or j['Header'].get('TransactionId'): continue
        h = j['Header']
        for parent in h.get('ParentRevisionIds') or []:
            pn = ids.get(parent)
            if not pn: continue
            pj = rows[pn]; ph = pj['Header']
            same = all(ph.get(k) == h.get(k) for k in ('SteamId', 'RunId', 'Category', 'LogicalSlot'))
            if same and pj.get('Kind') == 'checkpoint' and not ph.get('TransactionId'): superseded.add(pn)
    return rows, superseded

def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else ''
    if cmd == 'seed':
        source = Path(sys.argv[2])
        saves = [d for d in (DATA/'Saves').iterdir() if d.is_dir() and d.name.isdigit()]
        if len(saves) != 1: raise SystemExit('expected exactly one isolated Steam folder')
        if not BACKUP.exists(): subprocess.run(['/bin/cp', '-cR', str(DATA), str(BACKUP)], check=True)
        target = saves[0]; retained = DATA/'DialogueSaveLocal'/target.name/'Backups/Retained'
        retained.mkdir(parents=True, exist_ok=True)
        copied = 0
        for p in sorted(source.glob('dialogue_*.dsav')):
            for folder in (target, retained):
                if not (folder/p.name).exists(): shutil.copyfile(p, folder/p.name)
            copied += 1
        rows, superseded = classify(target)
        keep = {n: sha(target/n) for n in rows if n not in superseded}
        EXPECT.write_text(json.dumps({'folder': str(target), 'copied': copied, 'files': len(rows),
            'superseded': sorted(superseded), 'keep': keep}, indent=1))
        print(f'SEEDED copied={copied} files={len(rows)} keep={len(keep)} superseded={len(superseded)}')
    elif cmd == 'verify':
        e = json.loads(EXPECT.read_text()); target = Path(e['folder'])
        now = {p.name for p in target.glob('dialogue_*.dsav')}
        missing = [n for n, h in e['keep'].items() if n not in now or sha(target/n) != h]
        removed = sorted(set(e['keep']) | set(e['superseded']))
        removed = [n for n in removed if n not in now]
        wrong = [n for n in removed if n not in e['superseded']]
        size = sum((target/n).stat().st_size for n in now)
        print(json.dumps({'filesNow': len(now), 'bytesNow': size, 'removed': len(removed),
            'keptFilesMissingOrChanged': missing, 'removedButNotSuperseded': wrong}, ensure_ascii=False, indent=1))
        if missing or wrong: raise SystemExit('VERIFY_FAILED')
        print('VERIFY_OK')
    elif cmd == 'restore':
        if not BACKUP.exists(): raise SystemExit('no backup')
        shutil.rmtree(DATA); BACKUP.rename(DATA); EXPECT.unlink(missing_ok=True); print('RESTORED')
    else: raise SystemExit(__doc__)

if __name__ == '__main__': main()
