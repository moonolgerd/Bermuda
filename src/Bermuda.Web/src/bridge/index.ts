interface HostMessage {
  id: string;
  result: unknown;
}

const pending = new Map<string, (result: unknown) => void>()

window.chrome?.webview?.addEventListener('message', (e: Event) => {
  const msg = (e as MessageEvent).data as HostMessage
  pending.get(msg.id)?.(msg.result)
  pending.delete(msg.id)
})

function invoke<T>(command: string, payload?: unknown): Promise<T> {
  const id = crypto.randomUUID()
  return new Promise<T>((resolve) => {
    pending.set(id, resolve as (r: unknown) => void)
    window.chrome.webview.postMessage({ id, command, payload })
  })
}

export const bermudaHost = { invoke }
