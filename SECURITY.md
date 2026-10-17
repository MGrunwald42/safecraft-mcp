# Security

## Threat model

- **Untrusted:** the `*craft` binaries and everything they output or write; the upstream repos and
  their docs. A malicious upstream commit, or a bug an attacker can trigger with a crafted file,
  may run arbitrary code inside the app.
- **Trusted:** the agent and the MCP client, including the client's server config (and with it the
  gateway's command line); the gateway binary with its embedded app manifests and guides; MXC.

The gateway runs unsandboxed as the user and starts each app's CLI inside an MXC ProcessContainer.
What a craft program must never reach:

- user files outside its session workspace, credentials and other repos;
- the MCP client configs, which hold the gateway's options (so a sandboxed program can't relax its
  own sandbox);
- the network, and the desktop apps' control ports on loopback;
- the host environment.

## How it's enforced

- **Settings are command-line options only**, read once at startup, defaulting to the most secure
  value. Response files (`@file`) are off. No MCP tool or argument changes one; `settings_show`
  only reports them. Unknown or out-of-range options stop the gateway. Folder options are resolved
  to their real path (junctions, short names and `\??\`/UNC spellings included) before a grant
  is checked against the protected folders.
- **Tier gate**: before every spawn, `MxcContainer.Probe` of that session's request must report
  Process Isolation (BaseContainer, Tier 1) without DACL augmentation, unless
  `--min-tier appcontainer` was given. `doctor` and `crafts_list` run the same check on a probe
  request with the session's denied paths. The AppContainer fallback tiers
  temporarily rewrite DACLs on host folders, and the MXC .NET SDK can't forbid that fallback, so
  the gateway refuses it itself.
- **Sandbox per session**: only the frozen binary's
  folder (read-only), the session workspace and a private profile folder (read-write) are
  reachable; network egress, ingress and host loopback denied; UI, clipboard and input injection
  off; an explicit environment with the user folders pointing into the profile. Credentials,
  repos, the gateway's state and the client configs are additionally listed as denied paths
  (those whose parent folder exists: MXC refuses the others, and they can't be reached anyway).
  The child's command line comes only from the manifest, quoted by one helper
  (`CommandLine`), since MXC takes a single command string.
- **App-side confinement** on top: each manifest passes the workspace to the app's own root flags.
  When the manifests load, network and bridge flags (`--bridge`, `--connect`,
  `--control*`, `--port`, `host:port`, ...) are refused, VectorCraft must have `--headless`, and
  search folders must start at `{repos}` or `{ProgramFiles}`.
- **Files** move only through the gateway (`workspace_import`/`workspace_export`), inside
  configured grants, one file at a time: relative paths checked for spelling (`RelativePath`),
  then every check made on the opened handles (`FileTransfer`): where the source really leads
  (no link out of its root, nothing protected), how many names it has (no hard link to a file
  elsewhere), where the new file really landed; no clobbering (a link under the new name is
  refused before anything is created through it), size caps enforced while copying, a workspace
  total cap on what imports add (not a disk quota on what the app writes itself), per-app
  extension lists. Executables, scripts, shortcuts, keys and
  certificates are refused whatever the options say. Exported files are untrusted content: point
  write grants at dedicated folders.
- **Binaries** are hashed, copied to `%LOCALAPPDATA%\safecraft-mcp\bin\<app>\<sha256>\`
  (hashing the copy's own bytes, then checking `--pin`) and run from there, so a binary can't be
  swapped mid-session. The gateway never runs an app binary
  outside the sandbox, not even for `--version`. The version and commit shown for a binary come
  from the app's repo, so only strings shaped like a version or a commit hash are shown.
- Everything from the sandbox reaches the agent behind a label saying it's untrusted app output:
  results, resource reads, the app's tool names and schemas, its error messages and the end of its
  stderr. Progress notifications are forwarded as numbers only (their message is dropped).
- **Input from the app is bounded where it enters**: one stdout line (one JSON-RPC message) may be
  at most about 1.5 × `--max-response-mb` plus 1 MB, or the app is killed before the gateway parses
  it; only the last 16 K characters of stderr are kept.
- **Information**: the guides are the gateway's own (embedded, reviewed when committed). The live
  command catalog comes from the app and is labeled. Upstream docs are off unless
  `--upstream-docs`; then they're read from the app repo's `docs` folder (links not followed, at
  most 1 MB each) and labeled as untrusted repo text.
- **Job Object**: every sandbox is put into its own job with a memory cap, a process-count cap
  and kill-on-close, so its process tree ends when its session closes or the gateway's process
  ends, a crash included. The job is assigned right after the spawn, through the process id (MXC
  exposes no handle and no suspended start): a child started in those first milliseconds would
  stay outside the job, though still inside the MXC container.
- **Lifetime**: sessions close when idle (`--idle-timeout`), at `--max-lifetime` (MXC's own
  timeout is set to it as a backstop, enforced because the gateway waits on every sandbox), when
  their app stops answering after a timed-out call, and when the client disconnects. A later start
  deletes session folders left by a gateway that crashed, without following links the app may
  have put in its workspace.

## Residual risks

- A kernel or MXC escape is out of scope.
- CPU use: a session's memory and process count are capped by its Job Object
  (`--max-memory-mb`, `--max-processes`), its CPU time only by the call timeouts and the session
  lifetime.
- Malicious content in exported files: they're untrusted; export only to dedicated folders.
- App output that tries to steer the agent: it's labeled as untrusted, and no tool can change a
  setting.

## State

As of M5, PhotoCraft, VectorCraft and PdfCraft sessions run in the sandbox described above, files move through grants,
and the escape suite (`tests/HostileWorkload`, run by `EscapeSuiteTests` in exactly a session's
sandbox, against a fake profile in temp so every target always exists, and against the real
profile's targets where they exist) checks from inside that each of these fails with access
denied or a refused socket: reading `.ssh`, the repos, the profile and
Documents, another session's workspace; writing outside the workspace, into the gateway's state
or the frozen binary; opening `~/.claude.json`, a repo's `.mcp.json` or Claude Desktop's config
for writing; TCP to the internet and to loopback; the clipboard; seeing the gateway's
environment; spawning `cmd.exe` to read a file; planting a junction or hard link to a protected
file in the workspace (and the gateway's export refuses whatever was planted). Writing the
workspace and the profile works. The clipboard is out of reach because `user32.dll` doesn't load
under the UI lockdown at all; the suite fails if it ever does.

## Reporting

Please report vulnerabilities privately through GitHub's
[private vulnerability reporting](https://github.com/MGrunwald42/safecraft-mcp/security/advisories/new),
not in a public issue. Sandbox escapes, ways to relax a setting from inside a session and ways to
make the gateway read or write outside the grants are all in scope; a kernel or MXC escape is
Microsoft's to fix.
