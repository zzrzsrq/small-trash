#!/usr/bin/env python3
"""instinct.py 回归测试。每个用例对应一个已知 bug。

用法: python test_instinct.py
"""
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "instinct.py"

PASS, FAIL = [], []


def run(args, cwd, env=None):
    e = dict(os.environ)
    # 清掉会干扰推断的环境变量，测试要控制它们
    for k in ("METHOD_HARVEST_HOME", "DSH_HOME"):
        e.pop(k, None)
    if env:
        e.update(env)
    p = subprocess.run(
        [sys.executable, str(SCRIPT)] + args,
        cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace", env=e,
    )
    return p.returncode, p.stdout, p.stderr


def run_raw(args, cwd):
    """不指定编码跑，字节原样返回——用来测真实控制台的编码行为。"""
    e = {k: v for k, v in os.environ.items()
         if k not in ("METHOD_HARVEST_HOME", "DSH_HOME")}
    p = subprocess.run([sys.executable, str(SCRIPT)] + args,
                       cwd=cwd, capture_output=True, env=e)
    return p.returncode, p.stdout, p.stderr


def case(name):
    def deco(fn):
        def wrapper():
            tmp = Path(tempfile.mkdtemp(prefix="mh-test-"))
            try:
                fn(tmp)
                PASS.append(name)
                print(f"  PASS  {name}")
            except AssertionError as ex:
                FAIL.append(name)
                print(f"  FAIL  {name}\n        {ex}")
            finally:
                shutil.rmtree(tmp, ignore_errors=True)
        wrapper.__name__ = name
        CASES.append(wrapper)
        return wrapper
    return deco


CASES = []


def write_entry(d, fname, **fields):
    """按 instinct.md 的模板写一条。fields 覆盖默认值。"""
    d.mkdir(parents=True, exist_ok=True)
    base = {
        "id": "e1", "trigger": "触发条件", "confidence": "0.7",
        "domain": "tooling", "evidence": "2",
        "first_seen": "2026-01-01", "last_seen": "2026-01-01",
    }
    base.update(fields)
    body = "---\n"
    for k, v in base.items():
        if v is None:
            continue
        body += f"{k}: {v}\n"
    body += "---\n一个动作。\n"
    (d / fname).write_text(body, encoding="utf-8")
    return d / fname


# ── B1: 账本位置 ──────────────────────────────────────────────
@case("B1a --dir 指定账本目录，跳过布局推断")
def _(tmp):
    led = tmp / "ledger"
    write_entry(led, "a.md", id="x1", confidence="0.9", evidence="3")
    rc, out, err = run(["--dir", str(led)], cwd=str(tmp))
    assert rc == 0, f"exit={rc} stderr={err}"
    assert "x1" in out, f"没列出条目: {out}"


@case("B1b 环境变量 METHOD_HARVEST_HOME 生效")
def _(tmp):
    led = tmp / "led2"
    write_entry(led, "a.md", id="x2")
    rc, out, _ = run([], cwd=str(tmp), env={"METHOD_HARVEST_HOME": str(led)})
    assert rc == 0, f"exit={rc}"
    assert "x2" in out, out


@case("B1c 默认贴着自己：<skill>/../instincts")
def _(tmp):
    # 造 DSH 式布局: skills/method-harvest/scripts/ + skills/../instincts
    root = tmp / "home"
    (root / "skills" / "method-harvest" / "scripts").mkdir(parents=True)
    shutil.copy(SCRIPT, root / "skills" / "method-harvest" / "scripts" / "instinct.py")
    write_entry(root / "instincts", "a.md", id="x3")
    p = subprocess.run(
        [sys.executable, str(root / "skills" / "method-harvest" / "scripts" / "instinct.py")],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        env={k: v for k, v in os.environ.items()
             if k not in ("METHOD_HARVEST_HOME", "DSH_HOME")},
    )
    assert p.returncode == 0, f"exit={p.returncode} {p.stderr}"
    assert "x3" in p.stdout, p.stdout


@case("B1d 目录不存在 → 非零退出 + 给出修复命令")
def _(tmp):
    rc, out, err = run(["--dir", str(tmp / "nope")], cwd=str(tmp))
    assert rc != 0, f"应该非零退出，实际 {rc}"
    assert "--init" in (out + err), f"没提示 --init: {out}{err}"


@case("B1e --init 能建目录并给样例")
def _(tmp):
    led = tmp / "newled"
    rc, out, err = run(["--dir", str(led), "--init"], cwd=str(tmp))
    assert rc == 0, f"{rc} {err}"
    assert led.exists(), "目录没建"
    mds = list(led.glob("*.md"))
    assert mds, "没生成样例"
    rc2, out2, _ = run(["--dir", str(led)], cwd=str(tmp))
    assert rc2 == 0 and "0." in out2, f"样例读不出来: {out2}"


# ── B2: 空账本语义 ────────────────────────────────────────────
@case("B2 空账本 → exit 0 + 明确说空")
def _(tmp):
    led = tmp / "empty"
    led.mkdir()
    rc, out, err = run(["--dir", str(led)], cwd=str(tmp))
    assert rc == 0, f"空账本不该算失败，exit={rc}"
    assert "0" in out or "空" in out, f"没说清是空的: {out}"


# ── B3: YAML 解析 ─────────────────────────────────────────────
@case("B3a 带引号的 confidence 正确解析")
def _(tmp):
    led = tmp / "q"
    write_entry(led, "a.md", id="q1", confidence='"0.9"', evidence='"3"')
    rc, out, _ = run(["--dir", str(led), "--ready"], cwd=str(tmp))
    assert "q1" in out, f'confidence "0.9" 没被识别成 0.9: {out}'


@case("B3b 行内注释被忽略")
def _(tmp):
    led = tmp / "c"
    write_entry(led, "a.md", id="c1", confidence="0.9  # 已验证三次", evidence="3")
    rc, out, _ = run(["--dir", str(led), "--ready"], cwd=str(tmp))
    assert "c1" in out, f"行内注释导致解析失败: {out}"


@case("B3c evidence 写成 3.0 也能用")
def _(tmp):
    led = tmp / "f"
    write_entry(led, "a.md", id="f1", confidence="0.9", evidence="3.0")
    rc, out, _ = run(["--dir", str(led), "--ready"], cwd=str(tmp))
    assert "f1" in out, f"evidence 3.0 没被接受: {out}"


# ── B4: 日期与过期 ────────────────────────────────────────────
@case("B4a 带引号的 last_seen 能被 --stale 认出")
def _(tmp):
    led = tmp / "d"
    write_entry(led, "a.md", id="d1", last_seen='"2020-01-01"')
    rc, out, err = run(["--dir", str(led), "--stale", "30"], cwd=str(tmp))
    assert "d1" in out, f"5 年前的条目没被捞出: {out}"


@case("B4b 日期坏了 → 单独报出来，不当成没过期")
def _(tmp):
    led = tmp / "bad"
    write_entry(led, "a.md", id="b1", last_seen="not-a-date")
    rc, out, err = run(["--dir", str(led), "--check"], cwd=str(tmp))
    combined = out + err
    assert "b1" in combined and ("日期" in combined or "last_seen" in combined), \
        f"坏日期没被报出来: {combined}"


@case("B4c 缺 last_seen 时 --stale 不崩且报出来")
def _(tmp):
    led = tmp / "nols"
    write_entry(led, "a.md", id="nl1", last_seen=None)
    rc, out, err = run(["--dir", str(led), "--stale", "30"], cwd=str(tmp))
    combined = out + err
    assert "Traceback" not in combined, f"缺 last_seen 让 --stale 崩了:\n{err}"
    assert "nl1" in combined, f"缺日期的条目没被列出来: {combined}"
    assert rc == 1, f"有没法判断的条目时应返回 1，实际 {rc}"


@case("B4d --stale 遇到坏日期不崩")
def _(tmp):
    led = tmp / "badstale"
    write_entry(led, "a.md", id="bd1", last_seen="20xx-01-01")
    write_entry(led, "b.md", id="bd2", confidence="0.9")  # 好的基线
    rc, out, err = run(["--dir", str(led), "--stale", "30"], cwd=str(tmp))
    assert "Traceback" not in (out + err), f"坏日期让 --stale 崩了:\n{err}"
    assert "bd1" in (out + err), f"坏日期条目没被指出: {out}{err}"


@case("B4e confidence 有限大值（1e9）不让列表崩")
def _(tmp):
    led = tmp / "huge"
    write_entry(led, "a.md", id="hg1", confidence="1e9")
    write_entry(led, "b.md", id="hg2", confidence="1e200")
    for args in ([], ["--ready"], ["--stale", "30"], ["--check"]):
        rc, out, err = run(["--dir", str(led)] + args, cwd=str(tmp))
        assert "Traceback" not in (out + err), f"{args} 被大数值搞崩:\n{err}"
        assert rc in (0, 1, 2), f"{args} 异常退出码 {rc}"


# ── B5: 编码与坏文件 ──────────────────────────────────────────
@case("B5a GBK 文件能被读出来（内容正确，不是乱码）")
def _(tmp):
    led = tmp / "g"
    write_entry(led, "good.md", id="g1")
    gbk_body = ("---\nid: gbk1\ntrigger: 中文字段\nconfidence: 0.6\n"
                "domain: d\nevidence: 2\nfirst_seen: 2026-01-01\n"
                "last_seen: 2026-01-01\n---\n这是中文正文。\n")
    (led / "gbk.md").write_bytes(gbk_body.encode("gbk"))
    rc, out, err = run(["--dir", str(led)], cwd=str(tmp))
    assert "g1" in out, f"好文件丢了: {out}"
    assert "gbk1" in out, f"GBK 文件没被读出: {out}"
    assert "这是中文正文" in out, f"GBK 正文是乱码: {out}"


@case("B5a2 认不出编码时拒绝解析，不静默当乱码")
def _(tmp):
    led = tmp / "unk"
    write_entry(led, "good.md", id="u1")
    # 随机二进制，任何编码都解不出合法中文
    (led / "bin.md").write_bytes(b"---\nid: bin1\n" + bytes(range(128, 256)) * 4)
    rc, out, err = run(["--dir", str(led)], cwd=str(tmp))
    assert "u1" in out, f"好文件丢了: {out}"
    assert "编码" in (out + err) or "跳过" in (out + err), \
        f"认不出编码却没提示: {out} {err}"


@case("B5b 目录冒充 .md 不崩")
def _(tmp):
    led = tmp / "dirfake"
    write_entry(led, "good.md", id="h1")
    (led / "weird.md").mkdir()
    rc, out, err = run(["--dir", str(led)], cwd=str(tmp))
    assert rc == 0 and "h1" in out, f"目录冒充文件导致崩溃: rc={rc} {err}"


@case("B5c NaN / Inf 不让整本账本崩")
def _(tmp):
    led = tmp / "nan"
    write_entry(led, "a.md", id="n1", confidence="nan")
    write_entry(led, "b.md", id="n2", confidence="1e999")
    write_entry(led, "c.md", id="n3", evidence="inf")
    write_entry(led, "d.md", id="ok1", confidence="0.8", evidence="2")
    for args in ([], ["--ready"], ["--stale", "30"], ["--check"]):
        rc, out, err = run(["--dir", str(led)] + args, cwd=str(tmp))
        assert "Traceback" not in (out + err), f"{args} 崩了:\n{err}"
        assert rc in (0, 1, 2), f"{args} 异常退出码 {rc}"
    rc, out, _ = run(["--dir", str(led)], cwd=str(tmp))
    assert "ok1" in out, f"好的条目没列出来: {out}"


@case("B5d 引号 + 行内注释同时出现")
def _(tmp):
    led = tmp / "qc"
    # 单引号包 + 尾注释：只有「先剥引号再切注释」的实现能对
    write_entry(led, "a.md", id="qc1", confidence="'0.9'",
                evidence="'3'  # 三次", last_seen="'2020-01-01'")
    rc, out, _ = run(["--dir", str(led), "--ready"], cwd=str(tmp))
    assert "qc1" in out, f"单引号+注释 没被正确解析: {out}"
    rc, out, _ = run(["--dir", str(led), "--stale", "30"], cwd=str(tmp))
    assert "qc1" in out, f"带引号的旧日期没被 --stale 认出: {out}"


@case("B5e trigger 里的 # 不被误切（含引号的保留规则）")
def _(tmp):
    led = tmp / "hash"
    # 无歧义的情况：# 前无空格、或 # 后接非空格 → 必须原样保留
    write_entry(led, "a.md", id="hash1", trigger="见 #1 的坑")
    write_entry(led, "b.md", id="hash2", trigger="C# 项目")
    write_entry(led, "c.md", id="hash3", trigger="见 #a 的坑")
    # 有歧义的情况：加引号 = 保留一切（这是文档给出的解法）
    write_entry(led, "d.md", id="hash4", trigger="'打 tag 时 # 后面跟版本号'")
    write_entry(led, "e.md", id="hash5", trigger="'第 # 章'")
    # 真注释：无引号且 # 后接空格 → 应被切掉
    write_entry(led, "f.md", id="hash6", trigger="正常触发 # 这是注释")
    rc, out, err = run(["--dir", str(led), "--check"], cwd=str(tmp))
    assert "Traceback" not in (out + err), err
    driver = f'''
import importlib.util, json
spec = importlib.util.spec_from_file_location("i", {str(SCRIPT)!r})
m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
probs = []
items = m.load_all(__import__("pathlib").Path({str(led)!r}), probs)
print("JSON=" + json.dumps({{i.get("id"): i.get("trigger") for i in items}}, ensure_ascii=False))
'''
    d = tmp / "drv.py"
    d.write_text(driver, encoding="utf-8")
    p = subprocess.run([sys.executable, str(d)], capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    line = [l for l in p.stdout.splitlines() if l.startswith("JSON=")]
    assert line, f"driver 没输出: {p.stdout} {p.stderr}"
    got = __import__("json").loads(line[0][5:])
    assert got.get("hash1") == "见 #1 的坑", f"hash1: {got.get('hash1')!r}"
    assert got.get("hash2") == "C# 项目", f"hash2: {got.get('hash2')!r}"
    assert got.get("hash3") == "见 #a 的坑", f"hash3: {got.get('hash3')!r}"
    assert got.get("hash4") == "打 tag 时 # 后面跟版本号", f"引号没保住 #{got.get('hash4')!r}"
    assert got.get("hash5") == "第 # 章", f"引号没保住 #{got.get('hash5')!r}"
    assert got.get("hash6") == "正常触发", f"注释没被切: {got.get('hash6')!r}"


@case("B5f 输出含 emoji 不崩（子进程层级）")
def _(tmp):
    led = tmp / "emoji"
    write_entry(led, "a.md", id="e1", trigger="ok ✅ done 中文")
    # 真实子进程层级测：管道下 Python 默认 utf-8，测的是「不因编码异常退出」
    rc, out, err = run(["--dir", str(led)], cwd=str(tmp))
    assert rc == 0, f"exit={rc} {err}"
    assert "Traceback" not in (out + err), f"崩了:\n{err}"
    # 说明：GBK strict 控制台那条路径无法在本测试环境模拟
    # （argparse 会重新绑定 sys.stdout，把假的 TextIOWrapper 冲掉），
    # 代码里的 reconfigure 是正确的兜底，但没有测试覆盖——见 SKILL.md「已知限制」。


@case("B5g --init 指向已存在的文件不裸崩")
def _(tmp):
    exists = tmp / "occupied"
    exists.write_text("我是文件", encoding="utf-8")
    rc, out, err = run(["--dir", str(exists), "--init"], cwd=str(tmp))
    assert "Traceback" not in (out + err), f"裸栈崩了:\n{err}"
    assert rc != 0, "应该非零退出"


# ── B6: 校验 ─────────────────────────────────────────────────
@case("B6a --check 报缺字段")
def _(tmp):
    led = tmp / "miss"
    write_entry(led, "a.md", id="m1", domain=None, evidence=None)
    rc, out, err = run(["--dir", str(led), "--check"], cwd=str(tmp))
    combined = out + err
    assert rc != 0, f"缺字段应该非零退出: {rc}"
    assert "domain" in combined or "evidence" in combined, f"没指出缺什么: {combined}"


@case("B6b --check 报 confidence 越界")
def _(tmp):
    led = tmp / "range"
    write_entry(led, "a.md", id="r1", confidence="1.7")
    rc, out, err = run(["--dir", str(led), "--check"], cwd=str(tmp))
    assert rc != 0, f"1.7 应该被拒: {rc}"
    assert "1.7" in (out + err) or "confidence" in (out + err), out + err


@case("B6c --check 报 id 重复")
def _(tmp):
    led = tmp / "dup"
    write_entry(led, "a.md", id="same")
    write_entry(led, "b.md", id="same")
    rc, out, err = run(["--dir", str(led), "--check"], cwd=str(tmp))
    assert rc != 0, f"重复 id 应该被拒: {rc}"
    assert "same" in (out + err), out + err


@case("B6e evidence 为负数被报出来")
def _(tmp):
    led = tmp / "negev"
    write_entry(led, "a.md", id="nv1", evidence="-2")
    rc, out, err = run(["--dir", str(led), "--check"], cwd=str(tmp))
    combined = out + err
    assert rc != 0, f"负 evidence 应该非零退出: {rc}"
    assert "-2" in combined or "负数" in combined, f"没报负数: {combined}"


@case("B6f first_seen 坏日期被报出来")
def _(tmp):
    led = tmp / "fs"
    write_entry(led, "a.md", id="fs1", first_seen="不是日期")
    rc, out, err = run(["--dir", str(led), "--check"], cwd=str(tmp))
    combined = out + err
    assert rc != 0, f"坏 first_seen 应该非零退出: {rc}"
    assert "first_seen" in combined, f"没指出 first_seen: {combined}"


# ── B7: 门槛单一权威 ─────────────────────────────────────────
@case("B6g 每个必填字段单独缺失都能被报出来")
def _(tmp):
    fields = ["trigger", "confidence", "domain", "evidence",
              "first_seen", "last_seen"]
    for fld in fields:
        led = tmp / f"m_{fld}"
        write_entry(led, "a.md", id=f"mf_{fld}", **{fld: None})
        rc, out, err = run(["--dir", str(led), "--check"], cwd=str(tmp))
        combined = out + err
        assert rc != 0, f"缺 {fld} 时没非零退出"
        assert fld in combined, f"缺 {fld} 时没点名说出来: {combined}"


@case("B6h safe_float / safe_int 直接拦住 NaN 与 Inf")
def _(tmp):
    driver = f'''
import importlib.util, json
spec = importlib.util.spec_from_file_location("i", {str(SCRIPT)!r})
m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
out = {{}}
for name, fn, val in [("f_nan", m.safe_float, "nan"), ("f_inf", m.safe_float, "inf"),
                      ("i_nan", m.safe_int, "nan"), ("i_inf", m.safe_int, "inf"),
                      ("f_ok", m.safe_float, "0.9"), ("i_ok", m.safe_int, "3.0")]:
    probs = []
    out[name] = [fn(val, "file", "id", probs), len(probs)]
print("JSON=" + json.dumps(out))
'''
    d = tmp / "drv.py"
    d.write_text(driver, encoding="utf-8")
    p = subprocess.run([sys.executable, str(d)], capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    line = [l for l in p.stdout.splitlines() if l.startswith("JSON=")]
    assert line, f"driver 没输出: {p.stdout} {p.stderr}"
    import json as _j
    r = _j.loads(line[0][5:])
    for k in ("f_nan", "f_inf", "i_nan", "i_inf"):
        assert r[k][0] == 0, f"{k} 该归零，实际 {r[k][0]}"
        assert r[k][1] == 1, f"{k} 该记录一条问题，实际 {r[k][1]} 条"
    assert r["f_ok"] == [0.9, 0], f"正常值被误伤: {r['f_ok']}"
    assert r["i_ok"] == [3, 0], f"3.0 该变 3: {r['i_ok']}"


@case("B7 门槛常量只有一处，--ready 与文档一致（0.9 / 3）")
def _(tmp):
    led = tmp / "thr"
    write_entry(led, "a.md", id="t0", confidence="0.89", evidence="9")
    write_entry(led, "b.md", id="t1", confidence="0.9", evidence="2")
    write_entry(led, "c.md", id="t2", confidence="0.9", evidence="3")
    rc, out, _ = run(["--dir", str(led), "--ready"], cwd=str(tmp))
    assert "t2" in out, "达标条目没进 --ready"
    assert "t0" not in out, "0.89 不该进"
    assert "t1" not in out, "evidence=2 不该进"


# ── B8: case.py 案例记录 ────────────────────────────────────
CASE_SCRIPT = HERE / "case.py"


def run_case(args, cwd, home):
    e = {k: v for k, v in os.environ.items()
         if k not in ("METHOD_HARVEST_HOME", "DSH_HOME")}
    e["DSH_HOME"] = str(home)
    p = subprocess.run([sys.executable, str(CASE_SCRIPT)] + args,
                       cwd=cwd, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", env=e)
    return p.returncode, p.stdout, p.stderr


@case("B8a 空目录 --stats 不报错并提示怎么开始")
def _(tmp):
    rc, out, err = run_case(["--stats"], tmp, tmp)
    assert rc == 0, f"exit={rc} {err}"
    assert "还没有案例" in out, f"没说清是空的: {out}"
    assert "--kind" in out, f"没给怎么开始: {out}"


@case("B8b 建一条能写进 <DSH_HOME>/cases/")
def _(tmp):
    rc, out, err = run_case(["--kind", "false-negative", "--slug", "t1"], tmp, tmp)
    assert rc == 0, f"exit={rc} {err}"
    files = list((tmp / "cases").glob("*.md"))
    assert len(files) == 1, f"没建出文件: {files}"
    text = files[0].read_text(encoding="utf-8")
    assert "kind: false-negative" in text, text[:200]
    assert "## 当时" in text and "## 回看" in text, "模板缺节"


@case("B8c 三种 kind 都能建")
def _(tmp):
    for k in ("false-negative", "false-positive", "surprise"):
        rc, out, err = run_case(["--kind", k, "--slug", k], tmp, tmp)
        assert rc == 0, f"{k} 建失败: {err}"
    files = list((tmp / "cases").glob("*.md"))
    assert len(files) == 3, f"应该 3 个文件，实际 {len(files)}"


@case("B8d 非法 kind 被 argparse 拒绝")
def _(tmp):
    rc, out, err = run_case(["--kind", "随便写的"], tmp, tmp)
    assert rc != 0, "非法 kind 应该被拒"


@case("B8e --stats 能区分填完没填完")
def _(tmp):
    run_case(["--kind", "false-negative", "--slug", "keepblank"], tmp, tmp)
    run_case(["--kind", "surprise", "--slug", "willfill"], tmp, tmp)
    # 只填 willfill 那一条（用精确匹配，别让 glob 撞上 keepblank 里的 willfill 子串）
    targets = [f for f in (tmp / "cases").glob("*willfill.md")
               if "keepblank" not in f.name]
    assert len(targets) == 1, f"该只找到 1 个目标，实际 {[f.name for f in targets]}"
    t = targets[0].read_text(encoding="utf-8")
    t = t.replace("（一句话：在干什么，什么场景）", "在装东西")
    t = t.replace("（发生了什么）", "没查账本")
    t = t.replace("（这说明什么，要不要改 description 或形态）", "改 description")
    targets[0].write_text(t, encoding="utf-8")
    rc, out, err = run_case(["--stats"], tmp, tmp)
    assert rc == 0, err
    assert "未填完 1" in out, f"没正确数出未填完数量: {out}"


@case("B8f 同一类攒到 3 条会提示看系统性缺口")
def _(tmp):
    for n in range(3):
        run_case(["--kind", "false-negative", "--slug", f"m{n}"], tmp, tmp)
    for f in (tmp / "cases").glob("*.md"):
        t = f.read_text(encoding="utf-8")
        t = t.replace("（一句话：在干什么，什么场景）", "x")
        t = t.replace("（发生了什么）", "y")
        t = t.replace("（这说明什么，要不要改 description 或形态）", "z")
        f.write_text(t, encoding="utf-8")
    rc, out, err = run_case(["--stats"], tmp, tmp)
    assert rc == 0, err
    assert "缺口" in out, f"3 条填完的没提示要看缺口: {out}"


def main():
    print(f"instinct.py 回归测试 · {len(CASES)} 个用例\n")
    for c in CASES:
        c()
    print(f"\n通过 {len(PASS)} / 失败 {len(FAIL)}")
    if FAIL:
        print("\n失败用例:")
        for f in FAIL:
            print(f"  - {f}")
    return 1 if FAIL else 0


if __name__ == "__main__":
    sys.exit(main())
