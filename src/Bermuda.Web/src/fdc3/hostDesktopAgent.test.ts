import { describe, expect, it, vi } from 'vitest'
import { type Fdc3Transport, HostDesktopAgent } from './hostDesktopAgent'

type Responder = (command: string, payload: unknown) => unknown

/** In-memory stand-in for the host: records calls and lets the test push events. */
function fakeHost(respond: Responder = () => null) {
  const calls: { command: string; payload: unknown }[] = []
  let emit: (event: { type: string }) => void = () => {}
  const transport: Fdc3Transport = {
    call: async (command, payload) => {
      calls.push({ command, payload })
      return respond(command, payload)
    },
    onEvent: handler => {
      emit = handler
      return () => {}
    },
  }
  return { agent: new HostDesktopAgent(transport), calls, emit: (event: object) => emit(event as { type: string }) }
}

const incident = { type: 'bermuda.incident', id: { incidentId: '7' } }

describe('HostDesktopAgent', () => {
  it('forwards broadcast to the current user channel', async () => {
    const { agent, calls } = fakeHost()
    await agent.broadcast(incident)
    expect(calls).toEqual([{ command: 'fdc3.broadcast', payload: { channelId: null, context: incident } }])
  })

  it('rejects with the host FDC3 error string', async () => {
    const { agent } = fakeHost(() => ({ error: 'NoChannelFound' }))
    await expect(agent.joinUserChannel('nope')).rejects.toThrow('NoChannelFound')
  })

  it('does not mistake a context with an error field for a failure', async () => {
    const context = { type: 'x.status', error: 'disk full' }
    const { agent } = fakeHost(command =>
      command === 'fdc3.getOrCreateChannel' ? { id: 'ops', type: 'app', displayMetadata: null } : context)

    const ops = await agent.getOrCreateChannel('ops')
    await expect(ops.getCurrentContext()).resolves.toEqual(context)
  })

  it('routes context deliveries to the matching listener with source metadata', async () => {
    const { agent, calls, emit } = fakeHost()
    const handler = vi.fn()
    await agent.addContextListener('bermuda.incident', handler)

    const { listenerId } = calls[0].payload as { listenerId: string }
    expect(calls[0]).toMatchObject({ command: 'fdc3.addContextListener', payload: { channelId: null, contextType: 'bermuda.incident' } })

    emit({ type: 'fdc3.context', listenerId, context: incident, source: { appId: 'map', instanceId: 'i1' } })
    emit({ type: 'fdc3.context', listenerId: 'someone-else', context: incident, source: null })

    expect(handler).toHaveBeenCalledTimes(1)
    expect(handler).toHaveBeenCalledWith(incident, { source: { appId: 'map', instanceId: 'i1' } })
  })

  it('stops delivering after unsubscribe and tells the host', async () => {
    const { agent, calls, emit } = fakeHost()
    const handler = vi.fn()
    const listener = await agent.addContextListener(handler)
    const { listenerId } = calls[0].payload as { listenerId: string }

    await listener.unsubscribe()
    emit({ type: 'fdc3.context', listenerId, context: incident, source: null })

    expect(handler).not.toHaveBeenCalled()
    expect(calls[1]).toEqual({ command: 'fdc3.unsubscribe', payload: { listenerId } })
  })

  it('reports an intent handler result back to the host', async () => {
    const { agent, calls, emit } = fakeHost()
    await agent.addIntentListener('ViewIncident', async () => ({ type: 'fdc3.nothing' }))
    const { listenerId } = calls[0].payload as { listenerId: string }

    emit({ type: 'fdc3.intent', listenerId, intent: 'ViewIncident', context: incident, source: null, resultId: 'r1' })
    await vi.waitFor(() => expect(calls).toHaveLength(2))

    expect(calls[1]).toEqual({
      command: 'fdc3.sendIntentResult',
      payload: { resultId: 'r1', context: { type: 'fdc3.nothing' }, channelId: null, error: null },
    })
  })

  it('reports a throwing intent handler as an error', async () => {
    const { agent, calls, emit } = fakeHost()
    await agent.addIntentListener('ViewIncident', () => { throw new Error('nope') })
    const { listenerId } = calls[0].payload as { listenerId: string }

    emit({ type: 'fdc3.intent', listenerId, intent: 'ViewIncident', context: incident, source: null, resultId: 'r1' })
    await vi.waitFor(() => expect(calls).toHaveLength(2))

    expect(calls[1].payload).toMatchObject({ resultId: 'r1', error: 'Error: nope' })
  })

  it('returns a channel created by the intent handler by id', async () => {
    const { agent, calls, emit } = fakeHost(command =>
      command === 'fdc3.createPrivateChannel' ? { id: 'private.1', type: 'private', displayMetadata: null } : null)
    await agent.addIntentListener('StartChat', () => agent.createPrivateChannel())
    const { listenerId } = calls[0].payload as { listenerId: string }

    emit({ type: 'fdc3.intent', listenerId, intent: 'StartChat', context: incident, source: null, resultId: 'r2' })
    await vi.waitFor(() => expect(calls[calls.length - 1]?.command).toBe('fdc3.sendIntentResult'))

    expect(calls[calls.length - 1]?.payload).toMatchObject({ resultId: 'r2', context: null, channelId: 'private.1' })
  })

  it('wraps raiseIntent results in an IntentResolution', async () => {
    const { agent, calls } = fakeHost(command => {
      if (command === 'fdc3.raiseIntent') return { source: { appId: 'map', instanceId: 'i9' }, intent: 'ViewMap', resultId: 'r3' }
      if (command === 'fdc3.getIntentResult') return { context: { type: 'fdc3.location' }, channel: null }
      return null
    })

    const resolution = await agent.raiseIntent('ViewMap', incident, 'map')
    expect(calls[0].payload).toEqual({ intent: 'ViewMap', context: incident, app: { appId: 'map', instanceId: null } })
    expect(resolution.source).toEqual({ appId: 'map', instanceId: 'i9' })
    await expect(resolution.getResult()).resolves.toEqual({ type: 'fdc3.location' })
  })

  it('strips host nulls from metadata so optional fields are absent', async () => {
    const { agent } = fakeHost(() => ({ id: 'fdc3.channel.1', type: 'user', displayMetadata: { name: 'Channel 1', color: 'red', glyph: null } }))
    const channel = await agent.getCurrentChannel()
    expect(channel?.displayMetadata).toEqual({ name: 'Channel 1', color: 'red' })
  })

  it.each([42, 'a string', { noType: true }])('rejects the raiser when a handler returns %j', async (value) => {
    const { agent, calls, emit } = fakeHost()
    await agent.addIntentListener('ViewIncident', () => value as never)
    const { listenerId } = calls[0].payload as { listenerId: string }

    emit({ type: 'fdc3.intent', listenerId, intent: 'ViewIncident', context: incident, source: null, resultId: 'r9' })
    await vi.waitFor(() => expect(calls).toHaveLength(2))

    expect(calls[1].payload).toMatchObject({ resultId: 'r9', context: null, channelId: null, error: expect.any(String) })
  })

  it('treats a void handler result as success', async () => {
    const { agent, calls, emit } = fakeHost()
    await agent.addIntentListener('ViewIncident', () => {})
    const { listenerId } = calls[0].payload as { listenerId: string }

    emit({ type: 'fdc3.intent', listenerId, intent: 'ViewIncident', context: incident, source: null, resultId: 'r8' })
    await vi.waitFor(() => expect(calls).toHaveLength(2))

    expect(calls[1].payload).toEqual({ resultId: 'r8', context: null, channelId: null, error: null })
  })

  it('shares one retrieval between repeated and concurrent getResult() calls', async () => {
    const { agent, calls } = fakeHost(command => {
      if (command === 'fdc3.raiseIntent') return { source: { appId: 'map', instanceId: 'i9' }, intent: 'ViewMap', resultId: 'r3' }
      if (command === 'fdc3.getIntentResult') return { context: { type: 'fdc3.location' }, channel: null }
      return null
    })
    const resolution = await agent.raiseIntent('ViewMap', incident)

    const [a, b] = await Promise.all([resolution.getResult(), resolution.getResult()])
    const c = await resolution.getResult()

    expect(a).toEqual({ type: 'fdc3.location' })
    expect(b).toBe(a)
    expect(c).toBe(a)
    expect(calls.filter(call => call.command === 'fdc3.getIntentResult')).toHaveLength(1)
  })

  it('returns the same Channel object from every getResult() call', async () => {
    const { agent } = fakeHost(command => {
      if (command === 'fdc3.raiseIntent') return { source: { appId: 'map', instanceId: 'i9' }, intent: 'StartChat', resultId: 'r4' }
      if (command === 'fdc3.getIntentResult') return { context: null, channel: { id: 'private.1', type: 'private', displayMetadata: null } }
      return null
    })
    const resolution = await agent.raiseIntent('StartChat', incident)

    expect(await resolution.getResult()).toBe(await resolution.getResult())
  })

  it('shares a rejection too', async () => {
    const { agent, calls } = fakeHost(command => {
      if (command === 'fdc3.raiseIntent') return { source: { appId: 'map', instanceId: 'i9' }, intent: 'ViewMap', resultId: 'r5' }
      if (command === 'fdc3.getIntentResult') return { error: 'IntentHandlerRejected' }
      return null
    })
    const resolution = await agent.raiseIntent('ViewMap', incident)

    await expect(resolution.getResult()).rejects.toThrow('IntentHandlerRejected')
    await expect(resolution.getResult()).rejects.toThrow('IntentHandlerRejected')
    expect(calls.filter(call => call.command === 'fdc3.getIntentResult')).toHaveLength(1)
  })
})
