var __defProp = Object.defineProperty;
var __defNormalProp = (obj, key, value) => key in obj ? __defProp(obj, key, { enumerable: true, configurable: true, writable: true, value }) : obj[key] = value;
var __publicField = (obj, key, value) => __defNormalProp(obj, typeof key !== "symbol" ? key + "" : key, value);
(function() {
  "use strict";
  function stripNulls(value) {
    if (Array.isArray(value)) return value.map(stripNulls);
    if (value === null || typeof value !== "object") return value;
    return Object.fromEntries(
      Object.entries(value).filter(([, v]) => v !== null).map(([k, v]) => [k, stripNulls(v)])
    );
  }
  function isContext(value) {
    return typeof value === "object" && value !== null && typeof value.type === "string";
  }
  function isChannel(value) {
    return value instanceof HostChannel;
  }
  class HostChannel {
    constructor(agent, info) {
      __publicField(this, "id");
      __publicField(this, "type");
      __publicField(this, "displayMetadata");
      this.agent = agent;
      this.id = info.id;
      this.type = info.type;
      if (info.displayMetadata) this.displayMetadata = stripNulls(info.displayMetadata);
    }
    broadcast(context) {
      return this.agent.call("broadcast", { channelId: this.id, context });
    }
    async getCurrentContext(contextType) {
      const context = await this.agent.call("getCurrentContext", { channelId: this.id, contextType: contextType ?? null });
      return context ?? null;
    }
    addContextListener(typeOrHandler, maybeHandler) {
      const [contextType, handler] = typeof typeOrHandler === "function" ? [null, typeOrHandler] : [typeOrHandler, maybeHandler];
      return this.agent.listen({ kind: "context", handler }, (listenerId) => this.agent.call("addContextListener", { listenerId, channelId: this.id, contextType }));
    }
  }
  class HostPrivateChannel extends HostChannel {
    addEventListener(type, handler) {
      return this.agent.listen({ kind: "event", handler }, (listenerId) => this.agent.call("addEventListener", { listenerId, eventType: type, channelId: this.id }));
    }
    disconnect() {
      return this.agent.call("disconnectPrivateChannel", { channelId: this.id });
    }
    // Deprecated synchronous variants: hand back a Listener immediately and
    // finish registering in the background.
    onAddContextListener(handler) {
      return this.syncListener("addContextListener", (e) => handler(e.details?.contextType ?? void 0));
    }
    onUnsubscribe(handler) {
      return this.syncListener("unsubscribe", (e) => handler(e.details?.contextType ?? void 0));
    }
    onDisconnect(handler) {
      return this.syncListener("disconnect", () => handler());
    }
    syncListener(type, handler) {
      const listener = this.addEventListener(type, handler);
      return { unsubscribe: async () => (await listener).unsubscribe() };
    }
  }
  class HostDesktopAgent {
    constructor(transport) {
      __publicField(this, "handlers", /* @__PURE__ */ new Map());
      this.transport = transport;
      transport.onEvent((event) => this.dispatch(event));
    }
    // Plumbing ------------------------------------------------------------------
    /** Calls an fdc3.* host command; rejects with Error(<FDC3 error>) if the host returns `{ error }`. */
    async call(command, ...payload) {
      const result = await this.transport.call(`fdc3.${command}`, payload[0]);
      if (typeof result === "object" && result !== null && !isContext(result) && "error" in result) {
        throw new Error(String(result.error));
      }
      return result;
    }
    /** Registers a handler locally, then with the host; undoes the local half if the host refuses. */
    async listen(handler, register) {
      const listenerId = crypto.randomUUID();
      this.handlers.set(listenerId, handler);
      try {
        await register(listenerId);
      } catch (error) {
        this.handlers.delete(listenerId);
        throw error;
      }
      return {
        unsubscribe: async () => {
          this.handlers.delete(listenerId);
          await this.call("unsubscribe", { listenerId });
        }
      };
    }
    dispatch(event) {
      switch (event.type) {
        case "fdc3.context": {
          const e = event;
          const entry = this.handlers.get(e.listenerId);
          if (entry?.kind !== "context") return;
          entry.handler(e.context, e.source ? { source: this.appIdentifier(e.source) } : void 0);
          return;
        }
        case "fdc3.intent":
          void this.handleIntent(event);
          return;
        case "fdc3.event": {
          const e = event;
          const entry = this.handlers.get(e.listenerId);
          if (entry?.kind !== "event") return;
          entry.handler({ type: e.eventType, details: e.details });
          return;
        }
      }
    }
    /** Runs an intent handler and reports its outcome back so the raiser's getResult() settles. */
    async handleIntent(e) {
      const entry = this.handlers.get(e.listenerId);
      let result;
      try {
        if (entry?.kind !== "intent") throw new Error("No handler");
        result = await entry.handler(e.context, e.source ? { source: this.appIdentifier(e.source) } : void 0);
      } catch (error) {
        await this.call("sendIntentResult", { resultId: e.resultId, context: null, channelId: null, error: String(error) });
        return;
      }
      const outcome = result === void 0 || result === null ? { context: null, channelId: null, error: null } : isChannel(result) ? { context: null, channelId: result.id, error: null } : isContext(result) ? { context: result, channelId: null, error: null } : { context: null, channelId: null, error: "Intent handler returned an invalid result" };
      await this.call("sendIntentResult", { resultId: e.resultId, ...outcome });
    }
    appIdentifier(app) {
      return stripNulls(app);
    }
    channel(info) {
      return info.type === "private" ? new HostPrivateChannel(this, info) : new HostChannel(this, info);
    }
    resolution(info) {
      let result;
      const retrieve = async () => {
        const r = await this.call("getIntentResult", { resultId: info.resultId });
        if (r.channel) return this.channel(r.channel);
        if (r.context) return r.context;
        return void 0;
      };
      return {
        source: this.appIdentifier(info.source),
        intent: info.intent,
        getResult: () => {
          result ?? (result = retrieve());
          return result;
        }
      };
    }
    /** FDC3 1.x passed an app name string where 2.x takes an AppIdentifier. */
    static toAppIdentifier(app) {
      if (app === void 0) return null;
      return typeof app === "string" ? { appId: app, instanceId: null } : { appId: app.appId, instanceId: app.instanceId ?? null };
    }
    // Apps ------------------------------------------------------------------------
    async open(app, context) {
      const opened = await this.call("open", {
        app: HostDesktopAgent.toAppIdentifier(app),
        context: context ?? null
      });
      return this.appIdentifier(opened);
    }
    async findInstances(app) {
      const instances = await this.call("findInstances", { appId: app.appId, instanceId: app.instanceId ?? null });
      return instances.map((i) => this.appIdentifier(i));
    }
    async getAppMetadata(app) {
      return stripNulls(await this.call("getAppMetadata", { appId: app.appId, instanceId: app.instanceId ?? null }));
    }
    async getInfo() {
      return stripNulls(await this.call("getInfo"));
    }
    // Context & channels ------------------------------------------------------------
    broadcast(context) {
      return this.call("broadcast", { channelId: null, context });
    }
    addContextListener(typeOrHandler, maybeHandler) {
      const [contextType, handler] = typeof typeOrHandler === "function" ? [null, typeOrHandler] : [typeOrHandler, maybeHandler];
      return this.listen({ kind: "context", handler }, (listenerId) => this.call("addContextListener", { listenerId, channelId: null, contextType }));
    }
    addEventListener(type, handler) {
      return this.listen({ kind: "event", handler }, (listenerId) => this.call("addEventListener", { listenerId, eventType: type, channelId: null }));
    }
    async getUserChannels() {
      return (await this.call("getUserChannels")).map((c) => this.channel(c));
    }
    /** @deprecated FDC3 1.x name for getUserChannels. */
    getSystemChannels() {
      return this.getUserChannels();
    }
    joinUserChannel(channelId) {
      return this.call("joinUserChannel", { channelId });
    }
    /** @deprecated FDC3 1.x name for joinUserChannel. */
    joinChannel(channelId) {
      return this.joinUserChannel(channelId);
    }
    leaveCurrentChannel() {
      return this.call("leaveCurrentChannel");
    }
    async getCurrentChannel() {
      const info = await this.call("getCurrentChannel");
      return info ? this.channel(info) : null;
    }
    async getOrCreateChannel(channelId) {
      return this.channel(await this.call("getOrCreateChannel", { channelId }));
    }
    async createPrivateChannel() {
      return this.channel(await this.call("createPrivateChannel"));
    }
    // Intents ------------------------------------------------------------------------
    async findIntent(intent, context, resultType) {
      return stripNulls(await this.call("findIntent", { intent, context: context ?? null, resultType: resultType ?? null }));
    }
    async findIntentsByContext(context, resultType) {
      return stripNulls(await this.call("findIntentsByContext", { context, resultType: resultType ?? null }));
    }
    async raiseIntent(intent, context, app) {
      return this.resolution(await this.call("raiseIntent", { intent, context, app: HostDesktopAgent.toAppIdentifier(app) }));
    }
    async raiseIntentForContext(context, app) {
      return this.resolution(await this.call("raiseIntentForContext", { context, app: HostDesktopAgent.toAppIdentifier(app) }));
    }
    addIntentListener(intent, handler) {
      return this.listen({ kind: "intent", handler }, (listenerId) => this.call("addIntentListener", { listenerId, intent }));
    }
  }
  function install() {
    const webview = window.chrome?.webview;
    if (!webview || window.top !== window || window.fdc3) return;
    const pending = /* @__PURE__ */ new Map();
    const eventHandlers = /* @__PURE__ */ new Set();
    webview.addEventListener("message", (e) => {
      const msg = e.data;
      if ("id" in msg) {
        const resolve = pending.get(msg.id);
        if (!resolve) return;
        pending.delete(msg.id);
        resolve(msg.result);
        return;
      }
      if (typeof msg.type === "string" && msg.type.startsWith("fdc3.")) {
        for (const handler of eventHandlers) handler(msg);
      }
    });
    const transport = {
      call: (command, payload) => new Promise((resolve) => {
        const id = crypto.randomUUID();
        pending.set(id, resolve);
        const request = { id, command, payload };
        webview.postMessage(request);
      }),
      onEvent: (handler) => {
        eventHandlers.add(handler);
        return () => eventHandlers.delete(handler);
      }
    };
    window.fdc3 = new HostDesktopAgent(transport);
    window.dispatchEvent(new Event("fdc3Ready"));
  }
  install();
})();
