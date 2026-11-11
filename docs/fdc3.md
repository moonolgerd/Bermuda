# FDC3 support — feature specification

Status: implemented (PR #2). This document is the behavioral contract for Bermuda's FDC3 support. Where it says **MUST**, a test enforces it (see [Verification](#verification)).

## 1. Goal

Make the Bermuda host an [FDC3 2.2](https://fdc3.finos.org/docs/2.2/fdc3-intro) **Desktop Agent**, so that web apps running in Bermuda windows can interoperate with each other (share context, raise intents, open one another) through the standard `window.fdc3` API, with no Bermuda-specific code.

### Non-goals (this version)

- Desktop Agent Bridging (`optionalFeatures.DesktopAgentBridging` is `false`).
- The Web Connection Protocol, i.e. FDC3 apps running in a browser outside the host.
- An intent resolver UI or a channel selector UI provided by the agent (the app has its own channel picker; ambiguous intents are rejected, see [4.4](#44-targeting-and-the-missing-resolver)).
- Persisting anything: channels, contexts, listeners and results live in memory for the life of the process.
- Origin-based access control (see [Known limitations](#8-known-limitations)).

## 2. Architecture

```
 page (any web app)                       WPF host                          .NET
┌───────────────────────┐   postMessage  ┌───────────────────┐   ┌─────────────────────────────┐
│ window.fdc3           │ ─────────────▶ │ HostBridge        │──▶│ Fdc3Connection (per window)│
│ (fdc3-preload.js)     │ ◀───────────── │ MainWindow        │   │   └─ DesktopAgent (shared)  │
└───────────────────────┘  fdc3.* pushes └───────────────────┘   └─────────────────────────────┘
```

| Piece | Where | Role |
|---|---|---|
| `DesktopAgent` | `src/Bermuda.Fdc3` | All state and rules: instances, channels, listeners, intents, results, launching. One per process. |
| `Fdc3Connection` | `src/Bermuda.Fdc3` | One app instance's handle on the agent; implements the command interface for that instance. `IDisposable`. |
| `IFdc3Commands` | `src/Bermuda.IpcContract/Fdc3Contract.cs` | The wire contract: one method per `fdc3.*` command. TypeScript types are generated from it into `ipc.d.ts`. |
| `HostBridge` | `src/Bermuda` | Dispatches an `invoke()` command to the window's connection, binding the JSON payload to the method's parameter type. |
| `MainWindow` | `src/Bermuda` | One connected instance per page load; injects the preload; delivers the agent's pushes to the page. |
| `WindowAppLauncher` | `src/Bermuda` | Opens App Directory apps in new host windows for `open` and for intents. |
| `fdc3-preload.js` | `src/Bermuda/Preload` (built from `src/Bermuda.Web/src/fdc3/preload.ts`) | Installs `window.fdc3` in every page. |
| `HostDesktopAgent` | `src/Bermuda.Web/src/fdc3` | Implements the `@finos/fdc3-standard` `DesktopAgent` interface by calling the commands. |

## 3. Terms

- **App** — an App Directory entry (`appId`). **Instance** — one running page of an app (`instanceId`, a GUID). Every host window is exactly one instance at a time.
- **Listener** — a handler a page registered; identified by a page-generated `listenerId`, which the host echoes in every delivery for it.
- **Source** — the `{appId, instanceId}` that originated a context or intent.

## 4. Behavior

### 4.1 Instances and lifecycle

1. A window connects as a new instance on each **top-level navigation** (first load, reload, link). The previous instance is disposed: its listeners, channel memberships and pending deliveries go with it.
2. **Redirects** within one navigation MUST NOT disconnect or replace the instance.
3. A window opened by the launcher connects with the `instanceId` the agent assigned, so pending deliveries find it.
4. An instance's `appId` is fixed by the window (the launcher's app, or `bermuda-incidents` for the main window), not derived from the page it later navigates to.
5. Closing a window disposes its instance. Responses to requests still in flight at that moment MUST be discarded without touching the closed window.
6. Commands that act *as* an instance (channels, listeners, broadcast, raising, results, `getInfo`) fail with `AccessDenied` once that instance is disposed. Pure directory queries are not tied to an instance.

### 4.2 `window.fdc3` installation

- The preload runs before any page script (`AddScriptToExecuteOnDocumentCreatedAsync`), for every page in every host window, **top-level documents only** (not iframes).
- It sets `window.fdc3` and then dispatches `fdc3Ready`. It MUST leave an existing `window.fdc3` alone and MUST do nothing outside the host.
- The preload only handles the host messages it caused (`fdc3.*` pushes, and responses to its own request ids), so it coexists with a page's own use of `invoke()`.
- Bermuda's own bundle also calls `installFdc3()`, which is a no-op when the preload already ran (kept as a fallback if the preload file is missing).

### 4.3 Context and channels

| Rule | Detail |
|---|---|
| Contexts | A JSON object with a non-empty string `type`; anything else fails with `MalformedContext`. |
| User channels | `fdc3.channel.1` … `fdc3.channel.8` (red, orange, yellow, green, cyan, blue, magenta, purple), listed by `getUserChannels`. An instance is on at most one. |
| `joinUserChannel` | Unknown id → `NoChannelFound`. Joining delivers the channel's current context (per listener type) to listeners that follow the user channel, and fires `userChannelChanged`. |
| `leaveCurrentChannel` | Fires `userChannelChanged` with `currentChannelId: null`. |
| `broadcast` | On a user channel: delivered to every *other* instance on that channel whose listener's type filter matches (a `null` filter matches all); never echoed to the sender. **Not joined → no-op.** The channel remembers the latest context overall and per type (`getCurrentContext`). |
| Listener added late | A listener added while already on a channel immediately receives that channel's current matching context. |
| App channels | `getOrCreateChannel(id)` creates or returns an app channel shared by id; an id that names a user or private channel fails with `AccessDenied`. Broadcast/listen/`getCurrentContext` work as above. |
| Private channels | `createPrivateChannel` makes the caller the sole member. Only members may use it (`AccessDenied` otherwise; unknown id → `NoChannelFound`). |

**Private channels:**

- A channel is shared by returning it as an intent result. The raiser becomes a member when it collects the result, unless it had already disconnected from that channel.
- Events go only to the *other* side: `addContextListener` (when a context listener is added), `unsubscribe` (per context listener removed), `disconnect`.
- Registering an `addContextListener` (or catch-all) event listener MUST also replay one event per context listener the other side added earlier.
- A member leaving (disconnect, or its window disposing) emits `unsubscribe` for each of its context listeners, then `disconnect`.
- The channel is released (state discarded) once it has no members and no uncollected intent result that would hand it to someone.

### 4.4 Intents

**Declaring.** Apps declare what they handle in the App Directory (`interop.intents.listensFor`): per intent a `displayName`, `contexts` and `resultType`. An omitted or empty `contexts` means **any context**, and this holds everywhere (`findIntent`, `findIntentsByContext`, `raiseIntentForContext`).

**Listening.** `addIntentListener(intent, handler)`; one listener per intent per instance (`IntentListenerConflict` otherwise).

**Finding.** A *target* is either a **running instance with a listener** for the intent (matching its declared contexts and result type) or an **App Directory app that could be launched** for it. `findIntent` / `findIntentsByContext` return them grouped by intent; none → `NoAppsFound`. `findIntentsByContext` only considers intents some app declares for that context type.

**Raising.** `raiseIntent(intent, context, app?)` and `raiseIntentForContext(context, app?)`:

1. With an explicit `app`:
   - `{appId, instanceId}`: the instance MUST exist **and belong to that `appId`**, else `TargetInstanceUnavailable`.
   - `{appId}`: prefer a running instance of that app with a listener; otherwise launch it (it must declare the intent and accept the context, else `NoAppsFound`; for `raiseIntent`, an app not in the directory → `TargetAppUnavailable`). `raiseIntentForContext` with an `app` considers only that app's candidates, and none → `NoAppsFound`.
2. Without `app` (no resolver UI): exactly one running listener → deliver to it; no running listener and exactly one launchable app → launch it; zero → `NoAppsFound`; anything else → `ResolverUnavailable` (the caller can retry with an explicit `app`).
3. A launched target gets the intent when it registers the listener; if it does not within the launch timeout (15 s) → `IntentDeliveryFailed`. A launcher failure → `ErrorOnLaunch`. Neither leaves state behind.

**Results.** Every raise yields a `resultId`; `resolution.getResult()` collects the handler's outcome.

- The handler's return value is **void, a Context, or a Channel**. Anything else MUST reject the raiser's `getResult()` with `IntentHandlerRejected`, as MUST a handler that throws/rejects.
- If the handler's result cannot be accepted by the agent (e.g. a private channel it already left), the raiser's `getResult()` MUST still settle (`IntentHandlerRejected`) rather than hang.
- If the handler disconnects before answering, `getResult()` rejects with `NoResultReturned`. If it answered first, the raiser still receives that result.
- The frontend caches the retrieval per resolution: repeated and concurrent `getResult()` calls return the same value / the same `Channel` / the same rejection.
- A result is held until collected, until its raiser disconnects, or for 10 minutes (whichever is first), so apps that never call `getResult()` don't accumulate state.

### 4.5 `open`, discovery and metadata

- `open({appId}, context?)` launches a new instance of an App Directory app (unknown → `AppNotFound`; launcher failure → `ErrorOnLaunch`). It resolves with the new instance's identifier **only once the instance has connected** and, when a `context` was passed, **has registered a context listener that accepts it** (which receives the context). Otherwise `AppTimeout` after 15 s.
- `findInstances({appId})` lists connected instances (empty for a known app with none running; unknown app → `NoAppsFound`).
- `getAppMetadata`: for a specific instance, it MUST exist and match the `appId` (`TargetInstanceUnavailable`); an app that is neither in the directory nor running → `TargetAppUnavailable`.
- `getInfo`: `fdc3Version` `2.2`, `provider` `Bermuda`, `optionalFeatures` `OriginatingAppMetadata` and `UserChannelMembershipAPIs` **true**, `DesktopAgentBridging` **false**, and the caller's own `appMetadata`.
- Deprecated FDC3 1.x names are supported as aliases: `getSystemChannels`, `joinChannel`, and string app names in `open`/`raiseIntent`/`raiseIntentForContext`.

### 4.6 Errors

Rejections are `Error`s whose `message` is the standard FDC3 error string; over the wire a failed command returns `{ error: "<string>" }`.

`AppNotFound` · `ErrorOnLaunch` · `AppTimeout` · `NoAppsFound` · `ResolverUnavailable` · `TargetAppUnavailable` · `TargetInstanceUnavailable` · `IntentDeliveryFailed` · `IntentListenerConflict` · `NoResultReturned` · `IntentHandlerRejected` · `NoChannelFound` · `AccessDenied` · `CreationFailed` (blank channel id) · `MalformedContext` · `InvalidArguments` (missing or ill-typed payload, duplicate/empty listener id, unknown result id).

## 5. Wire contract

- Commands are the methods of `IFdc3Commands` (`[IpcCommands("fdc3")]`); the name is `fdc3.<camelCaseMethod>` without any `Async` suffix. The single method parameter is the JSON payload; the return value (`Task<T>` unwrapped) is the response `result`.
- JSON is camelCase out and case-insensitive in (`JsonSerializerOptions.Web`).
- Host → page pushes are plain messages with a `type` discriminator: `fdc3.context`, `fdc3.intent`, `fdc3.event`. They carry the `listenerId` they are for.
- The TypeScript for all of this is generated into `src/Bermuda.Web/src/ipc/__generated__/ipc.d.ts` by the build; do not edit it.

## 6. App Directory

`src/Bermuda/fdc3-appd.json`, copied next to the executable. It has the shape of the `applications` array of an AppD v2 response, so records can be pasted from a real directory. Fields used: `appId`, `name`, `title`, `tooltip`, `description`, `version`, `type` (only `web` is launchable), `details.url`, and `interop.intents.listensFor`.

- A relative `details.url` resolves against `WEB_URL`; only `http(s)` URLs launch.
- Apps in the directory today: `bermuda-incidents` (the dashboard; handles `ViewIncident` for `bermuda.incident`) and `fdc3-probe` (a plain-HTML sample).

## 7. Bermuda app behavior

- The header has a channel picker (8 buttons); click to join, click the active one to leave.
- Selecting an incident broadcasts `{ type: "bermuda.incident", id: { incidentId }, name: <location> }`. Selection that *arrives* over FDC3 (a broadcast on the joined channel, or the `ViewIncident` intent) selects the incident but does not re-broadcast, so linked windows cannot echo.
- Incidents unknown to the receiving window are ignored.

## 8. Known limitations

- **No origin check.** Any page loaded in a host window gets `window.fdc3` and can issue `fdc3.*` commands as that window's app identity (and, as before this feature, can use the host's other bridges). Fine while the host only loads trusted app URLs; restrict by origin before navigating to untrusted content.
- App identity is per window, not per origin (see 4.1).
- No resolver UI: ambiguous intents need an explicit target.
- State is in-memory only; an app restart clears everything.

## 9. Verification

| Area | Tests |
|---|---|
| Channels and context (4.3) | `DesktopAgentTests`: eight channels, broadcast scoping, type filter, join delivers current context, not-joined no-op, malformed context, unknown channel, `userChannelChanged`, app channels, user channels not fetchable as app channels |
| Private channels (4.3) | `Private_channel_returned_as_an_intent_result_is_shared_with_the_raiser_only`; `ReviewRegressionTests`: released when abandoned, kept alive until collected, replay of earlier listeners |
| Intents (4.4) | `DesktopAgentTests`: find/raise/for-context, results, rejection, launch and launch failure, several listeners need a target, undeclared contexts rejected, listener conflict; `ReviewRegressionTests`: completed result survives handler disconnect, wrong-app instance rejected, invalid result still settles, empty `contexts` matches any, expiry, no leak on failed launch |
| `open` and metadata (4.5) | `DesktopAgentTests`: open with context, unknown app, `getInfo`; `ReviewRegressionTests`: contextless `open` waits for connect, times out |
| Wire contract (5) | `IpcDispatchTests`: command naming, every command discovered, payload binding, `InvalidArguments`, unwrapped errors |
| `window.fdc3` (4.2, frontend) | `hostDesktopAgent.test.ts` (listener routing, results, validation, `getResult` caching), `preload.test.ts` |
| End to end, live app | `e2e/fdc3.spec.ts`: `getInfo`, two linked windows sharing selection and an intent to a specific instance, and a plain-HTML app receiving `open` with a context (proves the preload) |

Run: `dotnet test`, `pnpm test`, and `pnpm e2e` (needs the app: `aspire run`).
