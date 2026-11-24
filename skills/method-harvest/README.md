# method-harvest

> 不是每条经验都该写成 skill。先记账，攒够了再升级。

给 AI agent 用的经验沉淀框架。核心主张：**跳过 baseline 就写 skill，写出来的一定是错的**——你不知道要治什么病。

## 为什么需要它

大多数 skill 库只回答"怎么写 skill"。但真问题在前一步：**这条经验值不值得变成 skill？**

一次偶然踩坑写成的 skill，会让 agent 在无关场景里也照着做。这套框架把沉淀分成三层，让经验先经受验证：

```
干活 → 出了岔子/找到好路
         ↓  写一条，置信 0.5
      instinct 账本        ←── 复发：+0.1
         ↓ 出现 ≥0.9 且 ≥3 次
      并入 skill
         ↓ 能被程序判断的
      写成脚本
```

绝大多数经验永远停在 instinct 层。**这是对的。**

## 三层

| 层 | 是什么 | 成本 |
|---|---|---|
| **instinct** | 一条动作 + 置信度 + 证据数 | 几行 |
| **skill** | 多条成熟经验组成的流程 | 一个文件 |
| **检查** | 能被程序判断的规则 | 一个脚本 |

第三层最容易被忘：一条能自动检查的规则，写成 instruction 就是让 agent 每轮重新判断一次。

## 安装

把整个目录放进你的 skills 目录（Claude Code / DSH / 任何支持 Agent-Skills 格式的 harness）：

```bash
# 例：Claude Code
cp -r method-harvest ~/.claude/skills/

# 例：DSH
cp -r method-harvest "$DSH_HOME/skills/"
```

需要一个 Python 3.10+（只用标准库，无需 pip install）。

然后初始化账本：

```bash
python "<skill目录>/scripts/instinct.py" --init
```

## 用法

```bash
python "<skill目录>/scripts/instinct.py"            # 全部，按置信度排序
python "<skill目录>/scripts/instinct.py" --ready    # 够格提升成 skill 的
python "<skill目录>/scripts/instinct.py" --stale 30 # 30 天没出现的
python "<skill目录>/scripts/instinct.py" --check    # 体检：字段/范围/id/日期
python "<skill目录>/scripts/instinct.py" --init     # 建账本 + 放样例
```

**路径要加引号**——目录名有空格或中文时不加会失败。

账本位置按优先级自动找：

1. `--dir` 参数
2. `METHOD_HARVEST_HOME` 环境变量（直接就是账本目录）
3. `DSH_HOME` 环境变量（取 `$DSH_HOME/instincts`）
4. `<skill目录>/../instincts`

找不到会报错并给出 `--init` 命令，不静默失败。

## 一条 instinct 长什么样

```yaml
---
id: desktop-first-for-gui
trigger: 装 GUI 安装包、需要点界面、命令行搞不定交互时
confidence: 0.7
domain: tooling
evidence: 2
first_seen: 2026-01-01
last_seen: 2026-01-01
---
一次失败就换执行上下文，同一条命令不跑第三次。
```

**七个字段全要写**。写完跑 `--check`，它会告诉你缺什么。

## 置信度怎么动

| 分数 | 含义 | 行为 |
|---|---|---|
| 0.3 | 试过，不确定 | 明确相关时提一句 |
| 0.5 | 有点把握 | 相关时应用 |
| 0.7 | 稳了 | 主动应用 |
| 0.9 | 铁律 | 可提升进 skill |

- **复发** +0.1
- **用户说「又来了」** → 这条对，只是没生效 → 跳到 0.9
- **用户说「不对」** → 这条本身错 → 归 0，删掉

前两种方向相反，区别在否定的是**执行**还是**内容**。

## 文件结构

```
method-harvest/
├── SKILL.md                    # 主文件：该不该沉淀、沉到哪层
├── references/
│   ├── instinct.md             # 置信度规则的唯一权威
│   ├── loop.md                 # 闭环：时机与入口
│   ├── cases.md                # 案例记录：验证这套方法本身
│   └── writing.md              # 写法速查
├── scripts/
│   ├── instinct.py             # 账本工具
│   ├── case.py                 # 案例记录工具
│   ├── test_instinct.py        # 36 个回归测试
│   └── mutate.py               # 变异测试：验证测试真的能变红
├── LICENSE
└── README.md
```

## 案例记录：验证方法本身

这套框架的核心假设——「先记账再写 skill 比直接写更好」——没有数据支撑。**案例记录就是验它的数据。**

只记三类有判断价值的（正常触发不记）：

| 类型 | 什么情况 |
|---|---|
| `false-negative` | 该触发没触发——最值钱，说明 description 漏了分支 |
| `false-positive` | 触发了但没用 |
| `surprise` | 判断对了但结果意外 |

```bash
python scripts/case.py --kind false-negative --slug desc-miss
python scripts/case.py --stats      # 同类攒到 3 条，就该看系统性缺口了
```

存在 `$DSH_HOME/cases/`。攒到二三十个，就能回答：`description` 准不准、分层成不成立、门槛合不合理。

**没有这份数据，一切改进都是拍脑袋。**

## 测试

```bash
python scripts/test_instinct.py   # 36 个用例
python scripts/mutate.py          # 变异测试：把修复挨个去掉，看测试抓不抓得住
```

**为什么要跑变异测试**：普通的全绿只能证明测试跑通了。变异测试把关键修复逐个删掉，如果删了之后测试还是全绿，说明那段代码根本没人守——这种「假绿」比没有测试更危险。

当前状态：

```
基线：36 通过 / 0 失败
变异测试：抓住 18/18（instinct.py 15 + case.py 3），存活 0
```

自己复跑一遍来确认，别信这个数字。

覆盖的场景：路径解析（`--dir` / 环境变量 / 目录推测）、编码降级（UTF-8 / GB18030 / 认不出）、YAML 引号与注释、日期解析、NaN/Inf/有限大值、必填字段、id 唯一性、门槛边界。

## 与其他 skill 的关系

这套只管**该不该写**。**怎么写**推荐参考：

- [obra/superpowers](https://github.com/obra/superpowers) 的 `writing-skills`（324k★）——把 TDD 用在文档上
- Anthropic 的 skill 编写最佳实践

如果你的 harness 里有 `writing-for-agents` 这个 skill，写 skill 前先读它。

## 已知限制

- **纯中文**。英文版还没做。
- **`--domain` 实现了但文档里没细讲**：`--domain tooling` 只筛某个领域。
- **YAML 不是真解析**。用的是轻量正则 + 手工解析，只支持本框架用到的子集：单行键值、`#` 注释、单双引号。不支持多行值、锚点、嵌套。
- **`#` 有歧义**。无引号时，`#` 前有空格且后接空格或行尾才算注释。所以：
  - `见 #1 的坑`、`C# 项目`、`见 #a 的坑` —— 原样保留（# 后不是空格）
  - `0.9 # 注释`、`打 tag 时 # 后面跟版本号` —— 都被当成注释切掉

  **值里一定要保留「 # 」，请加引号**（`trigger: '打 tag 时 # 后面跟版本号'`）。这是 YAML 本身的歧义，不是本工具的 bug。
- **GBK strict 控制台下的 emoji 输出路径没有测试覆盖**——`argparse` 会重绑 `sys.stdout`，测试环境模拟不出来。代码里有 `reconfigure` 兜底，但没验证过。
- **`--check` 对同一个坏日期可能报两遍**（解析层和校验层各一次），措辞不同。

## 关于「别信测试全绿」

这个项目自己吃过一次亏：某一版 18 个测试全绿，但其中一条断言是假的——它检查 `'#' in 输出`，而输出里本来就有置信度条 `'#######'`，于是无论代码对不对都通过。

所以现在：
- `scripts/mutate.py` 会主动把修复删掉，验证测试真的能变红
- README 里给出的数字都可以自己复跑核对
- 这个项目的核心主张是「不要靠声称判断」，它自己也得守这条

## License

MIT
