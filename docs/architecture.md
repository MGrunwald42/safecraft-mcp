# Architecture

How the processes fit together. The threat model is in [SECURITY.md](../SECURITY.md), the
measurements behind each app's sandbox in [sandbox-notes.md](sandbox-notes.md).

```mermaid
flowchart TB
    subgraph trusted["Trusted: runs as the user, unsandboxed"]
        Agent["Agent (Claude)"]
        Client["MCP client<br/>Claude Code CLI / VS Code / Desktop<br/><i>server config = gateway command line</i>"]
        subgraph gw["safecraft-mcp.exe (.NET 10 NativeAOT gateway)"]
            Settings["SettingsParser<br/>secure defaults + command-line options<br/>(fixed at startup)"]
            Registry["AppRegistry<br/>embedded manifests + guides"]
            Resolver["BinaryResolver / BinaryFreezer<br/>locate, SHA-256, pin check, freeze"]
            Sessions["SessionManager + TierGate<br/>limits, timeouts"]
            Launcher["SandboxLauncher + JobLimits"]
            Transfer["WorkspaceTools / FileTransfer<br/>import / export through grants"]
            ChildRpc["ChildConnection<br/>JSON-RPC client to the app"]
            Log["FileLog<br/>%LOCALAPPDATA%\safecraft-mcp\logs"]
        end
    end

    subgraph host["Host filesystem"]
        Grants["Granted folders<br/>--grant-read / --grant-write"]
        Bin["Frozen binaries<br/>bin\{app}\{sha256}\"]
        WS["Session workspace<br/>(one per session, deleted on close)"]
        Src["{app}-cli.exe builds<br/>(update-craft / MSI)"]
    end

    subgraph mxc["MXC ProcessContainer (Tier 1 BaseContainer)"]
        App["{app}-cli.exe mcp<br/>PhotoCraft / VectorCraft / PdfCraft<br/><b>untrusted</b><br/><i>no network · no UI · no clipboard<br/>Job Object caps</i>"]
    end

    Agent <--> Client
    Client <-->|"stdio JSON-RPC<br/>session_open / session_call / craft_guide …"| gw
    Settings --> Sessions
    Registry --> Sessions
    Src -->|"hash + copy"| Resolver
    Resolver --> Bin
    Sessions --> Launcher
    Launcher -->|"MxcContainer.SpawnAsync<br/>Job Object: memory, process count, kill-on-close"| mxc
    ChildRpc <-->|"MxcProcess stdin/stdout<br/>(output labeled untrusted)"| App
    Transfer <-->|"workspace_import / workspace_export<br/>(path, type, size and link checks)"| Grants
    Transfer --> WS
    App -.->|"read only"| Bin
    App -.->|"read/write"| WS
    App -.-x|"denied"| Grants
```

- **Trust boundary:** only the app runs inside MXC. The agent, the client and the gateway are
  trusted.
- **Files never go directly between your folders and the app.** The gateway copies single files
  between the `--grant-*` folders and the session workspace; the app can't reach the grants.
- **Settings come only from the command line,** which is fixed in the client's MCP server config.
  No tool or tool argument can change them.
- **One container per session.** `craft_commands` also starts a short-lived container to fetch an
  app's command catalog, cached per binary hash.
