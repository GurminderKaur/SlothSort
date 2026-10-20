"""Score a Sloth Sort plan (from `slothsort plan <folder> --json plan.json`) against samples/expected.json.

Usage: python samples/score.py plan.json [--markdown] [--annotations]
Reports, per file: right folder? sensible new name (contains an expected keyword)?
"""
import json, os, sys

plan = {p["fileName"]: p for p in json.load(open(sys.argv[1]))}
here = os.path.dirname(os.path.abspath(__file__))
expected = json.load(open(os.path.join(here, "expected.json")))
md, ann = "--markdown" in sys.argv, "--annotations" in sys.argv

rows, folder_ok, name_ok, name_n, ai_secs = [], 0, 0, 0, []
for e in expected:
    p = plan.get(e["file"])
    if not p:
        rows.append((e["file"], e["folder"], "(missing)", "", False, None, None)); continue
    f_ok = p["folder"] == e["folder"]
    n_ok = None
    if e["keywords"]:
        name_n += 1
        n_ok = any(k in p["newName"].lower() for k in e["keywords"])
        name_ok += n_ok
    folder_ok += f_ok
    if p.get("seconds"): ai_secs.append(p["seconds"])
    rows.append((e["file"], e["folder"], p["folder"], p["newName"], f_ok, n_ok, p.get("source")))

skipped_ok = "big-video.mp4.crdownload" not in plan
n = len(expected)
avg = sum(ai_secs) / len(ai_secs) if ai_secs else 0
summary = f"folder {folder_ok}/{n}, name {name_ok}/{name_n}, partial download skipped: {'yes' if skipped_ok else 'NO'}, avg AI time {avg:.1f}s/file"

if md:
    print("### Sloth Sort on the sample messy folder\n")
    print("| File | Expected folder | Chosen folder | New name | By |\n|---|---|---|---|---|")
    for f, ef, pf, nn, fo, no, src in rows:
        print(f"| `{f}` | {ef} | {'✅' if fo else '❌'} {pf} | {'' if no is None else ('✅ ' if no else '❌ ')}`{nn}` | {src or ''} |")
    print(f"\n**Score:** {summary}")
elif ann:
    for f, ef, pf, nn, fo, no, src in rows:
        if not fo or no is False:
            print(f"::notice title=Miss {f}::expected {ef}, got {pf}; name '{nn}' (by {src})")
    print(f"::notice title=Sloth Sort score::{summary}")
else:
    for f, ef, pf, nn, fo, no, src in rows:
        print(f"{'✓' if fo else '✗'} {f:34} → {pf:17} {nn}")
    print(summary)
