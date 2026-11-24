---
name: windows-env-pitfalls
description: "Windows 环境变量改了不生效时用：注册表改了进程拿不到、重启仍不生效、PATH 加了目录命令找不到、closeToTray 假重启、安装器报 Error writing temporary file 或命令行装不上但双击能装。含 PATH 坏条目静默截断排查与程序硬编码 fallback 路径绕法。"
---

# Windows 环境变量与进程环境

DSH 在 Windows 上改环境变量后"改了不生效"是高频坑。这份文档给出根因和绕法。

## 根因：进程环境在启动时冻结

**进程启动那一刻拷贝一份环境块，之后注册表怎么改都不会同步。** 这是 Windows 的设计，不是 bug。

```
注册表 HKCU:\Environment  ← 你改的
        ↓ 只在新进程启动时读取
新进程的环境块            ← 但它从「父进程」继承，不是直接读注册表
        ↓
实际生效的值
```

**关键在第二层**：新进程继承的是父进程的环境，不是注册表。所以只要父进程（explorer.exe、VS Code、终端）还是旧的，再开多少个子进程都是旧的。

## 判断"是不是环境没刷新"

```powershell
# 注册表里是什么
(Get-ItemProperty 'HKCU:\Environment' -Name Path).Path

# 当前进程看到的是什么
$env:Path

# 逐项对比，找出差异
Compare-Object `
  ((Get-ItemProperty 'HKCU:\Environment' -Name Path).Path -split ';') `
  ($env:Path -split ';')
```

注册表有、进程没有 → 环境没刷新。
两边都没有 → 变量压根没写进去。

## 陷阱一：closeToTray 让"重启"变成假重启

DSH Desktop 默认 `closeToTray: true`，点 × 只是最小化到托盘，进程还活着，环境当然不变。

确认有没有真的重启：

```powershell
Get-CimInstance Win32_Process -Filter "Name='DSH NEXT.exe'" |
  Where-Object { $_.CommandLine -notmatch '--type=' } |
  Select-Object ProcessId, CreationDate
```

`CreationDate` 必须是刚才的时间。如果还是几小时前的，就是没真退。

强制退出：

```powershell
Get-Process -Name 'DSH NEXT' -ErrorAction SilentlyContinue |
  Stop-Process -Force
# 等 3-5 秒让单实例锁释放，再重新启动
```

## 陷阱二：PATH 里的坏条目导致静默截断

实测过：用户 PATH 里有一项 `C:\Program`（被截断的路径，原本应该是 `C:\Program Files\...`）。

**修正一个常见误解：坏条目不会让后面的条目失效。** 实测把 `C:\Program;F:\tools\gh\bin` 塞进 PATH，`gh` 照样解析到 `F:\tools\gh\bin\gh.exe`。真正的风险是三条：① 原本那条路径彻底丢了，程序找不到，报错却指向别处；② 带空格的路径被拆成两个条目后，两个都不对；③ PATH 越改越长，之后排查时误以为"已经配过"。

排查：

```powershell
# 只找「格式可疑」的条目。注意：每个判断写成单行，PowerShell 的多行 -or 会解析失败
$items = ((Get-ItemProperty 'HKCU:\Environment' -Name Path).Path -split ';') | Where-Object { $_ }
$bad = @()
foreach ($x in $items) {
    $isBad = (($x -notmatch '^[A-Za-z]:\\') -and ($x -notmatch '^\\\\') -and ($x -notmatch '^%')) -or ($x.Trim() -ne $x) -or ($x -match '^C:\\Program$')
    if ($isBad) { $bad += $x }
}
if ($bad.Count -gt 0) { $bad | ForEach-Object { "可疑: [$_]" } } else { "无格式畸形条目（共 $($items.Count) 项）" }
```

判断 PATH 条目看格式而非 `Test-Path`：实测本机 `Test-Path 'D:\Anki\python'` 返回 False，但那个目录真实存在（权限或重定向导致读不到）；用 `Test-Path` 会把 9 个正常条目误报成坏条目，反而误导排查。

真正要找的是格式畸形的条目：不以盘符/UNC/`%变量%` 开头、首尾带空格、或被截断成 `C:\Program` 这种。

修复时顺便去重（实测一次修掉 11 个重复项、30 项降到 19 项）。

## 陷阱三：广播 WM_SETTINGCHANGE 不一定有用

标准做法是改完环境变量广播 `WM_SETTINGCHANGE` 让 explorer 刷新。实测：不保证生效。

> ⚠️ `Add-Type` 只能在 PowerShell **FullLanguage** 模式下用。DSH 的 read-only 沙箱跑的是 ConstrainedLanguage，这段会直接报 "only core types" 错误，且不是权限问题、加权限也没用——那是模式限制。工作区可写（workspace-write）模式下才可用。

```powershell
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class EnvB {
    [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Auto)]
    public static extern IntPtr SendMessageTimeout(IntPtr h, uint m, UIntPtr w, string l, uint f, uint t, out UIntPtr r);
    public static void Go() {
        UIntPtr r;
        SendMessageTimeout(new IntPtr(0xffff), 0x001A, UIntPtr.Zero, "Environment", 0x0002, 5000, out r);
    }
}
'@
[EnvB]::Go()
```

广播后新开的 `cmd` 仍可能拿不到新值。

## 最可靠的绕法：找程序自己的 fallback 路径

优先找程序自己的硬编码默认位置：大多数 CLI 工具除了读环境变量，还会查几个硬编码位置。读源码找那个位置，把文件放过去。

实例（思路示例，来自第三方包 dsh-our-free-model，本机未安装、不可直接复现）：它找 Bun 的顺序是

```js
1. process.env.OUR_FREE_MODEL_BUN        ← 环境变量，读不到
2. BUN_INSTALL/bin/bun.exe               ← 环境变量，读不到
3. join(homedir(), '.bun', 'bin', 'bun.exe')   ← ★ 硬编码，不依赖环境！
4. PATH 里每一项 / bun.exe
5. APPDATA/npm/node_modules/bun/bin/bun.exe
```

把 `bun.exe` 放到 `C:\Users\<你>\.bun\bin\bun.exe`，第 3 条直接命中，不需要环境变量、不需要重启。

排查方法：

```powershell
# 在该工具的实现文件里搜环境变量名
Get-ChildItem -Recurse -File <包目录> -Include *.js,*.ts,*.py |
  Select-String -Pattern 'findBun|_BUN|\.bun' -List
```

## 绕法二：放到一个已经在 PATH 里的目录

当前进程 PATH 里已有的目录（比如 `F:\tools\gh\bin`）一定能被找到。把东西拷进去虽然脏，但立刻生效。

## 绕法三：改配置文件而非环境变量

插件支持在 config 里写绝对路径时，直接写配置，绕过环境变量。查 `<dsh-home>\profiles\desktop\cordis.patch.yml` 里该 entry 的 `config` 字段（不是仓库根目录下的同名文件）。

## 绕法四：接受现实，全程用绝对路径

反复重启都拿不到新环境时，用绝对路径最省心：

PATH 里的目录找不到 → 直接把完整路径写进命令：

```powershell
& '<scoop>\shims\mo.cmd' status --json
& 'F:\tools\gh\bin\gh.exe' auth status
& 'C:\Users\<你>\.bun\bin\bun.exe' --version
```

代价：不能直接敲 `mo`，必须写全路径。收益：零重启、零配置、立刻能用。

把常用工具的绝对路径记进工作区的 `AGENTS.md` 或本 skill 末尾（`docs/dsh-environment.md` 并不存在）。

## 先止血：确认母进程能不能自己刷新

反复重启还是旧环境，通常是因为启动 DSH 的那个进程本身就是旧的。三个来源排查：

| 启动方式 | 是否刷新 | 备注 |
|---|---|---|
| 桌面/开始菜单快捷方式 | ❌ explorer 缓存环境 | 最常见 |
| 任务栏固定图标 | ❌ 同上 | |
| 从旧终端敲命令 | ❌ 继承该终端 | |
| **注销后重新登录** | ✅ | 彻底但麻烦 |
| **重启电脑** | ✅ | 最彻底 |

不想注销就用绕法二（拷到已有目录）或绕法四（绝对路径）。

## 特例：第三方 Electron 应用写死 `spawn("python")`

**症状**：某软件功能报「失败/不可用」，但手工用绝对路径跑它的脚本完全正常。

**实例（LogiNote 学习软件，2026-11 实测）**：导入 PDF 显示「文本字符 0 / 索引分块 0 / 解析或索引失败：PDF 原图渲染不可用」。

根因链：

```
主进程 dist-electron/main.cjs 里:
    spawn("python", [pdfExtractScript, filePath])
    ↑ 写死字符串 "python"，从 PATH 找

用户机器 PATH 里从来没有 python   →   spawn 直接 ENOENT
                                    →   子进程没启动
                                    →   文本框 0 / 分块 0
                                    →   报一个看起来像"PDF 坏了"的错误
```

**关键点：报错信息完全不指向真因。** 它说的是「PDF 原图渲染不可用」，实际是「Python 不存在」。

### 排查步骤

1. **手工跑它的脚本**（用绝对路径的 python），看是否正常：

   ```powershell
   & 'C:\...\python.exe' '软件目录\resources\python\xxx.py' '测试文件.pdf'
   # 正常返回 JSON → 说明业务逻辑没问题，问题在解释器查找
   ```

2. **模拟它真实的调用方式**（裸命令名，不是绝对路径）：

   ```powershell
   & node -e "const{spawn}=require('child_process');const p=spawn('python',['-c','print(1)'],{stdio:'inherit'});p.on('error',e=>console.log('ERR',e.message))"
   # 输出 spawn python ENOENT → 确认是 PATH 问题
   ```

3. **读它的启动代码找解释器名**（asar 可解）：

   ```python
   # asar 头： 8 字节后是 header 长度，16 字节后是 JSON 索引
   import struct, json
   fh = open(asar, 'rb'); fh.seek(8)
   _, jl, _ = struct.unpack('<III', fh.read(12))
   fh.seek(16); index = json.loads(fh.read(jl).decode('utf-8'))
   # 然后按 offset 取出 .js/.cjs 文件搜 spawn("python" / python.exe
   ```

### 解法优先级

| 方案 | 做法 | 评价 |
|---|---|---|
| **A. 装独立 Python 并设 PATH** | 官方安装包 + `PrependPath=1` | **推荐**，长期稳定 |
| B. 用别的运行时 Python 加 PATH | 指到已有的 venv/conda | 那个运行时升级就断 |
| C. 改软件源码 | 把 `spawn("python")` 改绝对路径 | 需重打包，**软件更新即失效** |

装完必须让目标软件真正重启（不是最小化到托盘）——它继承了旧环境块，见上文陷阱一。

验证方法：重启后模拟它的调用 `spawn('python')` 返回 exit=0 才算成功。

依赖别忘装：LogiNote 需要 `pymupdf` + `pdfplumber`，只装 Python 不装包仍然失败，但报错会变成另一种。

## 快速决策树

```
环境变量改了不生效
  ├─ 进程是刚启动的吗？
  │    └─ 否 → 真正杀掉进程重开（注意 closeToTray）
  │
  ├─ 注册表和 process 对比一致吗？
  │    └─ 不一致 → 父进程没刷新 → 走绕法
  │
  ├─ PATH 里有坏条目吗？
  │    └─ 有 → 清掉 + 去重
  │
  └─ 还是不行 → 绕法二（拷到已有目录）/ 绕法四（绝对路径）
```

最省时间的判断：已经重启过 2 次还是不行，立刻放弃环境变量，改用绝对路径。

## 实测记录

同一个 DSH 进程，PATH 改了三次、重启四次，进程看到的仍是初始快照：

```
注册表第 1 项 : <scoop>\shims
进程第 1 项   : C:\Users\...\PowerShell\7     ← 完全不同的快照
```

而 `F:\tools\gh\bin`（第一次改的）能生效，`<scoop>\shims`（第三次改的）不能。说明快照停在某个时间点，之后的不再吸收。

| 尝试 | 结果 |
|---|---|
| 写注册表 + 重启 DSH | PATH 生效，但只到"改 PATH 那一刻"的快照 |
| 再改 PATH 加新目录 + 重启 | 新目录没进进程 |
| 广播 WM_SETTINGCHANGE | 新 cmd 仍拿不到 |
| 用 `UseShellExecute=true` 启动测试进程 | 变量没展开（证明父进程环境是旧的） |
| 放到 `~/.bun/bin/`（硬编码 fallback） | **立刻生效** |

结论：改完立刻用绝对路径验证，能跑就别管 PATH；重启不保证拿到新环境。

## 安装器报 "Error writing temporary file"

先换启动上下文，再查 TEMP 和权限：让用户从资源管理器双击同一个 .exe 跑一次。

装上了就停。「我这边起不来、用户双击能装」本身就是结论，原因可以不确定。

用户双击也失败，才回头查 TEMP 指向与写权限、安装包路径（网络盘 / 中文 / 只读）、杀软拦截。

差别大概在谁起的这个进程——子进程拿到的 token、integrity level、TEMP 解析可能与交互式桌面不同。这是假说，不必先证，先要结果。
