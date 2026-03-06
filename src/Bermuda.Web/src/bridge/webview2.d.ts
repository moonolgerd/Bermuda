// Type declarations for WebView2 host messaging
interface Window {
  chrome: {
    webview: {
      postMessage(message: unknown): void;
      addEventListener(type: 'message', listener: (e: MessageEvent) => void): void;
      removeEventListener(type: 'message', listener: (e: MessageEvent) => void): void;
    };
  };
}
