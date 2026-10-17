//! HostileWorkload: runs inside the gateway's session sandbox and tries every way out the escape
//! suite names. Each argument is one attempt, "<kind>:<target>"; each prints one JSON
//! line {"attempt", "escaped", "detail"}. "escaped": true means the sandbox let it through. The
//! "expect-*" kinds are the opposite: things that must work (the workspace), so the suite knows
//! the workload really ran.
//!
//!   read-dir:<dir>       list a folder              read-file:<file>     read a file's first byte
//!   write-open:<file>    open an existing file for writing (writes nothing)
//!   create-file:<dir>    create a new file in a folder (deleted again if it worked)
//!   tcp:<host>:<port>    connect                    clipboard            read the clipboard
//!   env:<NAME>           look for a variable, in its own environment and in a spawned cmd's
//!   spawn-read:<file>    spawn cmd.exe to type a file
//!   expect-write:<dir>   create, read back and delete a file: must work
//!   plant-junction:<link>|<target>   make a junction (with cmd's mklink) for the gateway to trip on
//!   plant-hardlink:<link>|<target>   make a hard link, the same way
//!
//! "plant-*" isn't an escape by itself (the app may make links in its own workspace); the suite
//! then checks that the gateway's export refuses to follow what was planted.
//!
//! `mcp <behavior>` is a different mode: a fake MCP server that misbehaves in one chosen way, for
//! the tests of the gateway's ChildConnection and craft_commands (see fake_mcp.rs).
//! Built by the escape suite with `cargo build --release`; run only inside the sandbox.

mod fake_mcp;

use std::ffi::{c_void, CString};
use std::fs::{self, OpenOptions};
use std::net::{TcpStream, ToSocketAddrs};
use std::path::Path;
use std::process::Command;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

#[link(name = "kernel32")]
extern "system" {
    fn LoadLibraryA(name: *const i8) -> *mut c_void;
    fn GetProcAddress(module: *mut c_void, name: *const i8) -> *mut c_void;
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.first().map(String::as_str) == Some("mcp") {
        fake_mcp::run(&args[1..]);
        return;
    }
    for arg in std::env::args().skip(1) {
        let (kind, target) = arg.split_once(':').unwrap_or((arg.as_str(), ""));
        let outcome = match kind {
            "read-dir" => fs::read_dir(target).map(|d| format!("{} entries", d.count())),
            "read-file" => fs::read(target).map(|b| format!("first byte {}", b.first().copied().unwrap_or(0))),
            "write-open" => OpenOptions::new().write(true).open(target).map(|_| "opened for writing".to_string()),
            "create-file" => create(target),
            "tcp" => connect(target),
            "clipboard" => clipboard(),
            "env" => env(target),
            "spawn-read" => spawn_read(target),
            "expect-write" => expect_write(target),
            "plant-junction" => plant(target, "/J"),
            "plant-hardlink" => plant(target, "/H"),
            _ => Ok(format!("unknown attempt kind '{kind}'")),
        };
        // Ok means it got through (for "expect-*": it worked); an error or a "held:" detail means not.
        let (escaped, detail) = match outcome {
            Ok(detail) => match detail.strip_prefix("held:") {
                Some(held) => (false, held.to_string()),
                None => (true, detail),
            },
            Err(error) => (false, error.to_string()),
        };
        println!("{{\"attempt\":{},\"escaped\":{},\"detail\":{}}}", json(&arg), escaped, json(&detail));
    }
}

type Outcome = std::io::Result<String>;

fn held(detail: &str) -> Outcome {
    Ok(format!("held:{detail}"))
}

fn create(dir: &str) -> Outcome {
    let stamp = SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_nanos()).unwrap_or(0);
    let file = Path::new(dir).join(format!("escape-{stamp}.txt"));
    fs::write(&file, b"escaped")?;
    let _ = fs::remove_file(&file);
    Ok(format!("created {}", file.display()))
}

fn connect(target: &str) -> Outcome {
    let address = target
        .to_socket_addrs()?
        .next()
        .ok_or_else(|| std::io::Error::other("no address"))?;
    TcpStream::connect_timeout(&address, Duration::from_secs(3)).map(|_| "connected".to_string())
}

fn clipboard() -> Outcome {
    // user32 is loaded only here: under the UI lockdown it may not load at all.
    unsafe {
        let name = CString::new("user32.dll").unwrap();
        let user32 = LoadLibraryA(name.as_ptr());
        if user32.is_null() {
            return held("user32.dll doesn't load in the sandbox");
        }
        let proc = |n: &str| {
            let n = CString::new(n).unwrap();
            GetProcAddress(user32, n.as_ptr())
        };
        let open: extern "system" fn(*mut c_void) -> i32 = std::mem::transmute(proc("OpenClipboard"));
        let get: extern "system" fn(u32) -> *mut c_void = std::mem::transmute(proc("GetClipboardData"));
        let close: extern "system" fn() -> i32 = std::mem::transmute(proc("CloseClipboard"));
        // From here on it counts as an escape: user32 loading at all means the UI lockdown is gone.
        if open(std::ptr::null_mut()) == 0 {
            return Ok("user32.dll loaded (OpenClipboard failed)".to_string());
        }
        const UNICODE_TEXT: u32 = 13;
        let data = get(UNICODE_TEXT);
        close();
        Ok(if data.is_null() { "user32.dll loaded, clipboard opened (no text)" } else { "read clipboard text" }.to_string())
    }
}

fn plant(target: &str, kind: &str) -> Outcome {
    let (link, to) = target.split_once('|').ok_or_else(|| std::io::Error::other("expected <link>|<target>"))?;
    let (exit, output) = cmd(&["/c", "mklink", kind, link, to])?;
    if exit == 0 {
        Ok(format!("planted {link} -> {to}"))
    } else {
        held(&format!("mklink exit {exit}: {}", output.trim()))
    }
}

fn cmd(args: &[&str]) -> std::io::Result<(i32, String)> {
    let root = std::env::var("SystemRoot").unwrap_or_else(|_| r"C:\Windows".to_string());
    let output = Command::new(Path::new(&root).join("System32").join("cmd.exe")).args(args).output()?;
    Ok((output.status.code().unwrap_or(-1), String::from_utf8_lossy(&output.stdout).into_owned()))
}

fn env(name: &str) -> Outcome {
    if std::env::var_os(name).is_some() {
        return Ok("in the workload's environment".to_string());
    }
    let (exit, output) = cmd(&["/c", "set"])?;
    if output.to_uppercase().contains(&format!("{}=", name.to_uppercase())) {
        Ok("in a spawned cmd's environment".to_string())
    } else {
        held(&format!("not visible (cmd set exit {exit})"))
    }
}

fn spawn_read(file: &str) -> Outcome {
    let (exit, output) = cmd(&["/c", "type", file])?;
    if exit == 0 && !output.is_empty() {
        Ok(format!("cmd type exit 0, {} chars", output.len()))
    } else {
        held(&format!("cmd type exit {exit}"))
    }
}

fn expect_write(dir: &str) -> Outcome {
    let file = Path::new(dir).join("expected.txt");
    fs::write(&file, b"inside")?;
    let back = fs::read(&file)?;
    fs::remove_file(&file)?;
    if back == b"inside" {
        Ok("wrote, read and deleted a file".to_string())
    } else {
        Err(std::io::Error::other("read back something else"))
    }
}

pub(crate) fn json(text: &str) -> String {
    let mut out = String::from("\"");
    for c in text.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
            c => out.push(c),
        }
    }
    out.push('"');
    out
}
