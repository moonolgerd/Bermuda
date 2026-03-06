### Bermuda - Windows 10/11 Desktop Application with WebView2

## Project Overview

Bermuda is a Windows 10/11 desktop application that uses Microsoft WebView2 to host a web-based UI inside a native WPF shell. The native host handles OS integration (window management, tray icon, file I/O, system APIs), while the frontend is a modern web app running inside the WebView2 control.

## Tech Stack

- **Native host**: C# (.NET 10+) with WPF, using the `Microsoft.Web.WebView2` NuGet package
- **Aspire orchestrator**: `Bermuda.AppHost` (.NET Aspire app host) wires up the WPF host and any backend services; used for local development orchestration
- **Frontend**: TypeScript + React (Vite), compiled to static assets served from the app bundle
- **IPC**: WebView2 `PostWebMessageAsJson` / `chrome.webview.postMessage` for host ↔ web communication; `AddHostObjectToScript` for synchronous host object exposure
- **Packaging**: Self-contained MSIX via `dotnet msbuild -t:CreateMsix`; uses `Microsoft.Windows.SDK.BuildTools` (`MakeAppx.exe`) and `Package.appxmanifest` in `Bermuda.Package/`; targets Windows 10 1903+ (build 18362)

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
- Aspire manages the Vite dev server via `AddViteApp` — no need to start `npm run dev` manually when using `dotnet run --project src/Bermuda.AppHost`
- WPF projects must target `net10.0-windows` (not `net10.0`) in their `.csproj`; Aspire's app host must also reference the Windows TFM project correctly
