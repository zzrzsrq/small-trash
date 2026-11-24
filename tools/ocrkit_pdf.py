#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
OcrKit PDF 处理器 —— 用 SimdPaddleOCR 做 PDF OCR。

流程：
  1. pymupdf 抽文本层，够用就直接返回（最快路径）
  2. 否则逐页渲染成图 → 调 OcrKit.exe --cli --format json 识别
  3. 可选：把结果写成不可见文字层，产出可搜索 PDF

用法：
  python ocrkit_pdf.py 输入.pdf                    # 只输出文本
  python ocrkit_pdf.py 输入.pdf --searchable 输出.pdf  # 同时产出双层 PDF
  python ocrkit_pdf.py 输入.pdf --dpi 300 --tier small
"""

import argparse
import json
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path

import pymupdf

OCRKIT = Path(r"<ocrkit-dir>\bin\OcrKit.exe")


def has_text_layer(doc, sample=3):
    """抽前几页看文本层。字符够多且不是乱码就认为有。"""
    n = min(sample, doc.page_count)
    if n == 0:
        return False
    counts = []
    for i in range(n):
        t = doc[i].get_text().strip()
        counts.append(len(t))
    avg = sum(counts) / len(counts)
    return avg >= 20


def render_pages(doc, out_dir, dpi=200):
    """逐页渲染成 PNG，返回 [(页码, 图片路径, 页宽pt, 页高pt)]"""
    zoom = dpi / 72.0
    mat = pymupdf.Matrix(zoom, zoom)
    pages = []
    for i, page in enumerate(doc):
        pix = page.get_pixmap(matrix=mat, alpha=False)
        p = out_dir / f"page_{i + 1:04d}.png"
        pix.save(str(p))
        pages.append((i, p, page.rect.width, page.rect.height))
    return pages


def run_ocrkit(image_dir, pages, tier="medium", device="auto"):
    """调 OcrKit 批量识别，返回 {页码: 结果dict}"""
    proc = subprocess.run(
        [str(OCRKIT), "--cli", str(image_dir),
         "--format", "json", "--tier", tier, "--device", device],
        capture_output=True, text=True, encoding="utf-8", errors="replace"
    )
    if proc.returncode not in (0, 1):
        raise RuntimeError(f"OcrKit 失败 (exit {proc.returncode}):\n{proc.stderr}")

    results = {}
    for page_no, img_path, _, _ in pages:
        jpath = img_path.with_suffix(".json")
        if jpath.exists():
            with open(jpath, encoding="utf-8") as f:
                results[page_no] = json.load(f)
    return results, proc.stdout


def write_text_layer(page, ocr, img_w, img_h):
    """把 OCR 结果写成不可见文字盖回原页（render_mode=3）。"""
    sx = page.rect.width / img_w
    sy = page.rect.height / img_h
    for line in ocr.get("lines", []):
        text = (line.get("text") or "").strip()
        if not text:
            continue
        box = line.get("box") or []
        if len(box) < 8:
            continue
        xs = [box[0], box[2], box[4], box[6]]
        ys = [box[1], box[3], box[5], box[7]]
        x0, y0, x1, y1 = min(xs), min(ys), max(xs), max(ys)
        h = max(y1 - y0, 1)
        size = max(h * sy * 0.92, 1.5)
        try:
            page.insert_text(
                (x0 * sx, (y1 * sy) - size * 0.18),
                text,
                fontname="china-s",
                fontsize=size,
                render_mode=3,
                color=(0, 0, 0),
            )
        except Exception as e:
            print(f"    警告：某行写入失败 {e}", file=sys.stderr)


def main():
    ap = argparse.ArgumentParser(description="OcrKit PDF OCR")
    ap.add_argument("input", help="输入 PDF")
    ap.add_argument("--searchable", metavar="OUTPUT", help="产出可搜索 PDF 的路径")
    ap.add_argument("--out-txt", metavar="FILE", help="纯文本输出路径")
    ap.add_argument("--dpi", type=int, default=200, help="渲染 DPI（默认 200）")
    ap.add_argument("--tier", default="medium", choices=["medium", "small", "tiny"])
    ap.add_argument("--device", default="auto", choices=["auto", "gpu", "cpu"])
    ap.add_argument("--force", action="store_true", help="有文本层也强制 OCR")
    args = ap.parse_args()

    src = Path(args.input)
    if not src.exists():
        print(f"找不到文件: {src}", file=sys.stderr)
        return 2

    doc = pymupdf.open(src)
    print(f"打开 {src.name} · {doc.page_count} 页")

    # ── 路径一：有文本层直接抽 ──
    if not args.force and has_text_layer(doc):
        print("检测到文本层，直接抽取（不调 OCR）")
        parts = [doc[i].get_text() for i in range(doc.page_count)]
        text = "\n".join(parts)
        if args.out_txt:
            Path(args.out_txt).write_text(text, encoding="utf-8")
            print(f"已写出 {args.out_txt} · {len(text)} 字")
        doc.close()
        return 0

    # ── 路径二：OCR ──
    print(f"无文本层，开始 OCR（{args.tier} / {args.device} / {args.dpi} DPI）")
    t0 = time.time()

    with tempfile.TemporaryDirectory(prefix="ocrkit_pdf_") as td:
        tmp = Path(td)
        pages = render_pages(doc, tmp, args.dpi)
        print(f"  已渲染 {len(pages)} 页，调 OcrKit…")

        results, stdout = run_ocrkit(tmp, pages, args.tier, args.device)
        for ln in stdout.strip().splitlines():
            if "ms" in ln or "完成" in ln:
                print("  " + ln)

        if not results:
            print("没有拿到任何识别结果", file=sys.stderr)
            doc.close()
            return 1

        # 纯文本
        text_parts = []
        total_lines = 0
        for page_no, _, _, _ in pages:
            r = results.get(page_no)
            if not r:
                continue
            text_parts.append(r.get("text", ""))
            total_lines += r.get("lineCount", 0)

        text = "\n\n".join(text_parts)
        if args.out_txt:
            Path(args.out_txt).write_text(text, encoding="utf-8")
            print(f"  已写出 {args.out_txt} · {len(text)} 字 / {total_lines} 行")

        # 双层 PDF
        if args.searchable:
            for page_no, img_path, _, _ in pages:
                r = results.get(page_no)
                if not r:
                    continue
                write_text_layer(doc[page_no], r, r["width"], r["height"])
            out = Path(args.searchable)
            doc.save(str(out), garbage=4, deflate=True)
            mb = out.stat().st_size / 1024 / 1024
            print(f"  已写出 {out} · {mb:.1f} MB（文字层不可见，可搜索可复制）")

    doc.close()
    print(f"完成，总耗时 {time.time() - t0:.1f}s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
