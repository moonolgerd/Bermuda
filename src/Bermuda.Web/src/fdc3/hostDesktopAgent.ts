// window.fdc3 for pages hosted in the Bermuda WPF shell.
//
// The Desktop Agent itself lives in the host (src/Bermuda.Fdc3/DesktopAgent.cs);
// this class implements the standard @finos/fdc3-standard DesktopAgent
// interface by forwarding each call as an fdc3.* invoke() command (shapes
// generated into ipc/__generated__/ipc.d.ts from src/Bermuda.IpcContract/Fdc3Contract.cs)
// and dispatching the host's fdc3.context / fdc3.intent / fdc3.event pushes to
// the handlers registered here. Errors reject with `new Error(<FDC3 error string>)`
// (e.g. 'NoAppsFound'), as the standard specifies.
import type {
  AppIdentifier,
  AppIntent,
  AppMetadata,
  Channel,
  ContextHandler,
  DesktopAgent,
  EventHandler,
  FDC3EventTypes,
  ImplementationMetadata,
  IntentHandler,
  IntentResolution,
  IntentResult,
  Listener,
  PrivateChannel,
  PrivateChannelEventTypes,
} from '@finos/fdc3-standard'
import type {
  Fdc3ApiEvent,
  Fdc3AppIdentifier,
  Fdc3ChannelInfo,
  Fdc3ContextEvent,
  Fdc3IntentEvent,
  Fdc3IntentResolutionInfo,
  IpcMethods,
} from '../ipc/__generated__/ipc'

/** An FDC3 context (re-derived here so we don't need a direct @finos/fdc3-context dependency). */
export type Context = Parameters<ContextHandler>[0]

type Fdc3Methods = IpcMethods['fdc3']

/** How the agent reaches the host. The default goes through bermudaHost; tests pass a fake. */
export interface Fdc3Transport {
  call(command: string, payload?: unknown): Promise<unknown>
  onEvent(handler: (event: { type: string }) => void): () => void
}

type Handler =
  | { kind: 'context'; handler: ContextHandler }
  | { kind: 'intent'; handler: IntentHandler }
  | { kind: 'event'; handler: EventHandler }

/** Host DTOs use null for "absent"; FDC3 types use optional (undefined) fields. */
function stripNulls<T>(value: unknown): T {
  if (Array.isArray(value)) return value.map(stripNulls) as T
  if (value === null || typeof value !== 'object') return value as T
  return Object.fromEntries(
    Object.entries(value).filter(([, v]) => v !== null).map(([k, v]) => [k, stripNulls(v)]),
  ) as T
}

function isContext(value: unknown): value is Context {
  return typeof value === 'object' && value !== null && typeof (value as { type?: unknown }).type === 'string'
}

function isChannel(value: unknown): value is Channel {
  return value instanceof HostChannel
}

class HostChannel implements Channel {
  readonly id: string
  readonly type: 'user' | 'app' | 'private'
  readonly displayMetadata?: Channel['displayMetadata']

  constructor(protected readonly agent: HostDesktopAgent, info: Fdc3ChannelInfo) {
    this.id = info.id
    this.type = info.type as Channel['type']
    if (info.displayMetadata) this.displayMetadata = stripNulls(info.displayMetadata)
  }

  broadcast(context: Context): Promise<void> {
    return this.agent.call('broadcast', { channelId: this.id, context })
  }

  async getCurrentContext(contextType?: string): Promise<Context | null> {
    const context = await this.agent.call('getCurrentContext', { channelId: this.id, contextType: contextType ?? null })
    return (context as Context | null) ?? null
  }

  addContextListener(contextType: string | null, handler: ContextHandler): Promise<Listener>
  addContextListener(handler: ContextHandler): Promise<Listener>
  addContextListener(typeOrHandler: string | null | ContextHandler, maybeHandler?: ContextHandler): Promise<Listener> {
    const [contextType, handler] = typeof typeOrHandler === 'function'
      ? [null, typeOrHandler]
      : [typeOrHandler, maybeHandler as ContextHandler]
    return this.agent.listen({ kind: 'context', handler }, listenerId =>
      this.agent.call('addContextListener', { listenerId, channelId: this.id, contextType }))
  }
}

class HostPrivateChannel extends HostChannel implements PrivateChannel {
  addEventListener(type: PrivateChannelEventTypes | null, handler: EventHandler): Promise<Listener> {
    return this.agent.listen({ kind: 'event', handler }, listenerId =>
      this.agent.call('addEventListener', { listenerId, eventType: type, channelId: this.id }))
  }

  disconnect(): Promise<void> {
    return this.agent.call('disconnectPrivateChannel', { channelId: this.id })
  }

  // Deprecated synchronous variants: hand back a Listener immediately and
  // finish registering in the background.
  onAddContextListener(handler: (contextType?: string) => void): Listener {
    return this.syncListener('addContextListener', e => handler(e.details?.contextType ?? undefined))
  }

  onUnsubscribe(handler: (contextType?: string) => void): Listener {
    return this.syncListener('unsubscribe', e => handler(e.details?.contextType ?? undefined))
  }

  onDisconnect(handler: () => void): Listener {
    return this.syncListener('disconnect', () => handler())
  }

  private syncListener(type: PrivateChannelEventTypes, handler: EventHandler): Listener {
    const listener = this.addEventListener(type, handler)
    return { unsubscribe: async () => (await listener).unsubscribe() }
  }
}

export class HostDesktopAgent implements DesktopAgent {
  private readonly handlers = new Map<string, Handler>()

  constructor(private readonly transport: Fdc3Transport) {
    transport.onEvent(event => this.dispatch(event))
  }

  // Plumbing ------------------------------------------------------------------

  /** Calls an fdc3.* host command; rejects with Error(<FDC3 error>) if the host returns `{ error }`. */
  async call<K extends keyof Fdc3Methods>(
    command: K,
    ...payload: Parameters<Fdc3Methods[K]>
  ): Promise<Awaited<ReturnType<Fdc3Methods[K]>>> {
    const result = await this.transport.call(`fdc3.${command}`, payload[0])
    if (typeof result === 'object' && result !== null && !isContext(result) && 'error' in result) {
      throw new Error(String((result as { error: unknown }).error))
    }
    return result as Awaited<ReturnType<Fdc3Methods[K]>>
  }

  /** Registers a handler locally, then with the host; undoes the local half if the host refuses. */
  async listen(handler: Handler, register: (listenerId: string) => Promise<void>): Promise<Listener> {
    const listenerId = crypto.randomUUID()
    this.handlers.set(listenerId, handler)
    try {
      await register(listenerId)
    } catch (error) {
      this.handlers.delete(listenerId)
      throw error
    }
    return {
      unsubscribe: async () => {
        this.handlers.delete(listenerId)
        await this.call('unsubscribe', { listenerId })
      },
    }
  }

  private dispatch(event: { type: string }) {
    switch (event.type) {
      case 'fdc3.context': {
        const e = event as Fdc3ContextEvent
        const entry = this.handlers.get(e.listenerId)
        if (entry?.kind !== 'context') return
        entry.handler(e.context as Context, e.source ? { source: this.appIdentifier(e.source) } : undefined)
        return
      }
      case 'fdc3.intent':
        void this.handleIntent(event as Fdc3IntentEvent)
        return
      case 'fdc3.event': {
        const e = event as Fdc3ApiEvent
        const entry = this.handlers.get(e.listenerId)
        if (entry?.kind !== 'event') return
        entry.handler({ type: e.eventType, details: e.details })
        return
      }
    }
  }

  /** Runs an intent handler and reports its outcome back so the raiser's getResult() settles. */
  private async handleIntent(e: Fdc3IntentEvent) {
    const entry = this.handlers.get(e.listenerId)
    let result: IntentResult
    try {
      if (entry?.kind !== 'intent') throw new Error('No handler')
      result = await entry.handler(e.context as Context, e.source ? { source: this.appIdentifier(e.source) } : undefined)
    } catch (error) {
      await this.call('sendIntentResult', { resultId: e.resultId, context: null, channelId: null, error: String(error) })
      return
    }

    await this.call('sendIntentResult', {
      resultId: e.resultId,
      context: isContext(result) && !isChannel(result) ? result : null,
      channelId: isChannel(result) ? result.id : null,
      error: null,
    })
  }

  private appIdentifier(app: Fdc3AppIdentifier): AppIdentifier {
    return stripNulls<AppIdentifier>(app)
  }

  private channel(info: Fdc3ChannelInfo): Channel {
    return info.type === 'private' ? new HostPrivateChannel(this, info) : new HostChannel(this, info)
  }

  private resolution(info: Fdc3IntentResolutionInfo): IntentResolution {
    return {
      source: this.appIdentifier(info.source),
      intent: info.intent,
      getResult: async () => {
        const result = await this.call('getIntentResult', { resultId: info.resultId })
        if (result.channel) return this.channel(result.channel)
        if (result.context) return result.context as Context
        return undefined
      },
    }
  }

  /** FDC3 1.x passed an app name string where 2.x takes an AppIdentifier. */
  private static toAppIdentifier(app: AppIdentifier | string | undefined): Fdc3AppIdentifier | null {
    if (app === undefined) return null
    return typeof app === 'string' ? { appId: app, instanceId: null } : { appId: app.appId, instanceId: app.instanceId ?? null }
  }

  // Apps ------------------------------------------------------------------------

  async open(app: AppIdentifier | string, context?: Context): Promise<AppIdentifier> {
    const opened = await this.call('open', {
      app: HostDesktopAgent.toAppIdentifier(app) as Fdc3AppIdentifier,
      context: context ?? null,
    })
    return this.appIdentifier(opened)
  }

  async findInstances(app: AppIdentifier): Promise<AppIdentifier[]> {
    const instances = await this.call('findInstances', { appId: app.appId, instanceId: app.instanceId ?? null })
    return instances.map(i => this.appIdentifier(i))
  }

  async getAppMetadata(app: AppIdentifier): Promise<AppMetadata> {
    return stripNulls(await this.call('getAppMetadata', { appId: app.appId, instanceId: app.instanceId ?? null }))
  }

  async getInfo(): Promise<ImplementationMetadata> {
    return stripNulls(await this.call('getInfo'))
  }

  // Context & channels ------------------------------------------------------------

  broadcast(context: Context): Promise<void> {
    return this.call('broadcast', { channelId: null, context })
  }

  addContextListener(contextType: string | null, handler: ContextHandler): Promise<Listener>
  addContextListener(handler: ContextHandler): Promise<Listener>
  addContextListener(typeOrHandler: string | null | ContextHandler, maybeHandler?: ContextHandler): Promise<Listener> {
    const [contextType, handler] = typeof typeOrHandler === 'function'
      ? [null, typeOrHandler]
      : [typeOrHandler, maybeHandler as ContextHandler]
    return this.listen({ kind: 'context', handler }, listenerId =>
      this.call('addContextListener', { listenerId, channelId: null, contextType }))
  }

  addEventListener(type: FDC3EventTypes | null, handler: EventHandler): Promise<Listener> {
    return this.listen({ kind: 'event', handler }, listenerId =>
      this.call('addEventListener', { listenerId, eventType: type, channelId: null }))
  }

  async getUserChannels(): Promise<Channel[]> {
    return (await this.call('getUserChannels')).map(c => this.channel(c))
  }

  /** @deprecated FDC3 1.x name for getUserChannels. */
  getSystemChannels(): Promise<Channel[]> {
    return this.getUserChannels()
  }

  joinUserChannel(channelId: string): Promise<void> {
    return this.call('joinUserChannel', { channelId })
  }

  /** @deprecated FDC3 1.x name for joinUserChannel. */
  joinChannel(channelId: string): Promise<void> {
    return this.joinUserChannel(channelId)
  }

  leaveCurrentChannel(): Promise<void> {
    return this.call('leaveCurrentChannel')
  }

  async getCurrentChannel(): Promise<Channel | null> {
    const info = await this.call('getCurrentChannel')
    return info ? this.channel(info) : null
  }

  async getOrCreateChannel(channelId: string): Promise<Channel> {
    return this.channel(await this.call('getOrCreateChannel', { channelId }))
  }

  async createPrivateChannel(): Promise<PrivateChannel> {
    return this.channel(await this.call('createPrivateChannel')) as PrivateChannel
  }

  // Intents ------------------------------------------------------------------------

  async findIntent(intent: string, context?: Context, resultType?: string): Promise<AppIntent> {
    return stripNulls(await this.call('findIntent', { intent, context: context ?? null, resultType: resultType ?? null }))
  }

  async findIntentsByContext(context: Context, resultType?: string): Promise<AppIntent[]> {
    return stripNulls(await this.call('findIntentsByContext', { context, resultType: resultType ?? null }))
  }

  async raiseIntent(intent: string, context: Context, app?: AppIdentifier | string): Promise<IntentResolution> {
    return this.resolution(await this.call('raiseIntent', { intent, context, app: HostDesktopAgent.toAppIdentifier(app) }))
  }

  async raiseIntentForContext(context: Context, app?: AppIdentifier | string): Promise<IntentResolution> {
    return this.resolution(await this.call('raiseIntentForContext', { context, app: HostDesktopAgent.toAppIdentifier(app) }))
  }

  addIntentListener(intent: string, handler: IntentHandler): Promise<Listener> {
    return this.listen({ kind: 'intent', handler }, listenerId =>
      this.call('addIntentListener', { listenerId, intent }))
  }
}
