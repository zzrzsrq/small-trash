#!/usr/bin/env python3
"""case — 记一条案例，或看看攒了多少。

案例只记三类有判断价值的：false-negative / false-positive / surprise。
正常触发不记。

用法:
    python case.py --kind false-negative --slug desc-miss
    python case.py --list
    python case.py --stats
"""
import argparse
import os
import re
import sys
from datetime import datetime
from pathlib import Path

KINDS = ("false-negative", "false-positive", "surprise")

TEMPLATE = """---
date: {date}
kind: {kind}
---
## 当时

（一句话：在干什么，什么场景）

## 该不该触发

{should}

## 实际

（发生了什么）

## 回看

（这说明什么，要不要改 description 或形态）
"""

SHOULD = {
    "false-negative": "该。因为（哪一条触发条件命中了）",
    "false-positive": "不该。因为（这次和 skill 管的不是一回事）",
    "surprise": "该。但结果和预期不同",
}


def cases_dir() -> Path:
    v = os.environ.get("METHOD_HARVEST_HOME")
    if v:
        return Path(v).expanduser().resolve() / "cases"
    v = os.environ.get("DSH_HOME")
    if v:
        return Path(v).expanduser().resolve() / "cases"
    here = Path(__file__).resolve()
    for up in here.parents:
        if up.name == "skills":
            return up.parent / "cases"
    return here.parent.parent / "cases"


def cmd_new(d: Path, kind: str, slug: str, dry: bool) -> int:
    now = datetime.now()
    safe = re.sub(r"[^a-zA-Z0-9\u4e00-\u9fa5_-]", "-", slug)[:40].strip("-") or "case"
    path = d / f"{now:%Y%m%d-%H%M}-{safe}.md"
    if path.exists():
        print(f"已存在: {path}", file=sys.stderr)
        return 1
    body = TEMPLATE.format(date=f"{now:%Y-%m-%d}", kind=kind, should=SHOULD[kind])
    if dry:
        print(body)
        print(f"（会写到 {path}）")
        return 0
    d.mkdir(parents=True, exist_ok=True)
    path.write_text(body, encoding="utf-8")
    print(f"已建: {path}")
    print("填完四节。写得长就没人写——十几行够了。")
    return 0


def load(d: Path) -> list:
    out = []
    for p in sorted(d.glob("*.md")):
        if not p.is_file():
            continue
        try:
            text = p.read_text(encoding="utf-8-sig")
        except (OSError, UnicodeDecodeError):
            continue
        m = re.search(r"^kind:\s*(\S+)", text, re.M)
        d_ = re.search(r"^date:\s*(\S+)", text, re.M)
        # 粗判「填完了没」：模板占位符还在就是没填
        filled = "（一句话" not in text and "（发生了什么）" not in text
        out.append({
            "file": p.name,
            "kind": (m.group(1) if m else "?"),
            "date": (d_.group(1) if d_ else "?"),
            "filled": filled,
        })
    return out


def main() -> int:
    for s in (sys.stdout, sys.stderr):
        try:
            s.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, OSError):
            pass

    ap = argparse.ArgumentParser(description="案例记录")
    ap.add_argument("--kind", choices=KINDS, help="案例类型")
    ap.add_argument("--slug", default="case", help="短标题（文件名用）")
    ap.add_argument("--list", action="store_true", help="列出所有案例")
    ap.add_argument("--stats", action="store_true", help="统计")
    ap.add_argument("--dry", action="store_true", help="只打印，不写文件")
    args = ap.parse_args()

    d = cases_dir()

    if args.kind:
        return cmd_new(d, args.kind, args.slug, args.dry)

    items = load(d)

    if args.stats or args.list:
        if not items:
            print(f"还没有案例。目录: {d}")
            print("记第一条：python case.py --kind false-negative --slug xxx")
            return 0
        by_kind = {}
        for i in items:
            by_kind.setdefault(i["kind"], []).append(i)
        print(f"共 {len(items)} 条，未填完 {sum(1 for i in items if not i['filled'])} 条\n")
        for k in KINDS:
            group = by_kind.get(k, [])
            print(f"  {k:14} {len(group)} 条")
        other = [i for i in items if i["kind"] not in KINDS]
        if other:
            print(f"  {'其它':14} {len(other)} 条")

        if args.list:
            print()
            for i in items:
                flag = "" if i["filled"] else "  (未填完)"
                print(f"  {i['date']}  {i['kind']:14} {i['file']}{flag}")
        else:
            # 系统性缺口的判断：同一类 3 条以上
            for k, group in by_kind.items():
                filled = [i for i in group if i["filled"]]
                if len(filled) >= 3:
                    print(f"\n{k} 已有 {len(filled)} 条填完的——该看它们是不是同一个缺口了。")
        return 0

    print(__doc__)
    return 0


if __name__ == "__main__":
    sys.exit(main())
