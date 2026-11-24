'use strict';

window.addEventListener('error', (e) => {
  const c = document.getElementById('cap');
  if (c) c.textContent = 'JS 错误：' + (e.message || e.error) + ' @' + (e.lineno || '?');
});
window.addEventListener('unhandledrejection', (e) => {
  const c = document.getElementById('cap');
  if (c) c.textContent = 'Promise 错误：' + (e.reason && e.reason.message || e.reason);
});

/* ── 状态 ─────────────────────────────────────────────────────────────── */
const S = {
  files: [],                       // {id,path,name,status,ms,lines,error,group}
  groups: [],                      // {id,name,sys}
  active: 'all',
  pages: [],
  page: 0,
  running: false,
  seq: 0,
  mock: false
};

const $ = (id) => document.getElementById(id);
const el = {
  files: $('files'), groups: $('groups'), listTitle: $('list-title'), listCount: $('list-count'),
  drop: $('drop-zone'), sheet: $('sheet'), art: $('art'), scene: $('scan-img'),
  reveal: $('reveal'), textpage: $('textpage'),
  cap: $('cap'), outFile: $('out-file'), outSub: $('out-sub'),
  pgSrc: $('pg-src'), pgRange: $('pg-range'), body: $('pg-body'), pgNo: $('pg-no'),
  cur: $('pg-cur'), total: $('pg-total'), prev: $('pg-prev'), next: $('pg-next'),
  exportBtn: $('btn-export'), modal: $('export-modal'), menu: $('group-menu')
};

/* ── 宿主桥（同源相对路径，不依赖 wry IPC） ─────────────────────────── */
function send(m) {
  if (S.mock) return;
  fetch('/__cmd/' + encodeURIComponent(JSON.stringify(m))).catch(() => { });
}

async function pollOnce() {
  try {
    const r = await fetch('/__poll', { cache: 'no-store' });
    const arr = await r.json();
    if (Array.isArray(arr)) for (const s of arr) {
      try { window.__onHostMessage(s); } catch (e) { console.error(e); }
    }
    return true;
  } catch (e) { return false; }
}

function startPolling() {
  let alive = true;
  const loop = async () => {
    if (!alive) return;
    await pollOnce();
    setTimeout(loop, 110);
  };
  loop();
  return () => { alive = false; };
}

window.__onHostMessage = function (raw) {
  let m; try { m = typeof raw === 'string' ? JSON.parse(raw) : raw; } catch { return; }
  switch (m.type) {
    case 'hello': el.outSub.textContent = m.engine || '本地引擎 · 零联网'; break;
    case 'dropped': case 'picked': addPaths(m.paths || []); break;
    case 'selftest': addPaths(m.paths || []); setTimeout(runAll, 300); break;
    case 'progress': el.cap.textContent = `识别中 ${m.index} / ${m.total} · ${m.name}`; break;
    case 'result': onResult(m); break;
    case 'log': el.cap.textContent = m.message || ''; break;
    case 'exported': el.cap.textContent = m.ok ? `已导出：${m.path}` : `导出失败：${m.message}`; break;
    case 'error': el.cap.textContent = `${m.code} ${m.message}`; break;
  }
};

/* ── 分组 ─────────────────────────────────────────────────────────────── */
function initGroups() {
  S.groups = [
    { id: 'all', name: '全部', sys: true },
    { id: 'none', name: '未分组', sys: true }
  ];
}

function groupName(id) {
  const g = S.groups.find(x => x.id === id);
  return g ? g.name : '未分组';
}

function countIn(gid) {
  if (gid === 'all') return S.files.length;
  return S.files.filter(f => f.group === gid).length;
}

function renderGroups() {
  el.groups.innerHTML = '';
  for (const g of S.groups) {
    const d = document.createElement('div');
    d.className = 'g' + (S.active === g.id ? ' on' : '');
    d.innerHTML = `<span class="nm">${esc(g.name)}</span><span class="n">${countIn(g.id)}</span>` +
      (g.sys ? '' : '<span class="del" title="删除分组">×</span>');
    d.onclick = (e) => {
      if (e.target.classList.contains('del')) { removeGroup(g.id); return; }
      S.active = g.id;
      renderGroups(); renderFiles();
      if (S.pages.length) { S.page = 0; renderPage(); }
    };
    el.groups.appendChild(d);
  }
}

function addGroup() {
  if (document.querySelector('.g-new')) return;
  const wrap = document.createElement('div');
  wrap.className = 'g-new';
  const inp = document.createElement('input');
  inp.placeholder = '分组名，回车确定';
  inp.maxLength = 24;
  const done = (commit) => {
    const name = inp.value.trim();
    wrap.remove();
    if (!commit || !name) return;
    const id = 'g' + (++S.seq);
    S.groups.push({ id, name, sys: false });
    S.active = id;
    renderGroups(); renderFiles();
  };
  inp.onkeydown = (e) => {
    if (e.key === 'Enter') done(true);
    if (e.key === 'Escape') done(false);
  };
  inp.onblur = () => done(true);
  wrap.appendChild(inp);
  el.groups.after(wrap);
  inp.focus();
}

function removeGroup(id) {
  S.files.forEach(f => { if (f.group === id) f.group = 'none'; });
  S.groups = S.groups.filter(g => g.id !== id);
  if (S.active === id) S.active = 'all';
  renderGroups(); renderFiles();
}

function assignGroup(file, gid) {
  file.group = gid;
  renderGroups(); renderFiles();
  el.cap.textContent = `「${file.name}」→ ${groupName(gid)}`;
}

/* 分组选择菜单 */
function openGroupMenu(anchor, file) {
  el.menu.innerHTML = '';
  for (const g of S.groups) {
    if (g.id === 'all') continue;
    const b = document.createElement('button');
    b.textContent = g.name;
    if (file.group === g.id) b.className = 'cur';
    b.onclick = () => { el.menu.hidden = true; assignGroup(file, g.id); };
    el.menu.appendChild(b);
  }
  el.menu.hidden = false;
  const r = anchor.getBoundingClientRect();
  el.menu.style.left = Math.min(r.left, window.innerWidth - 150) + 'px';
  el.menu.style.top = Math.min(r.bottom + 4, window.innerHeight - 40 - S.groups.length * 26) + 'px';
}
document.addEventListener('click', (e) => {
  if (!el.menu.hidden && !el.menu.contains(e.target) && !e.target.classList.contains('more')) el.menu.hidden = true;
});

/* ── 文件 ─────────────────────────────────────────────────────────────── */
function addPaths(paths) {
  const seen = new Set(S.files.map(f => f.path.toLowerCase()));
  let added = 0;
  for (const p of paths) {
    const k = String(p).toLowerCase();
    if (seen.has(k)) continue;
    seen.add(k); added++;
    S.files.push({
      id: 'f' + (++S.seq), path: p, name: String(p).split(/[\\/]/).pop(),
      status: '待识别', ms: null, lines: [], error: null, group: 'none'
    });
  }
  renderGroups(); renderFiles();
  if (added) {
    el.sheet.classList.remove('blank');
    send({ type: 'grant', roots: paths });
    if (S.files.length === added) selectFile(S.files[0]);
  }
  el.cap.textContent = added ? `已加入 ${added} 个文件` : '这些文件已经在列表里了';
}

function visibleFiles() {
  if (S.active === 'all') return S.files;
  return S.files.filter(f => f.group === S.active);
}

function renderFiles() {
  const list = visibleFiles();
  el.listTitle.textContent = S.active === 'all' ? '全部文件' : groupName(S.active);
  el.listCount.textContent = list.length;
  el.files.innerHTML = '';

  if (!list.length) {
    const d = document.createElement('div');
    d.className = 'empty-list';
    d.textContent = S.files.length ? '这个分组还没有文件' : '还没有文件';
    el.files.appendChild(d);
    return;
  }

  for (const f of list) {
    const d = document.createElement('div');
    d.className = 'f' + (f.status === '识别中' ? ' busy' : '') + (currentFile() === f ? ' on' : '');
    d.innerHTML = `<span class="thumb"></span>
      <span><div class="nm">${esc(f.name)}</div><div class="st">${esc(stateText(f))}</div></span>
      <span class="more" title="移到分组">⋯</span>`;
    d.onclick = (e) => {
      if (e.target.classList.contains('more')) { openGroupMenu(e.target, f); return; }
      selectFile(f);
    };
    el.files.appendChild(d);
  }
}

function stateText(f) {
  if (f.error) return '失败 · ' + f.error;
  if (f.status === '已识别') return `已识别 · ${f.lines.length} 行`;
  return f.status;
}
function esc(s) { return String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }

/* ── 表格还原：按 y 合并视觉行，按 x 聚列 ─────────────────────────────── */
const cy = (l) => (l.box[1] + l.box[5]) / 2;
const x0 = (l) => Math.min(l.box[0], l.box[6]);
const x1 = (l) => Math.max(l.box[2], l.box[4]);
const lh = (l) => Math.abs(l.box[5] - l.box[1]);

function median(a) {
  if (!a.length) return 0;
  const s = [...a].sort((x, y) => x - y);
  const m = s.length >> 1;
  return s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2;
}

/** 把「每格一行」的 OCR 结果按 y 归并成视觉行 */
function visualRows(lines) {
  const medH = median(lines.map(lh)) || 30;
  const sorted = [...lines].sort((a, b) => cy(a) - cy(b));
  const rows = [];
  for (const l of sorted) {
    const c = cy(l);
    const last = rows[rows.length - 1];
    if (last && Math.abs(last.cy - c) <= medH * 0.55) {
      last.lines.push(l);
      last.cy = (last.cy * (last.lines.length - 1) + c) / last.lines.length;
    } else {
      rows.push({ cy: c, lines: [l] });
    }
  }
  rows.forEach(r => r.lines.sort((a, b) => x0(a) - x0(b)));
  return { rows, medH };
}

/** 在一段连续行里找列边界（x0 的聚类中心） */
function columnClusters(rows, tol) {
  const pts = [];
  rows.forEach(r => r.lines.forEach(l => pts.push(x0(l))));
  pts.sort((a, b) => a - b);
  const cs = [];
  for (const p of pts) {
    const last = cs[cs.length - 1];
    if (last && p - last.sum / last.n <= tol) {
      last.sum += p; last.n++;
    } else {
      cs.push({ sum: p, n: 1 });
    }
  }
  return cs.map(c => c.sum / c.n);
}

/**
 * 识别表格块：
 * 连续视觉行里，多数行有 >=2 格、且各格左边界能对齐到少数几个列位，就算一块表。
 */
function detectTables(lines, pageWidth) {
  if (!lines.length) return [];
  const { rows, medH } = visualRows(lines);
  const tol = Math.max(medH * 0.9, 18);
  const blocks = [];
  let run = [];

  const flush = () => {
    if (run.length >= 3) {
      const cols = columnClusters(run, tol);
      const multi = run.filter(r => r.lines.length >= 2).length;
      if (cols.length >= 2 && multi >= Math.max(2, Math.floor(run.length * 0.6))) {
        blocks.push({
          rows: run.map(r => r.lines.map(l => l.text)),
          cols: cols.length,
          colXs: cols,
          width: pageWidth || 0,
          top: run[0].cy,
          bottom: run[run.length - 1].cy
        });
      }
    }
    run = [];
  };

  for (let i = 0; i < rows.length; i++) {
    const r = rows[i];
    const prev = rows[i - 1];
    const gap = prev ? r.cy - prev.cy : 0;
    const dense = r.lines.length >= 2 || (run.length && gap < medH * 2.4);
    if (dense && (run.length === 0 || gap < medH * 2.4)) run.push(r);
    else { flush(); if (r.lines.length >= 2) run.push(r); }
  }
  flush();
  return blocks;
}
function currentFile() { const p = S.pages[S.page]; return p ? S.files.find(f => f.name === p.file) : null; }

function selectFile(f) {
  el.sheet.classList.remove('blank');
  const idx = S.pages.findIndex(p => p.file === f.name);
  if (idx >= 0) { S.page = idx; renderPage(); }
  showArt(f);
  renderFiles();
}

/* 中间纸面：有图显示图，没图显示白纸 */
function showArt(f) {
  if (!f) { el.sheet.classList.add('blank'); return; }
  el.sheet.classList.remove('blank');
  if (S.mock) {
    el.art.style.display = '';
    el.scene.style.display = 'none';
  } else {
    el.art.style.display = 'none';
    el.scene.setAttribute('href', '/' + encodeURIComponent(f.path));
    el.scene.style.display = '';
  }
  const lines = f.lines || [];
  if (!lines.length) {
    el.textpage.innerHTML = '<p class="ph">等待识别…</p>';
    return;
  }
  el.textpage.innerHTML = lines.map(l => `<span>${esc(l.text) || '&nbsp;'}</span>`).join('');
  layoutText(f);

  /* 按识别坐标把文字排回纸面原位（等比缩放 + 居中留白，与图片一致） */
  function layoutText(file) {
    const r = el.textpage.getBoundingClientRect();
    if (!r.width || !r.height) return;
    const W = file.width || 1240, H = file.height || 1754;
    const VB_W = 1240, VB_H = 1754;               // 纸面 viewBox
    const fit = Math.min(VB_W / W, VB_H / H);     // 图片在纸上的缩放
    const offX = (VB_W - W * fit) / 2;
    const offY = (VB_H - H * fit) / 2;
    const k = r.width / VB_W;                     // viewBox → 像素

    const kids = el.textpage.children;
    for (let i = 0; i < kids.length; i++) {
      const s = kids[i], b = (file.lines[i] || {}).box;
      if (!b || b.length < 8) { s.style.position = 'static'; continue; }
      const xs = [b[0], b[2], b[4], b[6]], ys = [b[1], b[3], b[5], b[7]];
      const x0 = Math.min(...xs), y0 = Math.min(...ys);
      const bh = Math.max(...ys) - y0;
      s.style.position = 'absolute';
      s.style.left = ((offX + x0 * fit) * k) + 'px';
      s.style.top = ((offY + y0 * fit) * k) + 'px';
      s.style.fontSize = Math.max(7, bh * fit * k * 0.74) + 'px';
      s.style.lineHeight = (bh * fit * k * 1.02) + 'px';
      s.style.whiteSpace = 'nowrap';
      s.style.overflow = 'visible';
      s.style.maxWidth = 'none';
    }
  }
  window.__layoutText = () => layoutText(f);
}

/* 原图 / 文本 */
function setView(v) {
  el.sheet.classList.remove('running');
  if (v === 'text') { el.sheet.classList.add('done'); el.sheet.classList.remove('original'); }
  else { el.sheet.classList.add('original'); el.sheet.classList.remove('done'); }
  $('view-original').classList.toggle('on', v === 'original');
  $('view-text').classList.toggle('on', v === 'text');
}
$('view-original').onclick = () => setView('original');
$('view-text').onclick = () => setView('text');

/* ── 分页 ─────────────────────────────────────────────────────────────── */
function linesPerPage() {
  const h = el.body.clientHeight || 320;
  const lh = parseFloat(getComputedStyle(el.body).lineHeight) || 27.9;
  return Math.max(6, Math.floor(h / lh));
}

function buildPages() {
  const n = linesPerPage();
  S.pages = [];
  for (const f of S.files) {
    const lines = (f.lines || []).filter(l => l && l.text);
    if (!lines.length) continue;

    const blocks = detectTables(lines, f.width || 0);
    // 每个块覆盖的 y 区间，用于把行挂到表上
    const band = (l) => blocks.findIndex(b => cy(l) >= b.top - 4 && cy(l) <= b.bottom + 4);
    const used = new Set();
    let buf = [];

    const flushText = () => {
      for (let i = 0; i < buf.length; i += n) {
        S.pages.push({
          kind: 'text', file: f.name,
          lines: buf.slice(i, i + n),
          from: 0, to: 0
        });
      }
      buf = [];
    };

    for (const l of lines) {
      const bi = band(l);
      if (bi >= 0) {
        if (!used.has(bi)) {
          flushText();
          used.add(bi);
          S.pages.push({ kind: 'table', file: f.name, table: blocks[bi], lines: [] });
        }
      } else {
        buf.push(l);
      }
    }
    flushText();

    // 补上每页的行号区间
    let cursor = 0;
    for (const p of S.pages.filter(p => p.file === f.name)) {
      if (p.kind === 'table') { p.from = cursor + 1; cursor += p.table.rows.length; p.to = cursor; }
      else { p.from = cursor + 1; cursor += p.lines.length; p.to = cursor; }
    }
  }
  if (S.page >= S.pages.length) S.page = Math.max(0, S.pages.length - 1);
  renderPage();
}

function renderPage() {
  const total = S.pages.length;
  el.total.textContent = total;
  el.cur.textContent = total ? S.page + 1 : 0;
  el.prev.disabled = S.page <= 0;
  el.next.disabled = S.page >= total - 1;

  if (!total) {
    el.pgSrc.textContent = '—'; el.pgRange.textContent = '';
    el.pgNo.textContent = '— / —';
    el.body.innerHTML = '<p class="empty">识别完成后在这里分页显示</p>';
    el.outFile.textContent = '—'; el.outSub.textContent = '识别完成后生成本文档';
    return;
  }
  const p = S.pages[S.page];
  el.pgSrc.textContent = p.file;
  el.pgRange.textContent = `第 ${p.from}–${p.to} 行`;
  el.pgNo.textContent = `${S.page + 1} / ${total}`;

  if (p.kind === 'table') {
    el.body.innerHTML = renderTable(p.table);
  } else {
    el.body.textContent = p.lines.map(l => l.text).join('\n');
  }

  el.outFile.textContent = p.file;
  const done = S.files.filter(f => (f.lines || []).length).length;
  el.outSub.textContent = `第 ${S.page + 1} / ${total} 页 · 已识别 ${done} 个文件`;

  // 中间纸面跟着翻页走：显示当前页所属文件
  const pf = S.files.find(f => f.name === p.file);
  if (pf && !S.running) showArt(pf);
}

function renderTable(t) {
  const maxCols = Math.max(...t.rows.map(r => r.length));
  const out = ['<table class="ocr-table">'];

  // 用检测到的列位换算列宽，让比例接近原表
  if (t.colXs && t.colXs.length >= 2 && t.width) {
    const xs = t.colXs.slice(0, maxCols);
    const w = [];
    for (let i = 0; i < maxCols; i++) {
      const start = xs[i] != null ? xs[i] : (xs[xs.length - 1] + 60);
      const end = xs[i + 1] != null ? xs[i + 1] : t.width * 0.94;
      w.push(Math.max(end - start, 40));
    }
    const sum = w.reduce((a, b) => a + b, 0);
    out.push('<colgroup>' + w.map(x => `<col style="width:${(x / sum * 100).toFixed(1)}%">`).join('') + '</colgroup>');
  }

  t.rows.forEach((cells, ri) => {
    const tag = ri === 0 ? 'th' : 'td';
    out.push('<tr>');
    for (let c = 0; c < maxCols; c++) out.push(`<${tag}>${esc(cells[c] || '')}</${tag}>`);
    out.push('</tr>');
  });
  out.push('</table>');
  return out.join('');
}

function gotoPage(n) {
  if (!S.pages.length) return;
  S.page = Math.max(0, Math.min(S.pages.length - 1, n));
  renderPage(); renderFiles();
}
el.prev.onclick = () => gotoPage(S.page - 1);
el.next.onclick = () => gotoPage(S.page + 1);
document.addEventListener('keydown', (e) => {
  if (e.target.tagName === 'INPUT') return;
  if (e.key === 'ArrowLeft') gotoPage(S.page - 1);
  if (e.key === 'ArrowRight') gotoPage(S.page + 1);
  if (e.key === 'Escape') { el.modal.hidden = true; el.menu.hidden = true; }
});

/* ── 识别 ─────────────────────────────────────────────────────────────── */
function runAll() {
  if (S.running) return;
  const targets = visibleFiles().filter(f => !f.error);
  if (!targets.length) { el.cap.textContent = '当前分组没有可识别的文件'; return; }
  S.running = true;
  el.sheet.classList.remove('blank');
  el.sheet.classList.add('running');
  el.cap.textContent = '正在识别…';
  targets.forEach(f => f.status = '识别中');
  renderFiles();
  if (S.mock) return mockRun(targets);
  send({ type: 'ocr', paths: targets.map(f => f.path), format: 'txt', tier: 'medium', device: 'auto', chars: false });
}

function onResult(m) {
  for (const p of (m.pages || [])) {
    const f = S.files.find(x => x.name === p.name);
    if (!f) continue;
    if (p.ok) { f.status = '已识别'; f.ms = p.elapsedMs;
      f.width = p.width; f.height = p.height;
      f.lines = (p.lines || []).map(l => ({ text: l.text, box: l.box })); }
    else { f.error = p.code; f.status = '失败'; }
  }
  S.running = false;
  el.sheet.classList.remove('running');
  setView('text');
  buildPages(); renderFiles();
  const c = m.counts || { ok: 0, fail: 0 };
  el.cap.textContent = `完成 ${c.ok} 个` + (c.fail ? `，失败 ${c.fail} 个` : '') +
    (m.totalMs ? ` · ${(m.totalMs / 1000).toFixed(1)} s` : '');
  const cf = currentFile() || S.files.find(f => f.lines.length);
  if (cf) showArt(cf);
}

/* ── 导出 ─────────────────────────────────────────────────────────────── */
let exp = { format: 'txt', layout: 'sections', paging: 'pagebreak', scope: 'all' };
document.querySelectorAll('.chips').forEach(g => {
  g.addEventListener('click', (e) => {
    const b = e.target.closest('.chip'); if (!b) return;
    g.querySelectorAll('.chip').forEach(x => x.classList.remove('on'));
    b.classList.add('on');
    exp[g.dataset.field] = b.dataset.v;
  });
});
el.exportBtn.onclick = () => {
  if (!S.pages.length) { el.cap.textContent = '还没有可导出的内容'; return; }
  el.modal.hidden = false;
};
$('export-cancel').onclick = () => { el.modal.hidden = true; };
$('export-ok').onclick = () => {
  el.modal.hidden = true;
  const cf = currentFile();
  const opts = {
    format: exp.format, layout: exp.layout, paging: exp.paging,
    scope: exp.scope, current: cf ? cf.name : '',
    names: exp.scope === 'group' ? visibleFiles().map(f => f.name) : []
  };
  if (S.mock) { el.cap.textContent = `预览模式：${opts.format} / ${opts.layout} / ${opts.paging} / ${opts.scope}`; return; }
  send({ type: 'export', opts });
};

/* ── 内嵌标题栏 ─────────────────────────────────────────────────────── */
const tb = $('titlebar');
if (tb) {
  tb.addEventListener('mousedown', (e) => {
    if (e.button !== 0) return;
    if (e.target.closest('.wctl') || e.target.closest('.menu')) return;
    send({ type: 'win', action: 'drag' });
  });
  tb.addEventListener('dblclick', (e) => {
    if (e.target.closest('.wctl') || e.target.closest('.menu')) return;
    send({ type: 'win', action: 'max' });
  });
}
$('w-min').onclick = () => send({ type: 'win', action: 'min' });
$('w-max').onclick = () => send({ type: 'win', action: 'max' });
$('w-close').onclick = () => send({ type: 'win', action: 'close' });

/* ── 其他 ─────────────────────────────────────────────────────────────── */
$('new-group').onclick = addGroup;
$('pick-files').onclick = () => S.mock ? addPaths(demoPaths().slice(0, 2)) : send({ type: 'pick', kind: 'files' });
$('pick-folder').onclick = () => S.mock ? addPaths(demoPaths()) : send({ type: 'pick', kind: 'folder' });

$('m-view').onclick = (e) => {
  const next = document.documentElement.dataset.theme === 'ink' ? 'porcelain' : 'ink';
  document.documentElement.dataset.theme = next;
  $('theme-btn').textContent = next === 'ink' ? '浅色' : '暗色';
  try { localStorage.setItem('ocrkit-theme', next); } catch { }
};

function toggleTheme(e) {
  const next = document.documentElement.dataset.theme === 'ink' ? 'porcelain' : 'ink';
  document.documentElement.dataset.theme = next;
  if (e && e.target) e.target.textContent = next === 'ink' ? '浅色' : '暗色';
  try { localStorage.setItem('ocrkit-theme', next); } catch { }
}
$('theme-btn').onclick = toggleTheme;

document.addEventListener('dragover', (e) => { e.preventDefault(); el.drop.classList.add('hot'); });
document.addEventListener('dragleave', () => el.drop.classList.remove('hot'));
document.addEventListener('drop', (e) => {
  e.preventDefault(); el.drop.classList.remove('hot');
  const paths = [...(e.dataTransfer?.files || [])].map(f => f.path).filter(Boolean);
  if (paths.length) addPaths(paths);
});
window.addEventListener('resize', () => {
  if (S.pages.length) buildPages();
  if (window.__layoutText) window.__layoutText();
});

/* ── 演示 ─────────────────────────────────────────────────────────────── */
function demoPaths() {
  return ['F:\\扫描件\\合同扫描页 01.png', 'F:\\扫描件\\发票扫描页 02.png', 'F:\\扫描件\\报告扫描页 04.png'];
}
const DEMO = {
  '合同扫描页 01.png': ['客户合同确认书', '', '甲方：上海晨曦科技有限公司', '乙方：杭州云润贸易有限公司',
    '项目名称：智能办公系统升级', '合同金额：人民币 180,000 元', '交付日期：2026 年 6 月 30 日',
    '签署地点：上海市浦东新区', '签字栏：————————————', '', '附：验收标准与付款节点见附件一。',
    '本确认书一式两份，双方各执一份。', '第一条 本合同自双方签字盖章之日起生效，有效期至项目验收完成。',
    '第二条 甲方应于合同签订后十个工作日内支付预付款，金额为合同总额的百分之三十。',
    '第三条 乙方应按约定的里程碑提交交付物，逾期每日按合同总额的万分之五支付违约金。',
    '第四条 双方对履行过程中知悉的商业秘密负有保密义务，保密期限为合同终止后三年。'],
  '发票扫描页 02.png': ['增值税专用发票', '', '购买方：上海晨曦科技有限公司', '销售方：杭州云润贸易有限公司',
    '项目：软件开发服务', '金额：¥180,000.00', '税率：6%', '税额：¥10,800.00', '合计：¥190,800.00',
    '开票日期：2026 年 6 月 12 日', '', '备注：合同编号 HT-2026-0630。'],
  '报告扫描页 04.png': ['城市轨道交通智能运维实施细则（节选）', '', '第二条 智能运维系统应包含状态监测、故障诊断、预测性维护三大功能模块。',
    '第三条 检测设备的安装位置应避开强电磁干扰源，其与接触网的净距不得小于 1.5 米。',
    '第四条 系统上线前须完成不少于 30 天的连续试运行，误报率应低于 3%。',
    '第五条 运维数据的对外传输应采用国密算法加密，密钥更新周期不超过 90 天。']
};

function mockRun(targets) {
  let i = 0;
  const tick = setInterval(() => {
    if (i >= targets.length) {
      clearInterval(tick);
      S.running = false;
      el.sheet.classList.remove('running');
      setView('text');
      buildPages(); renderFiles();
      el.cap.textContent = `完成 ${targets.length} 个 · 2.1 s`;
      const cf = targets[0];
      const f = S.files.find(x => x.name === cf.name);
      if (f) showArt(f);
      return;
    }
    const f = targets[i];
    el.cap.textContent = `识别中 ${i + 1} / ${targets.length} · ${f.name}`;
    f.status = '已识别'; f.ms = 600 + Math.random() * 160;
    f.width = 1240; f.height = 1754;
    f.lines = (DEMO[f.name] || ['（演示内容）']).map((t, k) => ({
      text: t,
      box: [150, 150 + k * 74, 1180, 150 + k * 74, 1180, 196 + k * 74, 150, 196 + k * 74]
    }));
    i++;
    buildPages(); renderFiles();
    return;
  }, 420);
}

/* ── 启动 ─────────────────────────────────────────────────────────────── */
(function boot() {
  let theme = 'porcelain';
  try { theme = localStorage.getItem('ocrkit-theme') || 'porcelain'; } catch { }
  const m = location.hash.match(/theme=([a-z]+)/i);
  if (m) theme = m[1].toLowerCase() === 'ink' ? 'ink' : 'porcelain';
  document.documentElement.dataset.theme = theme;
  $('theme-btn').textContent = theme === 'ink' ? '浅色' : '暗色';

  initGroups();
  renderGroups();
  renderFiles();
  renderPage();

  // 先探一次宿主：能连上 asset:// 就是真壳，否则按预览处理
  fetch('/__poll', { cache: 'no-store' })
    .then(r => r.json())
    .then(() => { S.mock = false; startPolling(); send({ type: 'ready' }); })
    .catch(() => {
      S.mock = true;
      el.outSub.textContent = '预览模式 · 无宿主';
      el.cap.textContent = '预览模式（未连上壳）';
    });

  if (location.hash.includes('demo')) {
    setTimeout(() => { addPaths(demoPaths()); setTimeout(runAll, 120); }, 300);
  }
})();