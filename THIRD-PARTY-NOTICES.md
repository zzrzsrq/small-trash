# 第三方组件与署名

本项目**不声称拥有**下列作品。它们各自归原作者所有，按各自的许可证分发。
凡本项目直接依赖的，此处逐项列出作者、许可证与是否改动。

## 识别引擎

| 组件 | 作者 / 来源 | 许可证 | 本项目是否改动 |
|---|---|---|---|
| Sdcb.SimdPaddleOCR 2.0 | [sdcb](https://github.com/sdcb/SimdPaddleOCR) | Apache-2.0 | 否，原样引用 NuGet 包 |
| PP-OCRv6 模型权重（tiny / small / medium） | [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR)（百度） | Apache-2.0 | 否，随包内嵌分发 |
| Sdcb.SimdCodecs 2.0 | [sdcb](https://github.com/sdcb/SimdCodecs) | MIT | 否，原样引用 |

Apache-2.0 要求保留版权与许可声明。原项目 LICENSE 文本见各自仓库；
本项目不再重复分发其源码，仅以 NuGet 依赖与内嵌权重形式使用。

## 运行时与框架

| 组件 | 作者 | 许可证 |
|---|---|---|
| .NET 10 运行时 | .NET Foundation | MIT |
| Windows Forms（旧界面） | .NET Foundation | MIT |
| WebView2 运行时 | Microsoft | 随 Windows 分发 |
| wry | Tauri 项目组 | Apache-2.0 / MIT |
| tao | Tauri 项目组 | Apache-2.0 |
| rfd（文件对话框） | Poly 等 | MIT |
| serde / serde_json | dtolnay 等 | MIT / Apache-2.0 |

## 字体

界面只用系统字体栈（Segoe UI / 微软雅黑 / Noto Serif SC / 思源宋体 / 宋体）。
**仓库内不包含任何商业字体文件**；聊天与文档里出现的装饰字体不随本仓库分发。

## 本项目

| 内容 | 作者 | 许可证 |
|---|---|---|
| OcrKit 的 C# 代码、Rust 壳、界面、脚本 | zzrzsrq | MIT（见 LICENSE） |
| `skills/` 下的 skill 文本 | zzrzsrq | MIT |

## 如果你要再分发

请同时保留本文件与 LICENSE。若你替换了引擎或模型，署名要求以新组件为准。
