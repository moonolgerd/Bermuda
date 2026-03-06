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

The frontend calls the WPF host via `bermudaHost.invoke(command, payload)` (fire-and-forget or request/response with UUID correlation). The host pushes events back with `CoreWebView2.PostWebMessageAsJson`.

```
Web                              WPF Host
 │── postMessage({id, cmd}) ────▶│
 │◀─ postMessage({id, result}) ──│  (response)
 │◀─ postMessage({type, ...}) ───│  (push event)
```
