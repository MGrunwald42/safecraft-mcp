# Sandbox notes

What each app's CLI needs inside the MXC sandbox, measured by hand. Every app gets a pass like this
before its manifest is enabled.

Host for all measurements: Windows 11 26H2, build 26300.9550, `Microsoft.Mxc.Sdk` 1.0.0 (native
1.0.0). `MxcPlatform.GetAvailableBackends()` reports ProcessContainer at tier `BaseContainer` with
the capabilities `CaptureDenials`, `FilesystemDeniedPaths`, `FilesystemEnumeratePaths` and
`IngressHostLoopbackAllow`.

## MXC facts that shape the gateway

- `MxcContainer.Probe(request)` returns `Tier = BaseContainer` and `NeedsDaclAugmentation = false`
  here, before any spawn, so it's usable as the tier gate.
- With `InheritDefaultEnvironment = false`, MXC refuses an environment without `LOCALAPPDATA`
  (`PolicyValidation`: presence-only). The session profile sets it anyway.
- `ContainerRequest` takes one command **string**, not an argument list. The gateway must build
  it from the manifest's arguments with Windows command-line quoting, so a workspace path with
  spaces or quotes can't split into extra arguments (M2: unit-tested with spaces, quotes and
  trailing backslashes).
- A sandboxed `cmd.exe` starts in about 30 ms and can't read `%USERPROFILE%\.gitconfig` ("access
  denied") without any `DeniedPaths` entry: nothing outside the grants is readable.
- `MxcProcess.Id` is the sandboxed process id (for a Job Object in M6).
- A denied path whose **parent folder doesn't exist** makes the spawn fail with
  `CreateProcessSecurityEnvironment failed (HRESULT = 0x80070003)` (path not found); a missing
  leaf is fine. The gateway passes only denied paths whose parent exists
  (`SandboxRequest.UsableDeniedPaths`).
- A denied parent doesn't block a read-write grant on a folder inside it (the gateway's state
  folder is denied while the session's own folders in it are granted), and siblings stay
  unreadable.
- **How much a read-write grant allows depends on the folder** (2026-10-14, a sandboxed `cmd.exe`
  creating and renaming a file in its granted `work` folder, no denied paths): below `%TEMP%`
  and `%USERPROFILE%` create, rename and mkdir all work; below `%LOCALAPPDATA%\safecraft-mcp`
  create works but **rename is denied**; directly below `%LOCALAPPDATA%` or `%APPDATA%` even
  create is denied. The folders' DACLs are identical (MXC adds no ACE), and `CaptureDenials`
  records the rename as a lowbox write denial on the file and its folder. The cause inside MXC is
  unknown; the denial reports show lookups through Claude Desktop's MSIX `LocalCache`, so AppData
  redirection is a suspect. Every PhotoCraft save writes a temp file and renames it over the
  target, so with the session folders in the state folder every `doc_save` failed with "access
  denied", while the integration tests (their state in a temp folder) passed. Session folders
  therefore live in `%TEMP%\safecraft-mcp-sessions`, protected and denied like the state folder,
  and `SessionFolderTests` saves and overwrites in the real one.
- `TimeoutMs` is enforced **only while something waits** on the `MxcProcess` (`Wait`/`WaitAsync`
  poll it). The gateway starts `WaitAsync` at spawn and keeps it running for the session.
- `Process.GetProcessById(pid).HasExited` throws access denied for protected processes (pid 4);
  the orphan sweep treats those as running.
- **A .NET program doesn't start in the sandbox at all**, NativeAOT included: its runtime
  imports `ole32.dll`, which loads `user32.dll`, which doesn't initialize under the UI lockdown
  (`0xC0000142`); delay-loading ole32 only moves the failure (`0xC06D007E`, the runtime calls it at
  start). Rust and C programs with no user32 import start fine, as do cmd.exe and PhotoCraft. The
  escape suite's workload is therefore Rust. `user32.dll` can't be loaded from inside at all
  (`LoadLibrary` fails), so the clipboard is out of reach.
- From inside, `mklink /J` and `mklink /H` to a protected target fail: the app can't plant a link
  to a folder or file it can't reach. Unprivileged file symlinks are refused on this host
  (Developer Mode off), from inside and out, so the escape suite doesn't try them.
- The quoting in `CommandLine` is what CreateProcess, `CommandLineToArgvW` and Rust's std expect;
  `cmd /c` parses differently (it rejects `\"`), which doesn't matter since no app is cmd.
- **Shutdown order** matters with the MCP SDK's client: closing the app's stdin first makes it
  exit at once (about 15 ms, exit code 0); disposing the SDK client first waits about 5 s for
  stdout to end and the app is then killed (exit code 143). PhotoCraft finishes the request it's
  working on before it exits, so a call running when its session closes gets its real result if it
  ends within the 2 s grace.
- PhotoCraft 0.8.2 answers `ping` with an error response (it doesn't implement it) but keeps
  serving; the gateway counts any answer as alive. After a cancelled call it finishes the step in
  the background and goes on answering.

## PhotoCraft (`photocraft-cli` 0.8.2, commit `c72dda19`)

Sandbox: ProcessContainer at Tier 1 with a unique `ContainerName` and
`ClearPolicyOnExit`, the frozen exe's folder read-only, workspace and profile read-write,
`DeniedPaths` for only `.ssh` and `Documents\GitHub` (more denied paths can't make a read
succeed), egress, ingress and host loopback denied, UI disabled, `InheritDefaultEnvironment =
false` with `SystemRoot`, `WINDIR`, `PATH=C:\Windows\System32` and
`LOCALAPPDATA`/`APPDATA`/`USERPROFILE`/`HOME`/`TEMP`/`TMP` in the profile (no `XDG_*`), and
`PHOTOCRAFT_CPU_COMPOSE=1` in most runs. Command:
`"<frozen>\photocraft-cli.exe" mcp --automation-read-root "<work>" --automation-write-root "<work>"`.

- **It works with nothing extra.** `initialize`, `tools/list`, `command_list`, `doc_new`, a
  gradient fill layer (`command_run layer.newFillLayer.gradient`), `render_preview` (a 512 px PNG,
  55 KB), `doc_save out.png` into the workspace, `doc_inspect` and `resources/read
  photocraft://document` all succeed. The exe is a single file with no DLLs beside it.
- **Latency**: spawn about 300 ms (container plus process start), `initialize` 14 ms, previews
  about 30 ms. `craft_commands` can start a session on demand; a cache per binary hash is a
  convenience, not a necessity.
- **Denials recorded** (`CaptureDenials`, mode block) in a full session: four, all harmless and
  the same with and without `PHOTOCRAFT_CPU_COMPOSE`. No denied read of a system folder or DLL
  showed up, so the default access is enough; a deny-and-record run doesn't list successful
  reads.
  - conhost: a write probe on `C:\WINDOWS`, a read of `HKCU\Console`, and `ConvertToGui` (UI).
  - the CLI opening its own exe with full access (`0x1f01ff`) where the grant is read-only.
- **GPU**: the CLI has no GPU path. `PHOTOCRAFT_CPU_COMPOSE` is read only by the desktop UI's
  wgpu canvas (`crates/ui-egui/src/gpu_canvas.rs`), and the CLI doesn't depend on that crate, so
  the variable is a no-op here and the manifest doesn't set it.
- **App-side confinement** works too: `doc_save` to `C:/Users/<you>/Desktop/escape.png` returns
  `isError:true` ("absolute paths must be inside the automation root") before MXC is involved.
- **Tools in headless mode**: the list includes `control_call` (any control-protocol method) and
  the `ui_*` tools, which need bridge mode. In the sandbox they can't reach a desktop app (no
  network, loopback denied), and headless they return an error, so the gateway passes them
  through. The guide says they need bridge mode.
- The protocol version it answers is the one the client sends (`2025-06-18` here).
- **Fonts**: `type.create` with `"font":"Arial"` renders correctly; the system fonts in
  `C:\Windows\Fonts` are readable without a grant, and no extra denial shows up.

- **Camera raw** (2026-10-14, the same build): a synthetic 6000 x 4000 DNG (16-bit RGGB CFA, as
  PhotoCraft's own `testgen` writes them) opens as a session's `project` and previews, with
  nothing extra. The raw decoder is pure Rust (`flate2`, `zune-jpeg`, `jxl-oxide`, `rayon`), with
  no Windows codec or COM, so only memory and threads matter in the sandbox, and the Job Object's
  defaults are enough for 24 MP (`PhotoCraftFilesTests`).
- **PSB** (2026-10-14): a 40000 x 400 document, past PSD's 30000 px limit, saves as `.psb` in the
  workspace and opens again at full size. A PSB file over 2 GB is past PhotoCraft's default file
  cap (2048 MB) and needs `--max-file-mb photocraft=<n>` to move in or out of a workspace.

## VectorCraft (`vectorcraft-cli`, commit `4eaa5f0b`)

Built from the local checkout without pulling (the user's choice: the updater's safety check had
flagged VectorCraft #1729, about a test-corpus downloader). Same sandbox as PhotoCraft's, with
`VECTORCRAFT_NO_PREFS=1`. Command:
`"<frozen>\vectorcraft-cli.exe" mcp --headless --automation-read-root "<work>" --automation-write-root "<work>"`.

- It works with nothing extra: `initialize`, `command_list`, `draw_shape` (a rectangle and a
  star), `render_preview`, `export art.png`, `save_file art.vectorcraft` and `resources/read
  vectorcraft://document`. The four denials are the same harmless ones as PhotoCraft's.
- App-side confinement refuses `export` to the Desktop and to `../escape.svg` ("outside the write
  root") before MXC is involved.
- It answers one request at a time and ignores `notifications/cancelled` and progress (its
  docs/mcp.md says so): a timed-out call runs on, and the gateway's ping waits behind it.

## PdfCraft (`pdfcraft-cli`, commit `bbbe6001`)

Built from the local checkout without pulling (the safety check had flagged PdfCraft #1114, about
unpinned downloads in release signing and packaging). Command:
`"<frozen>\pdfcraft-cli.exe" mcp --root "<work>"`.

- It works with nothing extra: `doc_create` (blank), `page_add_text`, `render_preview` (a page
  renders at its own size, 612 x 792 at 72 dpi), `doc_save out.pdf`, `command_list` (it answers
  `{commands: [...]}`, not a bare array) and `resources/read pdfcraft://document`.
- App-side confinement refuses saves to the Desktop and to `../escape.pdf` ("outside the allowed
  directory").
- One extra denial, a welcome one: `sign_windows_ids` asks for the `sharedUserCertificates`
  capability and is refused, so the user's certificate store is out of reach (the tool returns no
  ids). `printers` returns none.
- It too handles one request at a time; exports are synchronous and can't be cancelled.

## The child protocol version

Left to negotiate, the MCP SDK's client sometimes took the 2026-07-28 path (per-request `_meta`)
when the host was busy, and PhotoCraft 0.8.2 refused those requests ("request _meta is missing or
has malformed required fields"). The gateway pins `2025-06-18`, the classic `initialize`
handshake all three apps speak (`ChildConnection.ChildProtocolVersion`).

