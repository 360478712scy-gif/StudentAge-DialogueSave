#!/usr/bin/env python3
"""Small, read-only project knowledge lookup. Python standard library only."""
import argparse
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
DATABASE = ROOT / "docs/knowledge/project.json"


def validate(data):
    errors, ids = [], set()
    entries = data.get("entries", [])
    if data.get("schema_version") != 1 or not 1 <= len(entries) <= 24:
        errors.append("Schema must be 1 and topic count must be 1..24")
    for entry in entries:
        key = entry.get("id", "")
        if not re.fullmatch(r"[a-z]+\.[a-z]+", key) or key in ids:
            errors.append(f"Invalid or duplicate id: {key}")
        ids.add(key)
        if not entry.get("title"):
            errors.append(f"Missing title: {key}")
        for field in ("keywords", "facts", "files"):
            values = entry.get(field)
            if not isinstance(values, list) or not values or not all(isinstance(v, str) and v.strip() for v in values):
                errors.append(f"Invalid {field}: {key}")
        if len(entry.get("facts", [])) > 5:
            errors.append(f"More than 5 facts: {key}")
        for name in entry.get("files", []):
            path = Path(name)
            if path.is_absolute() or ".." in path.parts or not (ROOT / path).is_file():
                errors.append(f"Missing/invalid repository file: {key}: {name}")
        # evidence is deliberately not required: ignored qa/ is machine-local.
    return errors


def score(entry, query):
    terms = query.casefold().split()
    heading = (entry["id"] + " " + entry["title"]).casefold()
    keywords = [word.casefold() for word in entry["keywords"]]
    body = " ".join(entry["facts"] + entry["files"]).casefold()
    return sum(12 * (term in heading) + 8 * sum(term in word or word in term for word in keywords)
               + 2 * (term in body) for term in terms)


def display(entry):
    print(f"[{entry['id']}] {entry['title']}")
    for fact in entry["facts"]:
        print(f"  - {fact}")
    print("  文件: " + ", ".join(entry["files"]))
    if entry.get("check"):
        print("  验证入口（仅打印，不执行）: " + entry["check"])
    if entry.get("evidence"):
        print("  历史证据（仅本机，须复核）: " + (", ".join(entry["evidence"]) if isinstance(entry["evidence"], list) else entry["evidence"]))
    print()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("query", nargs="*", help="Chinese or English keywords")
    parser.add_argument("--id", help="Exact topic id")
    parser.add_argument("--limit", type=int, default=3, help="Maximum results (default 3)")
    parser.add_argument("--check", action="store_true", help="Check schema, size and file references")
    args = parser.parse_args()
    if not 1 <= args.limit <= 24:
        parser.error("--limit must be between 1 and 24")
    data = json.loads(DATABASE.read_text(encoding="utf-8"))
    if args.check:
        errors = validate(data)
        if DATABASE.stat().st_size > 30 * 1024:
            errors.append("Knowledge database exceeds 30 KB; consolidate existing topics")
        if errors:
            raise SystemExit("\n".join(errors))
        print(f"KNOWLEDGE_OK {len(data['entries'])} topics; repository references verified")
        return
    entries = data["entries"]
    query = " ".join(args.query).strip()
    if args.id:
        matches = [entry for entry in entries if entry["id"] == args.id]
    elif query:
        ranked = sorted(((score(entry, query), entry) for entry in entries), key=lambda pair: -pair[0])
        matches = [entry for rank, entry in ranked if rank > 0][:args.limit]
    else:
        for entry in entries:
            print(f"{entry['id']:24} {entry['title']}")
        return
    if not matches:
        print("未命中。缩短关键词或无参数列主题；然后仅在相关目录使用 rg。")
        return
    for entry in matches:
        display(entry)


if __name__ == "__main__":
    main()
