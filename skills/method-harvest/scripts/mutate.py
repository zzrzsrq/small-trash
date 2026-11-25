#!/usr/bin/env python3
"""变异测试：把关键修复挨个去掉，看测试套能不能抓住。

这是判断「测试是不是假的」的可靠办法：变异存活说明那段代码没人守。
普通全绿只证明测试跑通了。

用法: python mutate.py
"""
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
INSTINCT = HERE / "instinct.py"

# 每个变异 = (名字, 正则, 替换)。正则匹配不上就跳过并报告。
MUTATIONS = [
    ("NaN 守卫", r"    if not math\.isfinite\(v\):", "    if False:"),
    ("None 守卫", r"    if not math\.isfinite\(f\):", "    if False:"),
    ("引号剥离", r"    q = v\[0\] if v\[0\] in \"\\\"'\" else None", "    q = None"),
    ("注释切分", r"    m = COMMENT_CUT\.search\(v\)", "    m = None"),
    ("GB18030 兜底", r"        text = raw\.decode\(\"gb18030\"\)",
                     "        text = raw.decode(\"utf-8\")"),
    ("二进制拒绝", r"    return \"\", f\"\{path\.name\}: 编码认不出来，已跳过（转成 UTF-8 重试）\"",
                   "    return raw.decode(\"utf-8\", errors=\"replace\"), None"),
    ("负 evidence", r"    if f < 0:", "    if False:"),
    ("日期解析", r"                entry\[f\"_\{fld\}_date\"\] = datetime\.strptime\(raw, \"%Y\-%m\-%d\"\)\.date\(\)",
                 "                entry[f\"_{fld}_date\"] = date.today()"),
    ("必填字段校验", r"        for f in REQUIRED_FIELDS:", "        for f in ():"),
    ("id 唯一校验", r"        elif eid in seen:", "        elif False:"),
    ("confidence 范围", r"        if not \(0\.0 <= c <= 1\.0\):", "        if False:"),
    ("bar clamp", r"            cells = max\(0, min\(int\(i\[\"confidence\"\] \* 10\), 10\)\)",
                  "            cells = int(i[\"confidence\"] * 10)"),
    ("stale 缺日期判断", r"        if i\.get\(\"_last_seen_bad\"\) or i\.get\(\"_last_seen_date\"\) is None:",
                         "        if False:"),
    ("门槛常量", r"THRESHOLD_CONFIDENCE = 0\.9", "THRESHOLD_CONFIDENCE = 0.5"),
    ("门槛 evidence", r"THRESHOLD_EVIDENCE = 3", "THRESHOLD_EVIDENCE = 1"),
]



def run_tests(script_dir: Path) -> tuple[int, int]:
    p = subprocess.run([sys.executable, str(script_dir / "test_instinct.py")],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    line = [l for l in p.stdout.splitlines() if l.startswith("通过")]
    if not line:
        return -1, -1
    m = re.search(r"通过 (\d+) / 失败 (\d+)", line[0])
    return (int(m.group(1)), int(m.group(2))) if m else (-1, -1)


def main() -> int:
    killed = survived = skipped = 0
    survivors = []

    for target_name, target_path, muts in (
        ("instinct.py", INSTINCT, MUTATIONS),
    ):
        if not target_path.exists():
            print(f"\n跳过 {target_name}（文件不存在）")
            continue
        orig = target_path.read_text(encoding="utf-8")

        # 基线：不改任何东西，测试应该全绿
        base_pass, base_fail = run_tests(HERE)
        if base_fail != 0:
            print(f"基线就不是全绿（{base_pass} 通过 / {base_fail} 失败），先修测试。")
            return 1

        print(f"\n── {target_name} ──")
        for name, pattern, replacement in muts:
            pat = re.compile(pattern, re.S)
            if not pat.search(orig):
                print(f"  SKIP      {name}（正则没命中，可能代码改过了）")
                skipped += 1
                continue

            mutated = pat.sub(lambda _m: replacement, orig, count=1)
            tmp = Path(tempfile.mkdtemp(prefix="mh-mut-"))
            try:
                # 整个 scripts 目录都复制，因为它依赖同目录的其它脚本
                shutil.copytree(HERE, tmp / "scripts",
                                ignore=shutil.ignore_patterns("__pycache__", "mutate.py"))
                (tmp / "scripts" / target_name).write_text(mutated, encoding="utf-8")
                passed, failed = run_tests(tmp / "scripts")
                if failed > 0:
                    killed += 1
                    print(f"  KILLED    {name}  ({failed} 个测试变红)")
                else:
                    survived += 1
                    survivors.append(f"{target_name}: {name}")
                    print(f"  SURVIVED  {name}  ← 测试抓不住，这段逻辑没被守住")
            finally:
                shutil.rmtree(tmp, ignore_errors=True)

    total = killed + survived
    print(f"\n抓住 {killed}/{total}，存活 {survived}，跳过 {skipped}")

    # 跳过 = 没测成。没测成不能算通过。
    if skipped:
        print(f"\n有 {skipped} 个变异没测成（正则没命中，代码可能改过）。")
        print("这些位置的代码现在无人守——要么更新正则，要么承认没覆盖。")
        return 1

    if survivors:
        print("\n存活清单（每一条都该补测试，或承认那段没有守）：")
        for s in survivors:
            print(f"  - {s}")
        return 1

    print("\n全部抓住（没有跳过、没有存活）。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
