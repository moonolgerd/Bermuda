### Bermuda - Windows 10/11 Desktop Application with WebView2

## Project Overview

Bermuda is a Windows 10/11 desktop application that uses Microsoft WebView2 to host a web-based UI inside a native WPF shell. It tracks incidents in the Bermuda Triangle. The native host handles OS integration (window management, dark title bar, file I/O), while the frontend is a React + Relay app running inside WebView2. A Hot Chocolate GraphQL API serves the data.

## Tech Stack

- **Native host**: C# (.NET 10) with WPF; `Microsoft.Web.WebView2` NuGet package; dark Win32 title bar via `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)`; `WPF-UI` 4.x (lepo.co) for Fluent theme resource dictionaries
- **API**: ASP.NET Core (.NET 10) with Hot Chocolate GraphQL; `Incident` record with `IncidentStatus` and `IncidentPriority` enums
- **Aspire orchestrator**: `Bermuda.AppHost` wires up the WPF host, API, Vite dev server, and Relay watch compiler
- **Frontend**: TypeScript + React 19 + Vite 5; Relay 20 for GraphQL queries; `react-leaflet` 5 + `leaflet` for the map; CSS Modules for styling
- **IPC**: `PostWebMessageAsJson` / `chrome.webview.postMessage` for host ↔ web; UUID correlation map for request/response
- **Packaging**: Self-contained MSIX via `dotnet msbuild -t:CreateMsix`; `Microsoft.Windows.SDK.BuildTools` (`MakeAppx.exe`); `Package.appxmanifest` in `src/Bermuda/`; targets Windows 10 1903+ (build 18362)
- **CI**: GitHub Actions — `build` job on `windows-latest`; `e2e` job on `self-hosted` Windows runner; `publish` job on `v*.*.*.*` tags produces a signed MSIX GitHub Release

## Repository Structure

```
Bermuda/
├── src/
│   ├── Bermuda.AppHost/         # .NET Aspire app host (AppHost.cs)
│   ├── Bermuda.ServiceDefaults/ # Shared Aspire service defaults
│   ├── Bermuda/                 # WPF native host
│   │   ├── MainWindow.xaml(.cs) # Window + WebView2 lifecycle + DWM dark title bar
│   │   ├── App.xaml(.cs)        # WPF-UI ThemesDictionary (dark), app startup
│   │   ├── HostBridge.cs        # IPC command dispatcher
│   │   ├── IpcRequest.cs
│   │   ├── Assets/              # app.ico (multi-size), MSIX tile images, logo.svg
│   │   └── Package.appxmanifest
│   ├── Bermuda.Api/             # ASP.NET Core + Hot Chocolate GraphQL
│   │   └── GraphQL/
│   │       ├── Incident.cs      # record + IncidentStatus/IncidentPriority enums
│   │       ├── IncidentRepository.cs  # 8 seeded incidents
│   │       └── IncidentQuery.cs
│   └── Bermuda.Web/             # Vite + React 19 + TypeScript frontend
│       ├── src/
│       │   ├── bridge/          # bermudaHost.invoke() IPC wrapper
│       │   ├── components/
│       │   │   ├── IncidentsDashboard.tsx  # Relay query root, selectedId state
│       │   │   ├── IncidentsTable.tsx      # Sortable table with priority/status badges
│       │   │   └── IncidentMap.tsx         # react-leaflet 5, CartoDB dark tiles
│       │   ├── types.ts         # IncidentItem type alias from Relay generated types
│       │   └── __generated__/   # Relay-generated TypeScript (do not edit)
│       ├── e2e/
│       │   ├── fixtures.ts      # CDP fixture — connects to WebView2 on port 9222
│       │   └── incidents.spec.ts
│       ├── playwright.config.ts # webServer: aspire run, url: localhost:9222/json
│       └── vite.config.ts       # test.exclude: ['e2e/**'] keeps Vitest away from Playwright specs
├── artifacts/                   # Build outputs (gitignored)
├── Bermuda.slnx
└── .github/
    ├── workflows/ci.yml
    └── copilot-instructions.md
```

## Build & Run

```powershell
# Install frontend dependencies (pnpm is required)
cd src/Bermuda.Web && pnpm install

# Run the full stack via Aspire (API + Vite dev server + Relay watch + WPF host)
aspire run

# Run E2E tests (app must be running, or let playwright.config.ts start it)
cd src/Bermuda.Web && pnpm e2e

# Build frontend only
pnpm build   # relay-compiler → tsc → vite build

# Create a self-contained MSIX installer (output: artifacts/msix/)
dotnet msbuild src/Bermuda -t:CreateMsix -p:Configuration=Release
#   -p:MsixVersion=1.2.3.0 -p:MsixSigningCertThumbprint=<thumbprint>
```

AppHost wires up:
- `Bermuda.Api` — GraphQL API (port assigned by Aspire; `VITE_API_URL` injected into Vite)
- `Bermuda.Web` — Vite dev server on port 5555 (`WEB_URL` injected into WPF host)
- `relay` — `pnpm run relay:watch` watching the API schema URL
- `Bermuda` (WPF host) — `WEB_URL=<vite>`, `CDP_PORT=9222`

## Coding Conventions

### .NET Aspire (AppHost)
- Entry point is `src/Bermuda.AppHost/AppHost.cs`
- `AddViteApp("web", "../Bermuda.Web").WithPnpm().WithEnvironment("PORT", "5555")`
- Pass `VITE_API_URL` to the Vite app so the frontend knows the GraphQL endpoint
- Pass `WEB_URL` and `CDP_PORT` to the WPF host via `.WithEnvironment()`
- Use `.WaitFor(api)` / `.WaitFor(web)` to enforce startup order

### C# (Host & API)
- Target `net10.0-windows` for WPF; `net10.0` for the API
- WebView2: all calls after `EnsureCoreWebView2Async()` only; UI-thread dispatch via `Dispatcher.Invoke`
- Dark title bar: `DwmSetWindowAttribute(hwnd, 20, ref dark=1, 4)` in `OnLoaded`
- Do NOT use `FluentWindow` / `ExtendsContentIntoTitleBar` — WebView2's child HWND swallows resize hit-test messages
- `HostBridge` methods must be `[ComVisible(true)]`, wrapped in try/catch, never throw
- Prefer `System.Text.Json` records; avoid Newtonsoft.Json

### TypeScript / React (Frontend)
- React 19 functional components, strict TypeScript
- Global state: React context + `useReducer`; local state: `useState`
- All GraphQL data comes through Relay (`src/__generated__/`); run `pnpm relay` after schema changes
- Query in `IncidentsDashboard.tsx` must be named `IncidentsDashboardQuery` (Relay requires query name = module name)
- All host bridge calls through `bermudaHost.invoke()` — never call `window.chrome.webview` directly in components
- CSS Modules only; no inline styles except dynamic values; no semicolons (`"semi": ["error", "never"]` in ESLint)

### Relay
- Schema lives at `src/Bermuda.Web/schema.graphql` — update it when the API changes, then run `pnpm relay`
- Generated files in `src/__generated__/` are committed; do not edit them manually
- `relay:watch` is managed by Aspire during development; `relay:schema` downloads a fresh schema from the running API

## E2E Testing

Playwright connects to the live WebView2 via CDP on port 9222 — no separate browser process.

```typescript
// e2e/fixtures.ts — overrides the `page` fixture
const browser = await chromium.connectOverCDP('http://localhost:9222')
const page = browser.contexts()[0].pages()[0]
// Reloads only when sort state has drifted (avoids flicker on every test)
const sort = await page.locator('thead th').first().getAttribute('aria-sort')
if (sort !== 'ascending') { await page.reload(); ... }
await provide(page)
// Never call browser.close() — it would kill the WebView2 host process
```

- `playwright.config.ts` sets `webServer: { command: 'aspire run', cwd: '../..', url: 'http://localhost:9222/json', reuseExistingServer: true }`
- `vite.config.ts` sets `test.exclude: ['e2e/**']` so Vitest never picks up Playwright specs
- Run with `pnpm e2e` (Playwright) not `pnpm test` (Vitest)

## CI (`.github/workflows/ci.yml`)

| Job | Runner | Trigger | Does |
|---|---|---|---|
| `build` | `windows-latest` | every push/PR | dotnet build, lint, Vitest, pnpm build |
| `e2e` | `self-hosted` | after `build` | full stack via `aspire run`, 22 Playwright tests |
| `publish` | `windows-latest` | `v*.*.*.*` tags | CreateMsix, GitHub Release with `.msix` asset |

Self-hosted runner needed for `e2e` because WebView2 requires an interactive desktop session.

MSIX signing: add `MSIX_CERT_BASE64` (base64 PFX) and `MSIX_CERT_PASSWORD` secrets to the repo; CI imports the cert and passes the thumbprint to `SignTool` automatically.

## Security

- `AreDefaultContextMenusEnabled = false` and `AreDevToolsEnabled = false` in Release builds
- Validate and allowlist all IPC command names in `HostBridge`
- Never pass unsanitised JS input into C# file paths or shell commands
- Do not expose sensitive .NET APIs via `AddHostObjectToScript`

## Common Pitfalls

- `CoreWebView2` is only available after `EnsureCoreWebView2Async()` — await before any use
- Do not use `FluentWindow` with `ExtendsContentIntoTitleBar` + WebView2 — resize breaks
- Relay query name must match the module filename (e.g. `IncidentsDashboardQuery` in `IncidentsDashboard.tsx`)
- `pnpm test` runs Vitest; `pnpm e2e` runs Playwright — never mix them
- `browser.close()` in the CDP fixture would terminate the host process — omit it intentionally
- WPF projects must target `net10.0-windows`; the solution file is `Bermuda.slnx` (not `.sln`)


## Repository Structure

```
Bermuda/
├── src/
│   ├── Bermuda.AppHost/       # .NET Aspire app host — orchestrates all resources
│   │   └── Program.cs
│   ├── Bermuda.ServiceDefaults/ # Shared Aspire service defaults (telemetry, health checks)
│   ├── Bermuda/               # C# WPF native host project (.csproj)
│   │   ├── MainWindow.xaml(.cs)  # Main window + WebView2 lifecycle
│   │   ├── HostBridge.cs         # [ClassInterface] host object exposed to JS
│   │   ├── IpcRequest.cs
│   │   ├── App.xaml(.cs)
│   │   ├── Package.appxmanifest  # MSIX manifest
│   │   ├── Assets/               # App icons (replace placeholders before publishing)
│   │   └── appsettings.json
│   └── Bermuda.Web/           # Frontend (Vite + React + TypeScript)
│       ├── src/
│       │   ├── bridge/       # bermudaHost IPC wrapper (index.ts)
│       │   ├── components/
│       │   ├── pages/
│       │   └── main.tsx
│       ├── index.html
│       ├── vite.config.ts
│       └── package.json
├── artifacts/                 # Build outputs (gitignored)
├── Bermuda.sln
└── .github/
    └── copilot-instructions.md
```

## Build & Run

```powershell
# Install frontend dependencies
cd src/Bermuda.Web && npm install

# Build frontend (outputs to src/Bermuda.Web/dist)
npm run build

# Run the full app via Aspire (starts Vite dev server + WPF host + Aspire dashboard)
dotnet run --project src/Bermuda.AppHost

# Run the WPF host directly (without Aspire orchestration)
dotnet run --project src/Bermuda

# Create a self-contained MSIX installer (output: artifacts/msix/)
dotnet msbuild src/Bermuda -t:CreateMsix -p:Configuration=Release
# Override version or sign:
#   -p:MsixVersion=1.2.3.0 -p:MsixSigningCertThumbprint=<thumbprint>
```

In development, the host loads `http://localhost:5173` (Vite dev server). In production, it loads `ms-appx-web:///` or a local `file://` path pointing to the bundled `dist/` assets.

## Coding Conventions

### .NET Aspire (AppHost)
- `Bermuda.AppHost` references `Bermuda.Host` (and any backend services) as Aspire resources
- Register the Vite dev server as a Vite app resource: `builder.AddViteApp("web", "../Bermuda.Web").WithHttpEndpoint(port: 5173, env: "PORT")`
- Pass the frontend URL to the WPF host via `.WithEnvironment("WEB_URL", web.GetEndpoint("http"))` so the host can load the correct origin
- Use `AddProject<Projects.Bermuda_Host>()` to register the WPF host; pass required environment variables (e.g. `ASPIRE_ALLOW_UNSECURED_TRANSPORT`) via `.WithEnvironment()`
- Service discovery URLs injected by Aspire are consumed by `Bermuda.Host` via `IConfiguration` / environment variables — never hard-code ports
- `Bermuda.ServiceDefaults` adds OpenTelemetry, health checks, and service discovery; call `builder.AddServiceDefaults()` in each .NET service's `Program.cs`
- The Aspire dashboard is available at `https://localhost:18888` during local development

### C# (Host)
- Target `.NET 10`; use `async`/`await` throughout — WebView2 APIs are async
- Use `CoreWebView2.PostWebMessageAsJson(json)` to push data to the web layer
- Use `CoreWebView2.WebMessageReceived` to receive messages from the web layer
- All cross-thread WebView2 calls must be dispatched to the UI thread via `Dispatcher.Invoke`/`Dispatcher.BeginInvoke`
- `HostBridge` methods exposed via `AddHostObjectToScript` must be `[ComVisible(true)]` and must not throw unhandled exceptions (wrap with try/catch and return error objects)
- Prefer records and `System.Text.Json` for serialisation; avoid Newtonsoft.Json

### TypeScript / React (Frontend)
- Strict TypeScript (`"strict": true` in tsconfig)
- React functional components only; no class components
- State management: React context + `useReducer` for global state; `useState` for local UI state
- All host bridge calls go through `src/bridge/` via the exported `bermudaHost` object — never call `chrome.webview` directly in components
- Use `postMessage` for async fire-and-forget; use request/response correlation (UUID + pending map) for calls that need a return value
- CSS: CSS Modules or Tailwind; no inline styles except for dynamic values

## Host ↔ Web IPC Pattern

### Web → Host (async request/response)
```typescript
// src/bridge/index.ts
const pending = new Map<string, (result: unknown) => void>();

window.chrome.webview.addEventListener('message', (e) => {
  const msg = e.data as { id: string; result: unknown };
  pending.get(msg.id)?.(msg.result);
  pending.delete(msg.id);
});

function invoke<T>(command: string, payload?: unknown): Promise<T> {
  const id = crypto.randomUUID();
  return new Promise<T>((resolve) => {
    pending.set(id, resolve as (r: unknown) => void);
    window.chrome.webview.postMessage({ id, command, payload });
  });
}

export const bermudaHost = { invoke };
```

```csharp
// MainWindow.cs
_webView.CoreWebView2.WebMessageReceived += async (_, e) =>
{
    var req = JsonSerializer.Deserialize<IpcRequest>(e.WebMessageAsJson)!;
    var result = await _bridge.HandleAsync(req.Command, req.Payload);
    var response = JsonSerializer.Serialize(new { req.Id, result });
    _webView.CoreWebView2.PostWebMessageAsJson(response);
};
```

### Host → Web (push events)
```csharp
_webView.CoreWebView2.PostWebMessageAsJson(
    JsonSerializer.Serialize(new { type = "FILE_CHANGED", path = filePath }));
```

```typescript
window.chrome.webview.addEventListener('message', (e) => {
  const event = e.data as HostEvent;
  if (event.type === 'FILE_CHANGED') { /* handle */ }
});
```

## Security

- **Virtual host mapping**: prefer `SetVirtualHostNameToFolderMapping` over raw `file://` URLs so the web content has a stable, controlled origin
- **`AreDefaultContextMenusEnabled = false`** in production builds
- **`AreDevToolsEnabled = false`** in production builds; enable only when `#DEBUG`
- Never pass unsanitised user input from JS into C# file paths or shell commands
- Validate and allowlist all IPC command names in `HostBridge.HandleAsync`
- Do not expose sensitive .NET APIs via `AddHostObjectToScript`

## Testing

- **Frontend unit tests**: Vitest + React Testing Library (`npm test` in `src/Bermuda.Web`)
- **Frontend E2E / integration**: Playwright targeting the running WebView2 app via `--remote-debugging-port` (see user preferences — prefer Playwright over manual testing)
- **Host unit tests**: xUnit project at `src/Bermuda.Host.Tests/`
- **Aspire integration tests**: use `Aspire.Hosting.Testing` + xUnit to spin up the full `Bermuda.AppHost` in tests
- Do not use native modules in the frontend (see user preferences)

## Common Pitfalls

- `CoreWebView2` is only available after `EnsureCoreWebView2Async()` completes — await it before any usage
- WebView2 `NavigationCompleted` fires before scripts are ready; use `DOMContentLoaded` message from the web layer to signal readiness
- MSIX packaging requires the WebView2 Runtime to be declared as a dependency in the manifest, or use the fixed-version runtime distribution
- Hot reload in development is handled by the Vite dev server managed by Aspire; the WPF host reads `WEB_URL` from its environment to know which origin to load
- Aspire manages the Vite dev server via `AddViteApp` — no need to start `npm run dev` manually when using `aspire run`
- WPF projects must target `net10.0-windows` (not `net10.0`) in their `.csproj`; Aspire's app host must also reference the Windows TFM project correctly
