#!/usr/bin/env python3
"""Prepare/restore an isolated QA run that probes dialogue saveability across subscribed story mods.

setup    back up the isolated mod lists once, enable every story mod (native list only), copy UP and
         mod-shipped plugin DLLs into the isolated plugins folder (rewritten by prepare_qa), clone mod
         folders for UP's resource lookup, and write qa/runtime/mod-sweep-plan.json.
restore  put the isolated lists back and remove the extra plugins/clones.
Only qa/runtime is written; the workshop folders are read.
"""
from pathlib import Path
import json, os, shutil, subprocess, sys

ROOT = Path(__file__).resolve().parents[1]
QA = ROOT/'qa/runtime'
WS = Path(os.environ.get('STUDENTAGE_WORKSHOP', str(Path.home()/'Library/Application Support/CrossOver/Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/workshop/content/1991040')))
UP, SELF, MINIGAMES = '3782171338', '3804899191', '3802190178'
BACKUP = QA/'mod-sweep-backup'
EXTRA = QA/'BepInEx/plugins/ModSweep'

def save_dir():
    dirs = [d for d in (QA/'data/Saves').iterdir() if d.is_dir() and d.name.isdigit()]
    if len(dirs) != 1: raise SystemExit('expected one isolated Steam folder')
    return dirs[0]

def load(path):
    text = path.read_text(encoding='utf-8-sig', errors='replace')
    try: data = json.loads(text)
    except Exception:
        # The game's reader tolerates comments and trailing commas.
        import re
        lenient = re.sub(r'(?m)^\s*//.*$', '', text)
        lenient = re.sub(r',(\s*[}\]])', r'\1', lenient)
        try: data = json.loads(lenient, strict=False)
        except Exception as e:
            print('UNPARSED', path, str(e)[:80]); return None
    return {int(r['id']): r for r in (data.values() if isinstance(data, dict) else data) if isinstance(r, dict) and 'id' in r}

def chain(talks, options, start, limit=40):
    seen, out, todo = set(), [], [start]
    while todo and len(out) < limit:
        t = todo.pop(0)
        if t in seen or t not in talks: continue
        seen.add(t); row = talks[t]; out.append(row)
        nxt = row.get('nextTalk') or []
        if nxt: todo.insert(0, nxt[0])
        for o in (row.get('option') or [])[:1]:
            ids = (options.get(o) or {}).get('talkId') or []
            if ids: todo.insert(0, ids[0])
    return out

def plan():
    mods = []
    for d in sorted(WS.iterdir()):
        cfg = d/'Cfgs/zh-cn'
        if d.name in (UP, SELF, MINIGAMES) or not (cfg/'EvtCfg.json').exists() or not (cfg/'TalkCfg.json').exists(): continue
        events, talks, options = load(cfg/'EvtCfg.json'), load(cfg/'TalkCfg.json'), load(cfg/'OptionCfg.json') if (cfg/'OptionCfg.json').exists() else {}
        title = ''
        try: title = json.loads((d/'manifest.json').read_text(encoding='utf-8-sig')).get('title', '')
        except Exception: pass
        if not events or not talks: mods.append({'mod': d.name, 'title': title, 'events': [], 'note': 'config not parsed'}); continue
        scored = []
        for e in events.values():
            ids = e.get('talkId') or []
            if not ids or ids[0] not in talks: continue
            lines = chain(talks, options or {}, ids[0])
            effects = sorted({int((r.get('screenEffect') or [0])[0]) for r in lines if r.get('screenEffect')})
            scored.append((len(effects), len(lines), e['id'], effects))
        if not scored: mods.append({'mod': d.name, 'title': title, 'events': []}); continue
        special = max(scored, key=lambda s: (s[0], s[1]))
        plain = next((s for s in sorted(scored, key=lambda s: s[2]) if s[2] != special[2] and s[1] >= 5), None)
        picks = [special] + ([plain] if plain else [])
        mods.append({'mod': d.name, 'title': title, 'events': [{'id': p[2], 'lines': p[1], 'effects': p[3]} for p in picks]})
    return mods

def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else ''
    sv = save_dir()
    if cmd == 'setup':
        BACKUP.mkdir(exist_ok=True)
        for src, name in ((QA/'BepInEx/config/enabled_mods.txt', 'enabled_mods.txt'), (sv/'_mod', '_mod')):
            if not (BACKUP/name).exists() and src.exists(): shutil.copy2(src, BACKUP/name)
        mods = plan()
        native = [m['mod'] for m in mods] + [UP, '3673850497']
        (sv/'_mod').write_text('\n'.join(dict.fromkeys(native + [SELF])) + '\n')
        # BepInEx keeps loading only the isolated plugin copies.
        (QA/'BepInEx/config/enabled_mods.txt').write_text(SELF + '\n')
        EXTRA.mkdir(parents=True, exist_ok=True)
        shutil.copy2(WS/UP/'plugins/EC2BUnofficialPatch.dll', QA/'BepInEx/plugins/EC2BUnofficialPatch.dll')
        for m in mods:
            # Mod plugins resolve their art relative to their own folder: clone the whole mod.
            if (WS/m['mod']/'plugins').is_dir() and not (EXTRA/m['mod']).exists():
                subprocess.run(['/bin/cp', '-cR', str(WS/m['mod']), str(EXTRA/m['mod'])], check=True)
        author = QA/'author-workshop'; author.mkdir(exist_ok=True)
        for mod in native:
            if not (author/mod).exists() and (WS/mod).is_dir(): subprocess.run(['/bin/cp', '-cR', str(WS/mod), str(author/mod)], check=True)
        (QA/'mod-sweep-plan.json').write_text(json.dumps(mods, ensure_ascii=False, indent=1))
        print('PLAN mods=%d events=%d plugins=%s' % (len(mods), sum(len(m['events']) for m in mods), sorted(p.name for p in EXTRA.iterdir())))
    elif cmd == 'restore':
        for name, dst in (('enabled_mods.txt', QA/'BepInEx/config/enabled_mods.txt'), ('_mod', sv/'_mod')):
            if (BACKUP/name).exists(): shutil.copy2(BACKUP/name, dst)
        shutil.rmtree(EXTRA, ignore_errors=True); (QA/'BepInEx/plugins/EC2BUnofficialPatch.dll').unlink(missing_ok=True)
        (QA/'isolation-verified.txt').unlink(missing_ok=True)
        print('RESTORED (author-workshop clones kept; prepare_qa must run again)')
    else: raise SystemExit(__doc__)

if __name__ == '__main__': main()
