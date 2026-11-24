---
name: sopdf
description: PDF 渲染成图、批量渲染页面；评估 SoPDF 是否比 PyMuPDF 快、能否用 GPU。
---

# SoPDF（渲染专精）

SoMark 官方配套的 PDF 处理库，`pip install sopdf`，Apache 2.0。底层 pypdfium2（Google PDFium，负责渲染/文本/搜索）+ pikepdf（libqpdf，负责结构写入/保存）+ opencv-python（图像编码）。

SoPDF 只负责把 PDF 页面渲染成图片。合并、拆分、旋转、去水印、文字层、加密走 `pdf` skill；扫描件 OCR 走 `pdf-ocr` skill。`pdf` skill 已把渲染委托到这里——两边说法不一致时，以末尾「分工」一节为准。

## 本机状态（实测）

- SoPDF 0.2.0 装在隔离沙箱 `<workspace>\.sandbox-somark-test\.venv`，系统 Python 3.12/3.15 未安装。用前先 `pip install sopdf`。
- 对比对象 PyMuPDF 2.1 在系统 Python 里已装（环境里的第三方库，不是插件、不是 DSH 官方组件）。
- 当前 pip 卡在版本自检，装包带 `--disable-pip-version-check` 并走国内镜像：
  `python -m pip install -i https://pypi.tuna.tsinghua.edu.cn/simple --no-cache-dir --disable-pip-version-check sopdf`

## 装完先自检

```python
import os
os.environ["OPENBLAS_NUM_THREADS"] = "1"; os.environ["OMP_NUM_THREADS"] = "1"
import sopdf
doc = sopdf.open("任意.pdf")
img = doc[0].render(dpi=150)
print(len(img))                 # 必须 > 0，0 字节说明渲染没起来
print(doc[0].get_text()[:30])
```

三条判据：`import` 不报错、`render()` 返回非空 bytes、`get_text()` 有输出。PNG 比 pymupdf 大是正常的（编码路径不同），比目视不比字节数。

## 性能实测（Windows 11 / 16 核 / Python 3.12 / 50 页 42KB / 预热 3 次 + 重复 9 次，中位数 ms）

ratio > 1 表示 SoPDF 更快。原始数据：`<workspace>\.sandbox-somark-test\bench4_out.txt`。

| 操作 | SoPDF | PyMuPDF | ratio | 用谁 |
|---|---|---|---|---|
| 渲染 1 页 @150dpi | 52.8 | 84.0 | **1.59×** | SoPDF |
| 渲染 1 页 @300dpi | 170.2 | 255.3 | **1.50×** | SoPDF |
| 渲染 50 页串行 | 2278.9 | 4256.1 | **1.87×** | SoPDF |
| 渲染 50 页并行 | 5450.4 | 4197.2 | 0.77× | PyMuPDF |
| 文本抽取 ×50 | 89.2 | 86.8 | 0.97× | 持平 |
| 带坐标文本块 ×50 | 254.9 | 85.0 | **0.33×** | PyMuPDF（快 3 倍）|
| 全文搜索 ×50 | 143.6 | 124.3 | 0.87× | PyMuPDF |
| 打开 / to_bytes | 2.6 / 11.0 | 2.0 / 9.4 | ~0.8× | 无差 |

P90 尾延迟同向：SoPDF 渲染更稳（单页 150dpi 的 P90 优势 2.67×）。

分工纪律：渲染成图（尤其串行批量、高 DPI）用 SoPDF；要坐标、要搜索、要并行渲染用 PyMuPDF。文本抽取两者持平，按现有习惯。

一致性已验证：文本词级 Jaccard 99.07%，搜索命中数两边都是 2250。渲染 PNG 字节数不同（SoPDF 1091275 / PyMuPDF 374223）是编码路径不同，不代表内容差异。

## Windows 坑（都会踩）

1. **`render_pages(parallel=True)` 会崩**：源码用 `ProcessPoolExecutor`，Windows spawn 模式下脚本缺 `if __name__ == '__main__':` 保护时子进程重导入脚本、无限递归、抛 `RuntimeError`。即便加了保护，本机 16 核上并行（5450ms）仍比 PyMuPDF 串行（4256ms）慢——多进程开销吃掉收益，用串行渲染。
2. **OpenBLAS 内存分配失败**：不设线程限制时打印成片 `OpenBLAS error: Memory allocation still failed after 10 retries`，opencv 线程争用所致。设 `OPENBLAS_NUM_THREADS=1` 与 `OMP_NUM_THREADS=1` 即消失。
3. **无 GPU**：依赖里没有 torch / cupy / nvidia-*，渲染 API 无 `device` 参数，纯 CPU。加速只能靠 CPU 多进程（见坑 1）。
4. **License 元数据不一致**：GitHub 仓库元数据写 `NOASSERTION`，README 与 PyPI 都写 Apache-2.0。商用前核一下仓库 LICENSE 文件原件。

## API 速查

```python
import os
os.environ["OPENBLAS_NUM_THREADS"] = "1"  # 必须在 import sopdf 之前
import sopdf

with sopdf.open("doc.pdf") as doc:
    img = doc[0].render(dpi=150)              # PNG bytes
    doc[0].render_to_file("page0.png", dpi=300)
    text = doc[0].get_text()                  # 纯文本（要坐标别用它，见分工表）
    hits = doc[0].search("invoice", match_case=False)
    doc.save("out.pdf", compress=True, garbage=True)
    raw = doc.to_bytes()
```

批量串行渲染（推荐用法）：
```python
with sopdf.open("doc.pdf") as doc:
    images = [p.render(dpi=150) for p in doc.pages]
```

## 分工

| 要做的事 | 用谁 | 备注 |
|---|---|---|
| 渲染成图（串行批量、高 DPI） | **本 skill** | 1.5–1.9× |
| 要坐标、要搜索、要并行渲染 | pymupdf | SoPDF 这三项都更慢 |
| 合并、拆分、旋转、去水印、文字层、加密 | `pdf` skill | 用 pymupdf |
| 结构级压缩 / 修复（qpdf 语义） | 本 skill `save(compress=True, garbage=True)` | 底层 pikepdf，等价 qpdf；日常先试 pymupdf `garbage=4` |
| 扫描件选 OCR 引擎 | `pdf-ocr` skill | 本 skill 不碰 OCR |

SoPDF 还带 `split` / `merge` / `rotation` / `metadata` / `outline` / `decrypt` / 自动修复损坏 PDF，这些是附带能力，默认仍走 `pdf` skill。
