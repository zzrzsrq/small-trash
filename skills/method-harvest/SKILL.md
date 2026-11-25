---
name: method-harvest
description: "沉淀经验：同一个坑踩第二次，或用户说「又来了」「记住这个」「浪费 token」；任务收尾要沉淀经验；修改已有 skill。"
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

先记账，再写 skill。绝大多数经验停在 instinct 层就够；能被程序判断的规则写成脚本，文字留给判断。

instinct 的字段、置信度涨跌、提升门槛、合并与删除——**全部在 [references/instinct.md](references/instinct.md)，唯一权威**。闭环全图在 [references/loop.md](references/loop.md)。

## 账本维护（每次加载先做）

```
python "<skill目录>/scripts/instinct.py" --stale 30
python "<skill目录>/scripts/instinct.py" --ready
```

有输出就当场处理：还会发生的按要求处理（规则见 [instinct.md](references/instinct.md)），不会再发生的删掉，处理完再干正事。用户问「你学到什么了」时读 `--ready` 回答。

路径要加引号——目录名有空格时不加会失败。其余参数、账本位置优先级、领域筛选看 `instinct.py --help`。改完账本跑一次 `--check`。

## 形态跟着失败走

先给 baseline 定类，再决定写什么。形态错了，写得再全也没用。

| baseline 失败 | 写什么 |
|---|---|
| 知道规则，压力下违反 | 禁令 + 合理化对照表 + 红旗清单 |
| 会做，但产出形状不对 | 正向配方：正面说清目标输出 |
| 产出里漏了必需项 | 模板里加必填槽位 |
| 行为该依条件变 | 条件挂在可观察的谓词上 |

## 写之前先查重

**先看手上已有什么。** 当前会话里能看到全部在册 skill 的名字和描述——第一手清单，比搜索快。先找同领域的，找到了就读它、改它。

名单里没有再扫文件系统：

```
<DSH_HOME>/skills/
<DSH_HOME>/profiles/*/node_modules/*/bundled-skills/
<DSH 安装目录>/resources/app.asar.unpacked/**/skills/
```

不确定 `<DSH_HOME>` 指向哪时用 `Test-Path` 试。同领域 → 改那个；独立领域 → 才新建。每条 description 都常驻提示词，建得越多每轮越贵。

**改已有 skill**（查重后命中同领域、要动笔时）→ 读 [references/refine.md](references/refine.md)：内容放哪一层、备份与分工、写后反查与运行验证。

## 收尾判据

三问全过才动手写 skill：

1. **可复用**：下次能少走哪一步？答不出具体一步 → 只记 instinct
2. **没被覆盖**：上面的查重走过了？有同领域 → 改那个
3. **值得**：价值 > 写入成本

**载体**：反复触发、有步骤 → skill；单个动作 → instinct；能被程序判断 → 脚本；一次性事实、环境现状 → 工作区 .md。

## 动笔前必读

**REQUIRED SUB-SKILL**：写或改任何 skill 之前先读 `writing-for-agents`。它定怎么写：description 只写触发不写流程、信息层级与渐进披露、leading word、删 no-op、token 预算。写 skill 时它还会要求读 `SKILL-MECHANICS.md`——一起读。

本地速查 [references/writing.md](references/writing.md) 是 `writing-for-agents` 的补充，不是替代。
