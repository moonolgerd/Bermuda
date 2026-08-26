// Type declarations for WebView2 host messaging.
// IpcHostObjects (windowController etc.) is generated from the [IpcHostObject]
// classes in the host project — see src/__generated__/ipc.d.ts and
// src/Bermuda/WindowScriptHost.cs. WebView2 maps each public method/property on
// a host object to an identically named member that resolves as a promise:
// methods via a call, properties via a bare (awaited) access, e.g.
// `await windowController.Left`.
import type { IpcHostObjects } from '../__generated__/ipc'

declare global {
  interface Window {
    chrome: {
      webview: {
        postMessage(message: unknown): void;
        addEventListener(type: 'message', listener: (e: MessageEvent) => void): void;
        removeEventListener(type: 'message', listener: (e: MessageEvent) => void): void;
        hostObjects: IpcHostObjects;
      };
    };
  }
}
