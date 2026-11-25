# 更新日志

## 2026-11-26 · method-harvest 精修

把 `skills/method-harvest/` 从「记账工具」收口成完整的沉淀方法：该不该沉淀、沉到哪层、怎么写、怎么改已有 skill，各有落点。

**删掉的死代码**：`scripts/case.py`、`references/cases.md` 及其 6 个测试 / 3 个变异。这套「记录触发案例以验证方法」的机制实际只留下积压未处理的条目，已停用并整文件删除；`--check`、`--ready`、`--stale` 与 30 个回归测试、15 个变异全部保留。

**正文（SKILL.md）**：130 → 83 行。置信度、门槛、字段、删除判据等规则收敛到 `references/instinct.md` 单一权威；脚本参数交给 `instinct.py --help`；新增一条带触发条件的指针——「查重后命中同领域、要动笔改已有 skill 时 → 读 `references/refine.md`」。

**新增 `references/refine.md`**：固化精修已有 skill 的动作与「内容放哪一层」的三分支——每轮都要用的步骤留正文；命中某分支才查的判据 / 例子 / 数据进兄弟文件、正文指针写明触发条件；「当时怎么发现的」直接删除。兄弟文件必须真的会在该分支被查阅，否则只是换个文件夹的 sediment。

**其余文档**：`instinct.md` 置信度涨跌改为按可观察信号分四支，修正了 BOM 处理与脚本不符的描述；`writing.md` 只留本地增量，写法权威交还 `writing-for-agents`；`loop.md` 只保留闭环图；`README.md` 压成安装与维护指针。

验证：`--check` 通过、回归 30/30、变异 15/15 全抓住；frontmatter、引用指针、代码围栏、脚本编译均通过。
