#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

//! OcrKit 外壳：tao 窗口 + wry(WebView2) 界面 + C# 边车进程。
//!
//! 通信不走 wry 的 IPC（本机实测 WebView2 + file:// 组合下收不到），
//! 改用 `asset://` 自定义协议当双向管道：
//!   JS → Rust：fetch('asset://local/__cmd/<urlencoded json>')
//!   Rust → JS：消息进队列，JS 定时 fetch('asset://local/__poll') 取走
//! 好处是零端口、零 socket，权限模型与取图片完全一致。

use std::borrow::Cow;
use std::collections::hash_map::DefaultHasher;
use std::hash::{Hash, Hasher};
use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use tao::dpi::LogicalSize;
use tao::event::{Event, WindowEvent};
use tao::event_loop::{ControlFlow, EventLoop, EventLoopProxy};
use tao::window::{Window, WindowBuilder};
use wry::http::{header, Request, Response, StatusCode};
use wry::{DragDropEvent, WebViewBuilder};

const UI_DIR: &str = r"<ocrkit-dir>\shell\ui";
const UI_URL: &str = "asset://local/ui/index.html";
const ENGINE_EXE: &str = r"<ocrkit-dir>\src\OcrKit\bin\Release\net10.0-windows\win-x64\OcrKit.exe";

#[derive(Debug, Clone, Copy)]
enum WinCmd {
    Minimize,
    Maximize,
    Close,
    Drag,
}

struct Shared {
    stdin: Arc<Mutex<Option<std::process::ChildStdin>>>,
    queue: Arc<Mutex<Vec<String>>>,
    roots: Arc<Mutex<Vec<PathBuf>>>,
    proxy: EventLoopProxy<WinCmd>,
}

fn main() {
    let child = spawn_sidecar(&parent_token());
    let mut child = child;
    let stdin = Arc::new(Mutex::new(child.stdin.take()));
    let queue: Arc<Mutex<Vec<String>>> = Arc::new(Mutex::new(Vec::new()));
    let roots: Arc<Mutex<Vec<PathBuf>>> = Arc::new(Mutex::new(Vec::new()));

    let event_loop: EventLoop<WinCmd> = tao::event_loop::EventLoopBuilder::<WinCmd>::with_user_event().build();
    let proxy = event_loop.create_proxy();

    // 边车 stdout → 队列
    if let Some(stdout) = child.stdout.take() {
        let q = Arc::clone(&queue);
        std::thread::spawn(move || {
            for line in BufReader::new(stdout).lines() {
                let Ok(line) = line else { break };
                if line.trim().is_empty() {
                    continue;
                }
                #[cfg(debug_assertions)]
                eprintln!("[child] {line}");
                let mut g = q.lock().unwrap();
                if g.len() > 4000 {
                    g.drain(0..1000);
                }
                g.push(line);
            }
        });
    }

    let sh = Arc::new(Shared { stdin, queue: Arc::clone(&queue), roots: Arc::clone(&roots), proxy });

    let window = WindowBuilder::new()
        .with_title("OcrKit")
        .with_decorations(false)
        .with_inner_size(LogicalSize::new(1440.0, 900.0))
        .with_min_inner_size(LogicalSize::new(1000.0, 640.0))
        .build(&event_loop)
        .expect("建窗口失败");

    let sh_proto = Arc::clone(&sh);
    let sh_drop = Arc::clone(&sh);
    spawn_selftest(Arc::clone(&sh));

    let start_url = match std::env::var("OCRKIT_THEME") {
        Ok(v) if v.eq_ignore_ascii_case("ink") => format!("{UI_URL}#theme=ink"),
        _ => UI_URL.to_string(),
    };

    let _webview = WebViewBuilder::new()
        .with_url(start_url)
        .with_devtools(cfg!(debug_assertions))
        .with_custom_protocol("asset".into(), move |_id, req| handle(&sh_proto, &req))
        .with_drag_drop_handler(move |ev| {
            if let DragDropEvent::Drop { paths, .. } = ev {
                let list: Vec<String> = paths.iter().map(|p| p.to_string_lossy().to_string()).collect();
                if let Ok(mut r) = sh_drop.roots.lock() {
                    for p in &paths {
                        let root = if p.is_dir() {
                            p.clone()
                        } else {
                            p.parent().map(|x| x.to_path_buf()).unwrap_or_default()
                        };
                        if let Ok(c) = canon(&root) {
                            if !r.iter().any(|x| same_path(x, &c)) {
                                r.push(c);
                            }
                        }
                    }
                }
                for p in &list {
                    let line = serde_json::json!({
                        "id": "grant-drop",
                        "method": "grant",
                        "params": { "roots": [Path::new(p).parent().unwrap_or(Path::new(p)).to_string_lossy()] }
                    });
                    write_child(&sh_drop, &line.to_string());
                }
                push_msg(&sh_drop, &serde_json::json!({ "type": "dropped", "paths": list }));
            }
            true
        })
        .build(&window).expect("建 webview 失败");

    event_loop.run(move |event, _, control_flow| {
        *control_flow = ControlFlow::Wait;
        match event {
            Event::WindowEvent { event: WindowEvent::CloseRequested, .. } => {
                let _ = child.kill();
                *control_flow = ControlFlow::Exit;
            }
            Event::UserEvent(WinCmd::Minimize) => window.set_minimized(true),
            Event::UserEvent(WinCmd::Maximize) => {
                window.set_maximized(!window.is_maximized());
            }
            Event::UserEvent(WinCmd::Close) => {
                let _ = child.kill();
                *control_flow = ControlFlow::Exit;
            }
            Event::UserEvent(WinCmd::Drag) => {
                let _ = window.drag_window();
            }
            _ => {}
        }
    });
}

/// OCRKIT_SELFTEST=<目录>：启动即导入该目录并跑一遍，供冒烟测试使用。
fn spawn_selftest(sh: Arc<Shared>) {
    let Ok(dir) = std::env::var("OCRKIT_SELFTEST") else { return };
    std::thread::spawn(move || {
        std::thread::sleep(std::time::Duration::from_millis(1800));
        let Ok(canon) = canon(&dir) else { return };
        let mut files: Vec<String> = Vec::new();
        if let Ok(rd) = std::fs::read_dir(&canon) {
            for e in rd.flatten() {
                let p = e.path();
                if p.is_file() {
                    files.push(p.to_string_lossy().to_string());
                }
            }
        }
        files.sort();
        if let Ok(mut r) = sh.roots.lock() {
            r.push(canon.clone());
        }
        let grant = serde_json::json!({
            "id": "g-st",
            "method": "grant",
            "params": { "roots": [canon.to_string_lossy().to_string()] }
        });
        write_child(&sh, &grant.to_string());
        let ocr = serde_json::json!({
            "id": format!("job-{}", now_nanos()),
            "method": "ocr",
            "params": { "paths": files, "format": "txt", "tier": "medium", "device": "auto", "chars": false }
        });
        write_child(&sh, &ocr.to_string());
        push_msg(&sh, &serde_json::json!({ "type": "selftest", "paths": files }));
    });
}

// ── 协议处理：取图 + 命令 + 轮询 ───────────────────────────────────────

fn handle(sh: &Arc<Shared>, req: &Request<Vec<u8>>) -> Response<Cow<'static, [u8]>> {
    let raw = req.uri().path().trim_start_matches('/').to_string();
    let decoded = percent_decode(&raw);

    if decoded == "__poll" {
        let msgs: Vec<String> = {
            let mut q = sh.queue.lock().unwrap();
            q.drain(..).collect()
        };
        let body = format!("[{}]", msgs.join(","));
        return json(body);
    }

    if let Some(cmd) = decoded.strip_prefix("__cmd/") {
        dispatch(sh, cmd);
        return json("{\"ok\":true}".to_string());
    }

    // 界面资源：只允许 ui 目录内的固定后缀，防穿越
    if let Some(rel) = decoded.strip_prefix("ui/") {
        let safe = rel.replace("..", "").replace('\\', "/");
        let path = Path::new(UI_DIR).join(&safe);
        let ok_ext = matches!(
            path.extension().and_then(|e| e.to_str()).unwrap_or(""),
            "html" | "css" | "js" | "svg" | "png" | "ico" | "woff2"
        );
        if !ok_ext {
            return plain(StatusCode::FORBIDDEN, "bad ui asset");
        }
        return match std::fs::read(&path) {
            Ok(bytes) => Response::builder()
                .status(StatusCode::OK)
                .header(header::CONTENT_TYPE, ui_mime(&path))
                .header(header::CACHE_CONTROL, "no-store")
                .body(Cow::Owned(bytes))
                .unwrap(),
            Err(_) => plain(StatusCode::NOT_FOUND, "ui asset missing"),
        };
    }

    // 其余当作文件路径：只服务已授权根
    let Ok(full) = canon(&decoded) else {
        return plain(StatusCode::NOT_FOUND, "not found");
    };
    let allowed = sh.roots.lock().map(|g| g.iter().any(|r| is_under(&full, r))).unwrap_or(false);
    if !allowed {
        return plain(StatusCode::FORBIDDEN, "outside granted roots");
    }
    match std::fs::read(&full) {
        Ok(bytes) => Response::builder()
            .status(StatusCode::OK)
            .header(header::CONTENT_TYPE, mime_of(&full))
            .header(header::CACHE_CONTROL, "no-store")
            .body(Cow::Owned(bytes))
            .unwrap(),
        Err(_) => plain(StatusCode::NOT_FOUND, "read failed"),
    }
}

fn dispatch(sh: &Arc<Shared>, json_text: &str) {
    #[cfg(debug_assertions)]
    eprintln!("[cmd] {}", &json_text.chars().take(160).collect::<String>());

    let Ok(v) = serde_json::from_str::<serde_json::Value>(json_text) else { return };
    let kind = v.get("type").and_then(|x| x.as_str()).unwrap_or("");

    match kind {
        "grant" => {
            if let Some(arr) = v.get("roots").and_then(|x| x.as_array()) {
                let mut accepted = Vec::new();
                if let Ok(mut roots) = sh.roots.lock() {
                    for a in arr {
                        if let Some(s) = a.as_str() {
                            if let Ok(c) = canon(s) {
                                if !roots.iter().any(|p| same_path(p, &c)) {
                                    roots.push(c.clone());
                                }
                                accepted.push(c.to_string_lossy().to_string());
                            }
                        }
                    }
                }
                let line = serde_json::json!({ "id": "g", "method": "grant", "params": { "roots": accepted } });
                write_child(sh, &line.to_string());
            }
        }
        "ocr" => {
            let paths = v.get("paths").cloned().unwrap_or(serde_json::json!([]));
            let line = serde_json::json!({
                "id": format!("job-{}", now_nanos()),
                "method": "ocr",
                "params": {
                    "paths": paths,
                    "format": v.get("format").and_then(|x| x.as_str()).unwrap_or("txt"),
                    "tier": v.get("tier").and_then(|x| x.as_str()).unwrap_or("medium"),
                    "device": v.get("device").and_then(|x| x.as_str()).unwrap_or("auto"),
                    "chars": v.get("chars").and_then(|x| x.as_bool()).unwrap_or(false)
                }
            });
            write_child(sh, &line.to_string());
        }
        "pick" => {
            let sh2 = Arc::clone(sh);
            let kind = v.get("kind").and_then(|x| x.as_str()).unwrap_or("files").to_string();
            std::thread::spawn(move || {
                let paths: Vec<String> = if kind == "folder" {
                    rfd::FileDialog::new().pick_folder()
                        .map(|p| vec![p.to_string_lossy().to_string()]).unwrap_or_default()
                } else {
                    rfd::FileDialog::new()
                        .add_filter("图片", &["png", "jpg", "jpeg", "bmp", "webp", "tif", "tiff", "gif"])
                        .pick_files().unwrap_or_default()
                        .into_iter().map(|p| p.to_string_lossy().to_string()).collect()
                };
                if paths.is_empty() { return; }
                if let Ok(mut roots) = sh2.roots.lock() {
                    for p in &paths {
                        let path = Path::new(p);
                        let root = if path.is_dir() { path.to_path_buf() }
                                   else { path.parent().map(|x| x.to_path_buf()).unwrap_or_default() };
                        if let Ok(c) = canon(&root) {
                            if !roots.iter().any(|x| same_path(x, &c)) { roots.push(c); }
                        }
                    }
                }
                push_msg(&sh2, &serde_json::json!({ "type": "picked", "paths": paths }));
            });
        }
        "export" => {
            let sh2 = Arc::clone(sh);
            let opts = v.get("opts").cloned().unwrap_or(serde_json::json!({}));
            std::thread::spawn(move || {
                if let Some(dir) = rfd::FileDialog::new().set_title("选择导出目录").pick_folder() {
                    let out = canon(&dir).unwrap_or(dir);
                    let out_str = out.to_string_lossy().to_string();
                    // 导出目录同样要先授权，否则边车会按 E_ROOT 拒绝
                    if let Ok(mut roots) = sh2.roots.lock() {
                        if !roots.iter().any(|x| same_path(x, &out)) {
                            roots.push(out.clone());
                        }
                    }
                    let grant = serde_json::json!({
                        "id": "g-export",
                        "method": "grant",
                        "params": { "roots": [out_str.clone()] }
                    });
                    write_child(&sh2, &grant.to_string());

                    let mut params = opts.as_object().cloned().unwrap_or_default();
                    params.insert("outDir".into(), serde_json::Value::String(out_str));
                    let line = serde_json::json!({
                        "id": format!("export-{}", now_nanos()),
                        "method": "export",
                        "params": params
                    });
                    write_child(&sh2, &line.to_string());
                }
            });
        }
        "theme" => {}
        "win" => {
            let act = v.get("action").and_then(|x| x.as_str()).unwrap_or("");
            let cmd = match act {
                "min" => Some(WinCmd::Minimize),
                "max" => Some(WinCmd::Maximize),
                "close" => Some(WinCmd::Close),
                "drag" => Some(WinCmd::Drag),
                _ => None,
            };
            if let Some(c) = cmd {
                let _ = sh.proxy.send_event(c);
            }
        }
        _ => {}
    }
}

fn push_msg(sh: &Arc<Shared>, v: &serde_json::Value) {
    if let Ok(mut q) = sh.queue.lock() {
        q.push(v.to_string());
    }
}

fn write_child(sh: &Arc<Shared>, line: &str) {
    if let Ok(mut g) = sh.stdin.lock() {
        if let Some(s) = g.as_mut() {
            let _ = s.write_all(line.as_bytes());
            let _ = s.write_all(b"\n");
            let _ = s.flush();
        }
    }
}

fn json(body: String) -> Response<Cow<'static, [u8]>> {
    Response::builder()
        .status(StatusCode::OK)
        .header(header::CONTENT_TYPE, "application/json; charset=utf-8")
        .header(header::CACHE_CONTROL, "no-store")
        .header(header::ACCESS_CONTROL_ALLOW_ORIGIN, "*")
        .body(Cow::Owned(body.into_bytes()))
        .unwrap()
}

fn plain(code: StatusCode, msg: &str) -> Response<Cow<'static, [u8]>> {
    Response::builder()
        .status(code)
        .header(header::CONTENT_TYPE, "text/plain; charset=utf-8")
        .header(header::ACCESS_CONTROL_ALLOW_ORIGIN, "*")
        .body(Cow::Owned(msg.as_bytes().to_vec()))
        .unwrap()
}

// ── 杂项 ────────────────────────────────────────────────────────────────

fn spawn_sidecar(token: &str) -> std::process::Child {
    use std::os::windows::process::CommandExt;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    Command::new(ENGINE_EXE)
        .arg("--serve")
        .env("OCRKIT_PARENT_TOKEN", token)
        .env("OCRKIT_PARENT_PID", std::process::id().to_string())
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .creation_flags(CREATE_NO_WINDOW)
        .spawn()
        .unwrap_or_else(|e| panic!("边车启动失败（{ENGINE_EXE}）：{e}"))
}

fn ui_mime(p: &Path) -> &'static str {
    match p.extension().and_then(|e| e.to_str()).unwrap_or("") {
        "html" => "text/html; charset=utf-8",
        "css" => "text/css; charset=utf-8",
        "js" => "application/javascript; charset=utf-8",
        "svg" => "image/svg+xml",
        "png" => "image/png",
        "ico" => "image/x-icon",
        "woff2" => "font/woff2",
        _ => "application/octet-stream",
    }
}

fn mime_of(p: &Path) -> &'static str {
    match p.extension().and_then(|e| e.to_str()).unwrap_or("").to_lowercase().as_str() {
        "png" => "image/png",
        "jpg" | "jpeg" => "image/jpeg",
        "webp" => "image/webp",
        "gif" => "image/gif",
        "bmp" => "image/bmp",
        "tif" | "tiff" => "image/tiff",
        _ => "application/octet-stream",
    }
}

fn percent_decode(s: &str) -> String {
    let b = s.as_bytes();
    let mut out = Vec::with_capacity(b.len());
    let mut i = 0;
    while i < b.len() {
        if b[i] == b'%' && i + 2 < b.len() {
            let hex = |c: u8| -> Option<u8> {
                match c {
                    b'0'..=b'9' => Some(c - b'0'),
                    b'a'..=b'f' => Some(c - b'a' + 10),
                    b'A'..=b'F' => Some(c - b'A' + 10),
                    _ => None,
                }
            };
            if let (Some(h), Some(l)) = (hex(b[i + 1]), hex(b[i + 2])) {
                out.push(h * 16 + l);
                i += 3;
                continue;
            }
        }
        out.push(b[i]);
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

/// 归一化路径：Windows 的 canonicalize 会给出 `\\?\F:\...`，这里退回普通形式，
/// 否则会被边车的 UNC / 设备路径防护拦下（那层防护是留给真 UNC 的）。
fn canon(p: impl AsRef<Path>) -> std::io::Result<PathBuf> {
    let c = std::fs::canonicalize(p)?;
    let s = c.to_string_lossy();
    let trimmed = if let Some(rest) = s.strip_prefix(r"\\?\UNC\") {
        format!(r"\\{rest}")
    } else if let Some(rest) = s.strip_prefix(r"\\?\") {
        rest.to_string()
    } else {
        s.to_string()
    };
    Ok(PathBuf::from(trimmed))
}

fn is_under(path: &Path, root: &Path) -> bool {
    let a = path.to_string_lossy().to_lowercase();
    let b = root.to_string_lossy().to_lowercase();
    a == b || a.starts_with(&format!("{b}\\"))
}

fn same_path(a: &Path, b: &Path) -> bool {
    a.to_string_lossy().to_lowercase() == b.to_string_lossy().to_lowercase()
}

fn file_url(p: &str) -> String {
    format!("file:///{}", p.replace('\\', "/"))
}

fn now_nanos() -> u128 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_nanos()).unwrap_or(0)
}

fn parent_token() -> String {
    let mut out = String::with_capacity(64);
    for i in 0..2u64 {
        let mut h = DefaultHasher::new();
        now_nanos().hash(&mut h);
        std::process::id().hash(&mut h);
        i.hash(&mut h);
        out.push_str(&format!("{:016x}", h.finish()));
    }
    out.push_str(&format!("{:032x}", now_nanos()));
    out
}