---
name: resilient-download
description: "下载大文件（安装包、模型、依赖），避免用户干等：单文件下载超过约 30 秒、Invoke-WebRequest 或浏览器下载报错、用户抱怨下载慢或卡住时改用多连接分段下载；后台任务长时间无输出时先读任务状态再决定等还是换方案。"
---

# 韧性下载与超时处理

目标：不让用户为下载干等。一次失败的等待比一次失败的下载更糟——用户看着 spinner 什么也做不了。

## 铁律

1. 单线程下载超过约 30 秒没完成，立刻切多连接。
2. 后台任务超时后先读状态，再决定等不等。
3. 下载器一报错就换方案并立刻告知用户，让用户以为还在进行更糟。
4. 大文件先探活再动手：一个 HEAD 请求换 30 秒的确定性。

## 决策流程

```
要下载 > 10 MB 的文件
  │
  ├─ 1. 探活（只花 1 秒）
  │     curl -sI -m 10 <url>  或  HEAD 请求
  │     ├─ 拿到 Content-Length → 知道总大小，进入 2
  │     └─ 超时/连不上 → 报错给用户，换镜像或问用户
  │
  ├─ 2. 先试单线程 curl（不是 Invoke-WebRequest）
  │     curl -L --retry 3 -C - --connect-timeout 30 -o out.zip <url>
  │     后台跑，30 秒没完成 → 进入 3
  │
  └─ 3. 多连接分段下载（见下）
```

## 为什么优先 curl 而不是 Invoke-WebRequest

`Invoke-WebRequest` 在 Windows 上实测比 curl 慢一个数量级，而且大文件容易静默卡死。同一 52 MB 文件实测：

| 方式 | 耗时 |
|---|---|
| `Invoke-WebRequest` | 超时失败（> 600s） |
| `curl -L --retry 5 -C -` 单线程 | **21 秒** |
| 8 段并行 | 通常 5–10 秒（受服务端限速约束） |

先试单线程 curl，通常已经够快；只有单线程也超过 30 秒才上分段。

## 多连接分段下载（Windows / PowerShell）

分段下载的关键是先用 HEAD 拿总大小；拿不到大小就不分。

### 完整可执行脚本

```powershell
$url = 'https://github.com/oven-sh/bun/releases/download/bun-v1.4.4/bun-windows-x64.zip'
$dir = Join-Path $PWD '_dl'    # 别写死到工作区外：沙箱 workspace-write 下会被拒写
$out = "$dir\download.bin"
$n   = 8   # 段数
New-Item -ItemType Directory -Force -Path $dir | Out-Null

# 1. 探活 + 拿总大小（curl 比 Invoke-WebRequest 的 HEAD 可靠）
$headers = & curl.exe -sIL --max-time 20 $url
$m = $headers | Select-String -Pattern '(?i)^content-length:\s*(\d+)' | Select-Object -Last 1
if (-not $m) { throw '拿不到 Content-Length，改用单线程：curl -L --retry 5 -C - -o out $url' }
$total = [int64]$m.Matches[0].Groups[1].Value
"总大小: {0:N1} MB，分 $n 段" -f ($total / 1MB)

# 2. 每段一个后台 curl（-r 指定字节区间）
$chunk = [math]::Ceiling($total / $n)
$jobs = @()
for ($i = 0; $i -lt $n; $i++) {
    $s = $i * $chunk
    $e = [Math]::Min($s + $chunk - 1, $total - 1)
    $jobs += Start-Process -FilePath 'curl.exe' `
        -ArgumentList "-sL --retry 3 --max-time 600 -r $s-$e -o `"$dir\part$i`" `"$url`"" `
        -WindowStyle Hidden -PassThru
}
$jobs | Wait-Process -Timeout 600

# 3. 合并（按 $n 动态拼参数，别写死 part0..part7）
$parts = 0..($n-1) | ForEach-Object { "`"$dir\part$_`"" }
cmd /c ("copy /b " + ($parts -join ' + ') + " `"$out`"") | Out-Null

# 4. 校验：先取长度再删，否则报错里取不到值
$actual = (Get-Item $out).Length
if ($actual -ne $total) {
    Remove-Item $out -Force
    throw "合并后大小不符（$actual vs $total），改用单线程重下"
}
Remove-Item "$dir\part*" -Force
"完成: {0:N1} MB" -f ((Get-Item $out).Length / 1MB)
```

### 注意事项

- 不支持 Range 的服务器会返回 200 全量，此时每个分片都是完整文件，合并必然出错 → 合并后必须校验大小。
- `-r` 的区间是闭区间，末段结束值要 `-1`。
- 分片之间用 `-C -` 是续传单文件的用法，分段时不用。

## 后台任务的三条纪律

1. 启动后台任务后先做别的事，立刻 `wait` 是浪费时间。
2. `job_output` 带 `timeout_ms` 等待时，超时了先看输出：有没有进度、有没有报错。
3. 看到 `[status: running]` 且无输出增长 → 判定卡死，kill 掉换方案。

## git push 被重置（curl 55 / RPC failed）

常见报错：

```
error: RPC failed; curl 55 Send failure: Connection was reset
send-pack: unexpected disconnect while reading sideband packet
fatal: the remote end hung up unexpectedly
```

先别急着调 `http.postBuffer`。实测：仓库只有 0.1 MB、36 个文件时也报这个错，根本不是体积问题，调 postBuffer 无效。

排查顺序：

```powershell
# 1. 确认仓库到底多大（多数时候不是体积问题）
git count-objects -vH

# 2. 确认远端 URL 协议
git remote -v

# 3. 用 gh 的 token 直推测试（绕过 credential helper）
$token = gh auth token
git push "https://x-access-token:$token@github.com/<owner>/<repo>.git" main
```

第 3 步成功 → 是 credential helper / 协议的问题，修复并固化：

```powershell
gh auth setup-git                      # 让 git 走 gh 的凭据
```

之后普通 `git push` 就能用。实测 `git config --global http.version HTTP/1.1` 在本机无效。

第 3 步也失败：

| 现象 | 原因 | 对策 |
|---|---|---|
| 大仓库（>100 MB）失败 | 传输过大 | `git config http.postBuffer 524288000` |
| 只在特定网络失败 | 代理/VPN 干扰 | 关代理重试，或改 SSH 协议 |
| 每次都在同一位置断开 | MTU / 中间设备 | 改 SSH：`git remote set-url origin git@github.com:...` |

实测记录：`git config http.postBuffer 524288000` + `http.version HTTP/1.1` 仍失败（21.9 秒后重置）；检查仓库体积 0.1 MB / 36 文件，排除体积因素；`gh auth token` 构造带认证 URL 直推成功；之后 `gh auth setup-git` 让 credential helper = manager，常规 push 可用。

## 选源优先级

速度差异极大，按以下顺序选：

1. **GitHub Releases** — 支持 Range，多连接能吃满带宽。大文件首选。
2. **项目官方镜像/CDN** — 通常也行，先探一次 Range 支不支持。
3. **PyPI / npm 官方源** — 在国内经常慢到不可用，装包别指望它。优先找 GitHub Release 的预编译产物，或用自带运行时的桌面版（解压即用，绕开包管理器）。
4. **国内 pip 镜像** — 不一定比官方快，有时直接 404。

## 校验必做

GitHub Release 的 API 里每个 asset 带 `digest: "sha256:..."`，下完直接比：

```powershell
$rel = Invoke-RestMethod "https://api.github.com/repos/<owner>/<repo>/releases/latest"
$rel.assets | Where-Object name -like '*Setup*' | Select-Object name, size, digest
(Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
```

## 探大小的正确姿势

`HEAD` 请求在 GitHub Release 上经常被拒（`RemoteDisconnected`）。改用带 Range 的 GET，从响应头读总长：

```python
req = urllib.request.Request(url, headers={"Range": "bytes=0-0", "User-Agent": "Mozilla/5.0"})
with urllib.request.urlopen(req, timeout=30) as r:
    total = int(r.headers["Content-Range"].split("/")[-1])
```

## Python 分段下载（比 PowerShell 版更稳）

PowerShell 的 `Start-Process curl` × N 也能用，但分段状态难追踪、续传要自己拼。Python 版更好：

```python
step = (total + PARTS - 1) // PARTS
def fetch(i):
    s, e = i*step, min(i*step + step - 1, total - 1)
    fn = OUT + f".part{i}"
    got = os.path.getsize(fn) if os.path.exists(fn) else 0
    for attempt in range(5):
        try:
            req = urllib.request.Request(URL, headers={"Range": f"bytes={s+got}-{e}"})
            with urllib.request.urlopen(req, timeout=60) as r, open(fn, "ab" if got else "wb") as f:
                while (b := r.read(262144)):
                    f.write(b)
            return True
        except Exception:
            got = os.path.getsize(fn) if os.path.exists(fn) else 0
            time.sleep(2 * (attempt + 1))
    return False
```

要点：每段独立 `.partN` 文件 → 断一段只重试那一段，不重下整体。

后台必须用托管 job（`run_in_background: true`）。`Start-Process -PassThru` 之后不管，进程会在沙箱下被静默杀掉，文件停在一半不再增长。

## 常见失败与对策

| 现象 | 原因 | 对策 |
|---|---|---|
| `Invoke-WebRequest` 超时无输出 | 该 cmdlet 在大文件上不可靠 | 换 `curl.exe` |
| `HEAD` 请求超时但 GET 能过 | 部分 CDN 不支持 HEAD | 直接用 `curl -r 0-1023` 探 |
| 下载完解压报错 | 断点续传导致文件不完整 | 先删残留再重下，不用 `-C -` |
| `Permission denied` 写临时文件 | 沙箱限制 | 写到工作区，或全程内存处理不落盘 |
| 后台任务输出长时间不增长 | 进程卡死或已静默失败 | `job_output` 看输出，无增长就 kill 换方案 |
| `git push` curl 55 重置 | 常见是凭据/协议问题，非体积 | 用 `gh auth token` 直推验证，见上一节 |

## 给用户的沟通规范

- 启动下载时说清预期："52 MB，预计 X 秒"。
- 超过预期还没好，主动报告："已耗时 X 秒，卡住了，我换多连接"。
- 报进度时带预计时间，"再等等"这种没有时间点的措辞用户无法规划。
