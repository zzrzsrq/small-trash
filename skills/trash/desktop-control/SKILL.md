---
name: desktop-control
description: "操作 Windows 桌面时用：需要看见界面才能完成的操作（点击按钮、填表单、拖文件、装 GUI 程序、启动并验证窗口、读浏览器标签页），或要判断一个操作该走 GUI 还是 shell。"
---

# 桌面操作：先观察，再动手

`mcp__desk__*` 是 29 个工具的统一入口，合并 UI Automation（元素语义）与浏览器 DOM / 区域截图。

## 铁律

1. **先观察再操作。** 没看过界面就点，等于闭眼按键。
2. **优先元素语义。** `elementId` 稳定；坐标会因窗口移动、DPI 缩放、切屏失效。
3. **需要看见界面的操作走 `mcp__desk__`。** 判断标准：这个操作需要看见界面吗？
4. **同一操作失败 2 次即换策略或问人。**
5. **启动 GUI 应用后验证窗口，不是进程。** `launch_app` 与 shell 的 `Start-Process` 都能启动，不分先后：要顺带操作界面就用 `launch_app`（启动后可直接 `list_windows`/`snapshot`），只要起个后台程序用 `Start-Process` 也行。Electron 类应用（VS Code、Discord、各类笔记软件）有时进程在、窗口没出来，退出码 0、stderr 空，看起来像启动失败；先用 `list_windows`/`wait_for` 看窗口到底出没出，窗口没出再换另一种启动方式。

## 选对工具（最常踩的坑）

### 观察

| 场景 | 用什么 | 为什么 |
|---|---|---|
| 要看整窗布局 + 控件 | `snapshot` | 截图 + UIA 树一起给，一次拿全 |
| 只要控件不要图 | `read_tree` | 省 token |
| 只要视觉不要结构 | `screenshot` | 最轻；支持 `region` 只截一块 |
| 找某个按钮/输入框 | `find` | 按 name/automationId/class 找；UIA 树稀疏时自动降级 OCR |
| 纯图/游戏/无 UI 树 | `ocr` | Windows OCR，返回字级坐标 |
| 单个元素的属性 | `element_info` | 给 elementId 或坐标都行 |

### 操作

| 动作 | 用什么 | 注意 |
|---|---|---|
| 点按钮 | `invoke` 先试，不行再 `click` | `invoke` 走 UIA 模式，不抢焦点 |
| 点任意位置 | `click`（给坐标） | 窗口动过会打偏，它会自校正 |
| 填输入框 | `set_value` 先试 | 走 ValuePattern，比模拟键盘可靠 |
| 打字（含中文） | `type_text` | CJK 走 clipboard 且用后恢复原剪贴板 |
| 组合键 | `keypress` | 如 `["ctrl","a"]`；Windows 键被屏蔽 |
| 拖拽文件 | `drag` | 传屏幕坐标路径点序列 |
| 等界面出现 | `wait_for` | 等的是动作之后才出现的文字，不是刚打进去的 |
| 简单延时 | `wait` | 没有判断条件时才用 |

### 窗口与系统

`list_windows` 枚举 / `activate_window` 前置 / `close_window` 发 WM_CLOSE（不是杀进程）/ `move_window` 移动不抢焦点 / `launch_app` 启动或切窗 / `clipboard` 读写剪贴板 / `notify` toast 通知 / `display_info` 多屏与 DPI / `control_status` 查控制权归属。

## 坐标缩放（最容易点错的地方）

`screenshot` 返回的图常被缩放，必须乘 `Screenshot Coordinate Scale` 再传给 `click`。

```
截图里的 (200, 150) × scale 1.777778 → 实际屏幕坐标 (356, 267)
```

`snapshot` 返回的 `boundingBox` 已经是屏幕坐标，不用换算。

## 有浏览器标签页时

`list_windows` 返回的 `browserTab` 传给 `snapshot` / `read_webpage`，可以后台操作网页不动前台：

- `read_webpage` + `use_dom=true` → 读当前标签页 DOM，绕开反爬、复用登录态
- 元素 id 形如 `t1a2b:57`，可直接用于 `click` / `set_value`

## 交叉引用

- 被移除的内置 `PowerShell` / `Registry` / `FileSystem` / `Process` 工具用原生 `pwsh` / `read` / `write` 替代，它们受沙箱管辖。
- 报错命中 `Error writing temporary file` / `NSIS` 时先读 windows-env-pitfalls——那是进程环境问题，不是 GUI 问题。
