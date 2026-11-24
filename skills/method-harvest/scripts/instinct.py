#!/usr/bin/env python3
"""instinct — 原子经验账本：列账、查该提升的、查该淘汰的、体检。

账本位置按优先级：
  1. --dir 参数
  2. 环境变量 METHOD_HARVEST_HOME / DSH_HOME（其下 instincts/）
  3. 贴着 skill 自己：<skill>/../instincts

用法:
    python instinct.py                     # 列出全部，按置信度排序
    python instinct.py --ready             # 只看够格提升成 skill 的
    python instinct.py --stale 30          # 看 30 天没出现的
    python instinct.py --check             # 体检：字段、范围、id 唯一、日期
    python instinct.py --init              # 建账本目录 + 样例条目
    python instinct.py --dir PATH          # 指定账本目录
"""
import argparse
import math
import os
import re
import sys
from datetime import date, datetime
from pathlib import Path

# ── 门槛：唯一权威。文档必须与此一致 ──────────────────────────
THRESHOLD_CONFIDENCE = 0.9
THRESHOLD_EVIDENCE = 3

REQUIRED_FIELDS = ("id", "trigger", "confidence", "domain", "evidence",
                   "first_seen", "last_seen")

# 关键：--- 行尾可能是 \r（CRLF），用 lookahead 容忍，不用 $
FRONTMATTER = re.compile(r"^---[ \t]*\r?\n(.*?)^---[ \t]*(?=\r?\n|\Z)(.*?)(?=^---[ \t]*(?:\r?\n|\Z)|\Z)",
                         re.M | re.S)

# 行内注释的起点：# 前面是空白，且 # 后面是空白或行尾。
# `见 #1 的坑`、`C# 项目`、`第 # 章`、`值 # 后面` 的 # 后面都跟非空格 → 保住。
COMMENT_CUT = re.compile(r"[ \t]#(?=[ \t]|$)")

SAMPLE = """---
id: example-entry
trigger: 出现这个情况时该被想起
confidence: 0.5
domain: tooling
evidence: 1
first_seen: 2026-01-01
last_seen: 2026-01-01
---
一句话说清一个动作。改成你自己的，或者删掉这条。
"""


# ── 读取 ─────────────────────────────────────────────────────
def unquote(v: str) -> str:
    """去掉 YAML 引号与行内注释。

    规则（YAML 的 ` #` 本身有歧义，这里取一条明确的线）：

    - 引号包起来的：取引号内内容，**引号内的一切原样保留**，包括 #
    - 没引号的：# 只有在「前面有空格、后面是空格或行尾」时算注释起点

    所以：
        见 #1 的坑           → 原样（# 后接数字）
        C# 项目              → 原样（# 前无空格）
        0.9 # 注释           → 0.9（# 后接空格，判定为注释）
        打 tag 时 # 后面跟版本号 → 打 tag 时（同样判定为注释，这是取舍）

    **值里真想保留「 # 」，请加引号。** 这是 YAML 本身解决不了的歧义。
    """
    v = v.strip()
    if not v:
        return v

    q = v[0] if v[0] in "\"'" else None
    if q:
        end = v.find(q, 1)
        if end > 0:
            return v[1:end].strip()

    m = COMMENT_CUT.search(v)
    if m:
        v = v[:m.start()].strip()
    return v


def read_text(path: Path) -> tuple[str, str | None]:
    """读文件，返回 (文本, 警告)。

    只在能明确判定编码时才返回文本；判定不了就返回空 + 警告，
    绝不「猜一个能解码的就算」——那会把 Big5 读成乱码还当成功。
    """
    raw = path.read_bytes()
    if not raw:
        return "", None

    # BOM 明确时用它
    if raw.startswith(b"\xef\xbb\xbf"):
        return raw[3:].decode("utf-8", errors="replace"), None
    if raw.startswith((b"\xff\xfe", b"\xfe\xff")):
        try:
            return raw.decode("utf-16"), None
        except UnicodeDecodeError:
            pass

    # 无 BOM：先严格 UTF-8
    try:
        return raw.decode("utf-8"), None
    except UnicodeDecodeError:
        pass

    # UTF-8 失败：试 GB18030（GBK 超集，中文 Windows 最常见）
    try:
        text = raw.decode("gb18030")
        return text, f"{path.name}: 不是 UTF-8，按 GB18030 读入（建议转 UTF-8）"
    except (UnicodeDecodeError, UnicodeError):
        pass

    # 都不行：拒绝解析，别猜
    return "", f"{path.name}: 编码认不出来，已跳过（转成 UTF-8 重试）"


def parse_one(chunk: str) -> dict:
    meta = {}
    for line in chunk.splitlines():
        if ":" not in line:
            continue
        k, v = line.split(":", 1)
        meta[k.strip()] = unquote(v)
    return meta


def load_file(path: Path, problems: list) -> list:
    """读一个文件里的所有条目。出错只记录，不中断。"""
    try:
        text, warn = read_text(path)
    except OSError as ex:
        problems.append(f"{path.name}: 读不出来 ({ex})")
        return []
    if warn:
        problems.append(warn)
    if not text:
        return []

    out = []
    for m in FRONTMATTER.finditer(text):
        meta = parse_one(m.group(1))
        meta["body"] = " ".join(m.group(2).split())
        meta["file"] = path.name
        out.append(meta)

    if not out and text.strip():
        problems.append(f"{path.name}: 没有解析出条目（首行应是 ---）")
    return out


def safe_float(raw, name, eid, problems, default=0.0):
    """转 float，NaN/Inf/非数字都归入 default 并记录。"""
    try:
        v = float(raw)
    except (TypeError, ValueError):
        problems.append(f"{name} [{eid}]: confidence 不是数字 ({raw!r})")
        return default
    if not math.isfinite(v):
        problems.append(f"{name} [{eid}]: confidence 是 {raw}（NaN/Inf 不接受）")
        return default
    return v


def safe_int(raw, name, eid, problems, default=0):
    """转 int，接受 3.0，拒绝 NaN/Inf/负数。"""
    try:
        f = float(raw)
    except (TypeError, ValueError):
        problems.append(f"{name} [{eid}]: evidence 不是整数 ({raw!r})")
        return default
    if not math.isfinite(f):
        problems.append(f"{name} [{eid}]: evidence 是 {raw}（NaN/Inf 不接受）")
        return default
    if f < 0:
        problems.append(f"{name} [{eid}]: evidence 是负数 ({f})")
        return default
    return int(f)


def coerce(entry: dict, problems: list) -> dict:
    """把字符串字段转成可用类型，并收集问题。"""
    name = entry.get("file", "?")
    eid = entry.get("id") or "无id"

    entry["confidence"] = safe_float(entry.get("confidence", ""), name, eid, problems)
    entry["evidence"] = safe_int(entry.get("evidence", ""), name, eid, problems)

    for fld in ("first_seen", "last_seen"):
        raw = entry.get(fld, "")
        entry[f"_{fld}_date"] = None
        entry[f"_{fld}_bad"] = False
        if raw:
            try:
                entry[f"_{fld}_date"] = datetime.strptime(raw, "%Y-%m-%d").date()
            except ValueError:
                entry[f"_{fld}_bad"] = True
                problems.append(f"{name} [{eid}]: {fld} 日期坏了 ({raw!r})")
    return entry


def load_all(ledger: Path, problems: list) -> list:
    items = []
    for p in sorted(ledger.glob("*.md")):
        if not p.is_file():
            problems.append(f"{p.name}: 是目录不是文件，已跳过")
            continue
        for meta in load_file(p, problems):
            items.append(coerce(meta, problems))
    return items


# ── 路径解析 ─────────────────────────────────────────────────
def resolve_ledger(explicit: str | None) -> Path:
    """账本目录。优先级：--dir > 环境变量 > 贴着 skill 自己。"""
    if explicit:
        return Path(explicit).expanduser().resolve()

    # METHOD_HARVEST_HOME 直接就是账本目录；DSH_HOME 下才找 instincts/
    v = os.environ.get("METHOD_HARVEST_HOME")
    if v:
        return Path(v).expanduser().resolve()
    v = os.environ.get("DSH_HOME")
    if v:
        return Path(v).expanduser().resolve() / "instincts"

    # 默认：从脚本位置往上找 skills/ 的父目录（DSH 布局是 <home>/skills/<name>/scripts/）
    here = Path(__file__).resolve()
    for up in here.parents:
        if up.name == "skills":
            return up.parent / "instincts"
    # 不在 skills/ 下（比如 clone 成独立仓库）：贴着自己
    return here.parent.parent / "instincts"


# ── 命令 ─────────────────────────────────────────────────────
def cmd_init(ledger: Path) -> int:
    try:
        ledger.mkdir(parents=True, exist_ok=True)
    except (OSError, FileExistsError) as ex:
        print(f"建不了账本目录 {ledger}：{ex}", file=sys.stderr)
        print("看看这个路径是不是已经是个文件了。", file=sys.stderr)
        return 2
    sample = ledger / "_example.md"
    if sample.exists():
        print(f"账本已存在: {ledger}")
        print("（样例还在，可以删了）")
        return 0
    try:
        sample.write_text(SAMPLE, encoding="utf-8")
    except OSError as ex:
        print(f"样例写不进去：{ex}", file=sys.stderr)
        return 2
    print(f"已建账本目录: {ledger}")
    print(f"已放样例:     {sample.name}")
    print("改它或删它，然后开始记账。")
    return 0


def cmd_check(ledger: Path, items: list, problems: list) -> int:
    bad = list(problems)

    seen = {}
    for it in items:
        eid = it.get("id", "")
        fname = it.get("file", "?")
        if not eid:
            bad.append(f"{fname}: id 为空")
        elif eid in seen:
            bad.append(f"{fname} [{eid}]: id 与 {seen[eid]} 重复")
        else:
            seen[eid] = fname

        for f in REQUIRED_FIELDS:
            if not str(it.get(f, "")).strip():
                bad.append(f"{fname} [{eid or '无id'}]: 缺字段 {f}")

        for fld in ("first_seen", "last_seen"):
            raw_date = str(it.get(fld, "")).strip()
            if raw_date and it.get(f"_{fld}_date") is None:
                # 有值但解析不出来 —— 只有一条判定路径
                bad.append(f"{fname} [{eid or '无id'}]: {fld} 不是 YYYY-MM-DD ({raw_date!r})")

        c = it.get("confidence", 0.0)
        if not (0.0 <= c <= 1.0):
            bad.append(f"{fname} [{eid or '无id'}]: confidence 越界 ({c})，应在 0–1")

    if bad:
        print(f"体检发现 {len(bad)} 个问题：\n")
        for b in bad:
            print(f"  - {b}")
        print("\n修完再跑一次。")
        return 1

    print(f"体检通过：{len(items)} 条，{len(seen)} 个 id，字段/范围/唯一性都没问题。")
    return 0


def cmd_list(items: list, domain: str | None) -> int:
    if domain:
        items = [i for i in items if i.get("domain") == domain]
    if not items:
        print("账本里 0 条。" if not domain else f"domain={domain} 里 0 条。")
        return 0
    print(f"{len(items)} 条 instinct\n")
    for i in sorted(items, key=lambda x: -x["confidence"]):
        # clamp 到 0–10：confidence 可能是有限但很大的值（1e9），
        # 直接 int(c*10) 会 OverflowError 让整本账本挂掉
        try:
            cells = max(0, min(int(i["confidence"] * 10), 10))
        except (OverflowError, ValueError):
            cells = 0
        bar = "#" * cells
        print(f"  {i['confidence']:.2f} {bar:<10} {i.get('id', '?')}")
        print(f"        {i.get('domain') or '?':<12} {i['evidence']}次  "
              f"最后 {i.get('last_seen') or '?'}")
        print(f"        {i.get('body', '')[:90]}")
        print()
    return 0


def cmd_ready(items: list, domain: str | None) -> int:
    if domain:
        items = [i for i in items if i.get("domain") == domain]
    ready = [i for i in items
             if i["confidence"] >= THRESHOLD_CONFIDENCE
             and i["evidence"] >= THRESHOLD_EVIDENCE]
    if not ready:
        print(f"还没有够格的。门槛：置信 ≥{THRESHOLD_CONFIDENCE} 且 证据 ≥{THRESHOLD_EVIDENCE}。")
        return 0
    print(f"{len(ready)} 条可以提升成 skill：\n")
    groups = {}
    for i in ready:
        groups.setdefault(i.get("domain") or "?", []).append(i)
    for dom, group in groups.items():
        print(f"  [{dom}]")
        for i in group:
            print(f"    {i.get('id')}  ({i['confidence']}, {i['evidence']}次)")
            print(f"      {i.get('body', '')[:80]}")
        print()
    return 0


def cmd_stale(items: list, days: int) -> int:
    today = date.today()
    stale, undated = [], []
    for i in items:
        if i.get("_last_seen_bad") or i.get("_last_seen_date") is None:
            undated.append(i)
            continue
        delta = (today - i["_last_seen_date"]).days
        if delta >= days:
            stale.append((delta, i))

    rc = 0
    if stale:
        print(f"{len(stale)} 条超过 {days} 天没出现：\n")
        for d, i in sorted(stale, key=lambda t: -t[0]):
            print(f"  {d:>5}天  {i.get('id')}  ({i['confidence']})  [{i.get('domain') or '?'}]")
        print("\n逐条问一句：它还会发生吗？会 → 更新 last_seen；不会 → 删掉。")
    else:
        print(f"没有超过 {days} 天没出现的。")

    if undated:
        print(f"\n还有 {len(undated)} 条日期缺失或坏了，没法判断新旧：")
        for i in undated:
            print(f"  {i.get('file')} [{i.get('id') or '无id'}]: "
                  f"last_seen={i.get('last_seen') or '缺'}")
        print("用 --check 看详情。")
        rc = 1
    return rc


def main() -> int:
    # 中文 Windows 控制台默认 GBK，正文里的 emoji/生僻字会崩 print。
    # 统一改成 UTF-8；做不到就退成 ASCII 转义，绝不因为输出崩掉。
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, OSError):
            pass

    ap = argparse.ArgumentParser(description="原子经验账本")
    ap.add_argument("--dir", help="账本目录（覆盖自动推断）")
    ap.add_argument("--ready", action="store_true", help="只看够格提升的")
    ap.add_argument("--domain", help="只看某个领域")
    ap.add_argument("--stale", type=int, metavar="DAYS", help="看 N 天没出现的")
    ap.add_argument("--check", action="store_true", help="体检：字段/范围/id/日期")
    ap.add_argument("--init", action="store_true", help="建账本目录 + 样例")
    args = ap.parse_args()

    ledger = resolve_ledger(args.dir)

    if args.init:
        return cmd_init(ledger)

    if ledger is None or not ledger.is_dir():
        print(f"找不到账本目录: {ledger}", file=sys.stderr)
        print(f"建它：python {Path(sys.argv[0]).name} --init --dir \"{ledger}\"", file=sys.stderr)
        return 2

    problems: list = []
    items = load_all(ledger, problems)

    if args.check:
        return cmd_check(ledger, items, problems)

    if problems:
        print("读到几个问题（不影响下面的结果）：", file=sys.stderr)
        for p in problems:
            print(f"  - {p}", file=sys.stderr)
        print("", file=sys.stderr)

    if args.ready:
        return cmd_ready(items, args.domain)
    if args.stale is not None:
        if args.stale < 0:
            print("--stale 要 ≥0", file=sys.stderr)
            return 2
        return cmd_stale(items, args.stale)
    return cmd_list(items, args.domain)


if __name__ == "__main__":
    sys.exit(main())
