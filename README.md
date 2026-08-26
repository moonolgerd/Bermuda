# Bermuda

A Windows 10/11 desktop application built with a WPF/WebView2 native host, a .NET Aspire orchestrator, a Hot Chocolate GraphQL API, and a React + Relay frontend.

![Tech Stack](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet) ![React](https://img.shields.io/badge/React-19-61DAFB?logo=react) ![TypeScript](https://img.shields.io/badge/TypeScript-strict-3178C6?logo=typescript) ![GraphQL](https://img.shields.io/badge/GraphQL-Hot_Chocolate-E10098?logo=graphql)

## Architecture

```
┌─────────────────────────────────────────┐
│  WPF Host  (Bermuda — net10.0-windows)  │
│  ┌───────────────────────────────────┐  │
│  │   WebView2 (chromium embedded)    │  │
│  │                                   │  │
│  │   React + Relay frontend          │  │
│  │   react-leaflet map               │  │
│  └───────────────────────────────────┘  │
│  Dark Win32 title bar (DWM)             │
└──────────────────┬──────────────────────┘
                   │ GraphQL (HTTP)
┌──────────────────▼──────────────────────┐
│  Bermuda.Api  (ASP.NET Core / .NET 10)  │
│  Hot Chocolate GraphQL                  │
│  Incidents with Priority, Status, Map   │
└─────────────────────────────────────────┘
          orchestrated by
┌─────────────────────────────────────────┐
│  Bermuda.AppHost  (.NET Aspire 13)      │
│  • Vite dev server (port 5555)          │
│  • Relay watch compiler                 │
│  • Aspire dashboard (port 18888)        │
└─────────────────────────────────────────┘
```

## Features

- **Dark theme** — dark Win32 title bar via DWMWA_USE_IMMERSIVE_DARK_MODE
- **GraphQL + Relay** — type-safe queries with auto-generated TypeScript types

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org/) and [pnpm](https://pnpm.io/) (`npm i -g pnpm`)
- [.NET Aspire CLI](https://aspire.dev/get-started/install-cli/): `irm https://aspire.dev/install.ps1 | iex`
- Windows 10 1903+ (build 18362) or Windows 11

## Getting started

```powershell
aspire run
```

The Aspire dashboard opens at `https://localhost:18888`. The WPF window launches automatically once the Vite dev server is ready.

## Project structure

```
src/
├── Bermuda/               # WPF host (WebView2, IPC bridge, dark title bar)
├── Bermuda.Api/           # ASP.NET Core + Hot Chocolate GraphQL API
│   └── GraphQL/
│       ├── Incident.cs    # Incident record + enums (Status, Priority)
│       ├── IncidentRepository.cs
│       └── IncidentQuery.cs
├── Bermuda.AppHost/       # .NET Aspire orchestration
├── Bermuda.ServiceDefaults/
└── Bermuda.Web/           # Vite + React + TypeScript frontend
    ├── src/
    │   ├── bridge/        # WebView2 IPC wrapper (bermudaHost.invoke)
    │   ├── components/
    │   │   ├── IncidentsDashboard.tsx   # Relay query root, selection state
    │   │   ├── IncidentsTable.tsx       # Sortable table
    │   │   └── IncidentMap.tsx          # react-leaflet map
    │   └── __generated__/ # Relay-generated TypeScript types
    └── e2e/               # Playwright E2E tests (CDP, no web server needed)
```

## E2E tests

Tests connect to the running WebView2 process via Chrome DevTools Protocol — no separate browser is launched.

```powershell
cd src/Bermuda.Web
pnpm e2e          # run all 22 tests
pnpm e2e:ui       # open Playwright UI mode
```

## Other useful commands

```powershell
# Build a self-contained MSIX installer
dotnet msbuild src/Bermuda -t:CreateMsix -p:Configuration=Release
# Output: artifacts/msix/
```

## IPC — Web ↔ Host

The frontend calls the WPF host via `bermudaHost.invoke(command, payload)` (fire-and-forget or request/response with UUID correlation). The host pushes events back with `CoreWebView2.PostWebMessageAsJson`. The request/response envelopes are `[IpcType]`-marked records (`IpcRequest`, `HostResponse` in `Bermuda.IpcContract`) generated into `ipc.d.ts` like everything else in this section — `MainWindow` serializes them with `JsonNamingPolicy.CamelCase` (`HostToWebJson`) specifically so the wire format matches those generated (camelCase) shapes.

```
Web                              WPF Host
 │── postMessage({id, cmd}) ────▶│
 │◀─ postMessage({id, result}) ──│  (response)
 │◀─ postMessage({type, ...}) ───│  (push event)
```

### Window control

`MainWindow` projects a small `WindowScriptHost` object into the page via `CoreWebView2.AddHostObjectToScript`. Unlike the `invoke()` channel above, this doesn't round-trip through `HostBridge` — WebView2 itself maps `WindowScriptHost`'s public methods/properties onto identically named (PascalCase) promises at `chrome.webview.hostObjects.windowController`, so call it directly for anything with no extra logic on the JS side:

```tsx
const wc = window.chrome.webview.hostObjects.windowController;

await wc.Minimize();
await wc.Maximize();
await wc.ToggleMaximize();
await wc.Resize(1280, 800);
await wc.Move(100, 100);
await wc.Hide();
const left = await wc.Left; // properties resolve as promises too
```

`bridge/index.ts`'s `bermudaHost.window` only wraps the handful of calls that need real JS-side logic — combining multiple calls, defaulting a param, or narrowing a type:

```tsx
import { bermudaHost } from './bridge';

// Combines 7 property reads into one Promise.all + reshapes the result
const state = await bermudaHost.window.getState(); // { bounds, isMaximized, isMinimized, isFocused }

// Badge the taskbar icon (WPF TaskbarItemInfo.Overlay under the hood); description defaults to ''
bermudaHost.window.setOverlay(pngDataUrl, '3 critical incidents open');
window.chrome.webview.hostObjects.windowController.ClearOverlayIcon();

// Taskbar progress bar (WPF TaskbarItemInfo.ProgressState / .ProgressValue); state is
// narrowed to a literal union here instead of the generated method's raw `string`
bermudaHost.window.setProgressState('indeterminate'); // 'none' | 'indeterminate' | 'normal' | 'error' | 'paused'
window.chrome.webview.hostObjects.windowController.SetProgressValue(0.6); // 0–1
```

`invoke()` commands are allow-listed on the host and return `{ error }` when unknown. Window control has no allow-list to maintain — only the members declared on `WindowScriptHost` are ever reachable from script.

`IncidentsDashboard` uses `setOverlay`/`ClearOverlayIcon` to badge the taskbar icon with the count of open critical incidents (`bridge/overlayBadge.ts` renders the badge to a PNG data URL via `<canvas>`), so it stays visible even when the app isn't focused. `App`/`IncidentsDashboard` also set an indeterminate taskbar progress bar while the initial incidents query is loading, clearing it once data is ready.

### Contract + codegen

`Bermuda.IpcContract` is the single source of truth for all three: `[IpCommand]` marks an `invoke()` command (e.g. `ping`), `[IpcHostObject]` marks a class projected via `AddHostObjectToScript` (e.g. `WindowScriptHost`), and `[IpcType]` marks a plain record/enum whose shape should be generated even though nothing else references it — e.g. `WindowStateSnapshot` (what `getState()` assembles by hand, since WebView2 host-object methods can't return nested structured data directly) or `TaskbarProgressState` (the valid strings for `SetProgressState`, converted by name to WPF's own enum in `MainWindow`). `Bermuda.IpcContract.SourceGenerator` runs as an analyzer on the `Bermuda` project, reflects all three attributes, and writes `src/Bermuda.Web/src/__generated__/ipc.d.ts` on every build — that file is committed (it's small, and it lets the frontend build without needing a prior `dotnet build`, e.g. in CI's MSIX publish job).

If you're touching any hand-written type in `bridge/index.ts` that mirrors a C# shape, check whether it can be `[IpcType]`-generated instead — that's how `IpcRequest`/`HostResponse`/`WindowStateSnapshot`/`TaskbarProgressState` all ended up there, replacing what used to be hand-duplicated (and, in one case — `HostResponse`'s casing — actually mismatched) interfaces.
