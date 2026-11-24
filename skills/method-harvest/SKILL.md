---
name: method-harvest
description: 沉淀经验：同一个坑踩第二次，或用户说「又来了」「记住这个」「浪费 token」；任务收尾要沉淀经验；修改已有 skill。
---

# Method Harvest

## 铁律

**先跑 baseline，看你不在场时 agent 怎么错，照着那个错写。**

写 skill = TDD 用在文档上：没有失败的 baseline，就不知道要教什么。

## 边界

**只管「该不该沉淀、沉到哪一层」。怎么写归 `writing-for-agents`。** 判据会互相污染：想着怎么写时就没法诚实判断该不该写。

## 三层沉淀

经验按成熟度分层，越往下越贵：

| 层 | 是什么 | 成本 |
|---|---|---|
| **instinct** | 一条动作 + 置信度 + 证据 | 几行 |
| **skill** | 多条成熟经验组成的流程 | 一个文件 |
| **检查** | 能被程序判断的规则 | 一个脚本 |

**先记账，再写 skill。** 绝大多数经验停在 instinct 层就够——过早写 skill 会把一次偶然当成规律。

**能被程序判断的规则写成脚本，文字留给判断。** 能自动检查的规则写成 instruction，等于让 agent 每轮重新判断一次。

instinct 的字段、置信度涨跌、提升门槛、合并与删除——**全部在 [references/instinct.md](references/instinct.md)，唯一权威**。

## 账本维护（每次加载先做）

先跑：

```
python "<skill目录>/scripts/instinct.py" --stale 30
```

有输出就当场处理：还会发生的留（按 [instinct.md](references/instinct.md) 的复发规则记上），不会再发生的删掉，处理完再干正事。维护时机就绑在加载上，不必另记。

**账本只进不出、分母涨分子不动，这套系统就是在自我安慰**——所以每次都要清理。

用户问「你学到什么了」时跑 `--ready`，读结果回答。

## 脚本怎么用

```
python "<skill目录>/scripts/instinct.py"            # 全部，按置信度排序
python "<skill目录>/scripts/instinct.py" --ready    # 够格提升成 skill 的
python "<skill目录>/scripts/instinct.py" --stale 30 # 30 天没出现的
python "<skill目录>/scripts/instinct.py" --check    # 体检：字段/范围/id/日期
python "<skill目录>/scripts/instinct.py" --init     # 首次：建账本 + 放样例
```

加 `--domain tooling` 只筛某个领域（可叠加在 `--ready` / `--stale` 上）。

**路径要加引号**——目录名有空格或中文时不加会失败。

账本位置按优先级自动找：`--dir` → `METHOD_HARVEST_HOME` → `DSH_HOME/instincts` → `<skill>/../instincts`。找不到会报错并给出 `--init` 命令，不静默失败。

改完账本跑一次 `--check`：字段缺、置信越界、id 重复、日期坏掉都会指出来。`scripts/test_instinct.py` 是脚本自己的回归测试。

## 形态跟着失败走

先给 baseline 定类，再决定写什么。**形态错了，写得再全也没用。**

| baseline 失败 | 写什么 |
|---|---|
| 知道规则，压力下违反 | 禁令 + 合理化对照表 + 红旗清单 |
| 会做，但产出形状不对 | 正向配方：正面说清目标输出，不列禁止项 |
| 产出里漏了必需项 | 模板里加必填槽位 |
| 行为该依条件变 | 条件挂在可观察的谓词上 |

**这张表只用来定类；具体落笔读 `writing-for-agents`**（正向提示、信息层级、leading word），这里不重写。

## 写之前先查重

**先看手上已有什么。** 当前会话里能看到全部在册 skill 的名字和描述——第一手清单，比搜索快。先找同领域的，找到了就读它、改它。

名单里没有再扫文件系统。三个根，实际路径以本机为准：

```
<DSH_HOME>/skills/
<DSH_HOME>/profiles/*/node_modules/*/bundled-skills/
<DSH 安装目录>/resources/app.asar.unpacked/**/skills/
```

不确定 `<DSH_HOME>` 指向哪时用 `Test-Path` 试，别猜——工作区和 DSH_HOME 常常不是同一个目录。

同领域 → 改那个；独立领域 → 才新建。每条 description 都常驻提示词，建得越多每轮越贵。

## 收尾判据

三问全过才动手写 skill：

1. **可复用**：下次能少走哪一步？答不出具体一步 → 只记 instinct
2. **没被覆盖**：上面的查重走过了？有同领域 → 改那个
3. **值得**：价值 > 写入成本？为一句「重启就好」建 skill → 不值

**载体**：反复触发、有步骤 → skill；单个动作 → instinct；能被程序判断 → 脚本；一次性事实、环境现状 → 工作区 .md。

## 案例记录：验证这套方法本身

核心假设——「先记账再写 skill 比直接写更好」——**没有数据支撑**，案例记录就是验它的数据。

**只记三类**（正常触发不记）：

| 类型 | 什么情况 | 值不值钱 |
|---|---|---|
| `false-negative` | 该触发没触发 | 最值钱，说明 description 漏分支 |
| `false-positive` | 触发了但没用 | 调 description 的直接依据 |
| `surprise` | 判断对了但结果意外 | 调形态或门槛的依据 |

```bash
python "<skill目录>/scripts/case.py" --kind false-negative --slug 短标题
python "<skill目录>/scripts/case.py" --stats   # 攒到 3 条同类，就该看系统性缺口了
```

存在 `<DSH_HOME>/cases/`。规则细节（怎么捕获 false-negative、什么时候整理）：[references/cases.md](references/cases.md)。

`false-negative` 没法自动记——skill 没触发就没有代码跑。**发现刚才该用却没用时，你说一句「刚才该触发的」，我补一条。** 这是唯一需要你参与的地方。

## 动笔前必读

**REQUIRED SUB-SKILL**：写或改任何 skill 之前先读 `writing-for-agents`。它定怎么写：description 只写触发不写流程、信息层级与渐进披露、leading word、删 no-op、token 预算。写 skill 时它还会要求读 `SKILL-MECHANICS.md`（frontmatter、model-invoked / user-invoked、router 怎么写）——一起读。

本地速查 [references/writing.md](references/writing.md) 是 `writing-for-agents` 的补充，不是替代；闭环全图在 [references/loop.md](references/loop.md)。
