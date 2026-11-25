# method-harvest

> 先跑 baseline，把经验记成 instinct，验证够了再写 skill；能被程序判断的写成脚本。

**用法、判据、置信度规则全部以 [SKILL.md](SKILL.md) 为准。** 本文件只讲安装和维护；两边说法冲突时，以 SKILL.md 和它指向的 `references/` 为准。

## 安装

把整个目录放进你的 skills 目录（Claude Code / DSH / 任何支持 Agent-Skills 格式的 harness）：

```bash
# 例：Claude Code
cp -r method-harvest ~/.claude/skills/

# 例：DSH
cp -r method-harvest "$DSH_HOME/skills/"
```

需要 Python 3.10+（只用标准库，无需 pip install）。装好后初始化账本：

```bash
python "<skill目录>/scripts/instinct.py" --init
```

## 文件结构

```
method-harvest/
├── SKILL.md                    # 主文件：该不该沉淀、沉到哪层（唯一入口）
├── references/
│   ├── instinct.md             # 置信度规则的唯一权威
│   ├── loop.md                 # 闭环：时机与入口
│   └── writing.md              # 写法速查（writing-for-agents 的补充）
├── scripts/
│   ├── instinct.py             # 账本工具
│   ├── test_instinct.py        # 回归测试
│   └── mutate.py               # 变异测试：验证测试真的能变红
├── LICENSE
└── README.md                   # 本文件：安装与维护
```

日常用法读 SKILL.md 的「账本维护」一节；其余参数、账本位置、领域筛选看 `instinct.py --help`。

## 测试与维护（给改代码的人）

改过 `scripts/` 里的东西后，跑这两个：

```bash
python scripts/test_instinct.py   # 回归测试
python scripts/mutate.py          # 变异测试：逐个删掉关键修复，看测试抓不抓得住
```

变异测试是必要的：普通全绿只证明测试跑通了；删掉修复后仍全绿，说明那段代码没人守。

维护时的两个提醒：

- **数字以本次运行为准**：测试个数、变异捕获数随代码变，读你自己刚才那次的输出。
- **改代码同步改测试**：`test_instinct.py` 每个用例对应一个已知 bug，`mutate.py` 用正则定位关键行。改了这些行就更新对应正则，让变异保持命中。覆盖面的细节读代码。

## 与其他 skill 的关系

本 skill 只管「该不该写、写到哪层」。**怎么写**归 `writing-for-agents`——写或改任何 skill 前先读它。

## License

MIT
