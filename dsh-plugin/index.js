// Vibe Notch bridge: forwards DSH session/tool/approval/question events to the
// Vibe Notch island over its named pipe (`\\.\pipe\vibe-notch`).
//
// Event mapping (DSH -> island):
//   session/created        -> SessionStart
//   session/disposed       -> SessionEnd
//   agent/status(running)  -> UserPromptSubmit
//   agent/turn-stopping    -> Stop
//   tools/execute          -> PreToolUse (before) + PostToolUse (after)
//   approval/request       -> PermissionRequest (interactive; island decides)
//   user-questions/request -> AskUserQuestion (interactive; island answers)
//   subagent/start|end     -> SubagentStart | SubagentStop
//
// Interactive waterfalls only answer when the island replies allow/deny in
// time; otherwise they call next() and DSH's own askers take over.
import net from 'node:net';
import fs from 'node:fs';
import path from 'node:path';

const PIPE_PATH = '\\\\.\\pipe\\vibe-notch';
const CONNECT_TIMEOUT_MS = 150;
const INTERACTIVE_TIMEOUT_MS = 90_000;

let reqSeq = 0;

function log(message) {
  try {
    const dir = path.join(
      process.env.APPDATA ?? process.env.USERPROFILE ?? '.',
      'VibeNotch', 'logs');
    fs.mkdirSync(dir, { recursive: true });
    const file = path.join(dir, 'dsh-bridge.log');
    const info = fs.existsSync(file) ? fs.statSync(file) : null;
    if (info && info.size > 512 * 1024) fs.rmSync(file, { force: true });
    fs.appendFileSync(file, `${new Date().toISOString().slice(11, 23)} ${message}\n`);
  } catch {
    // logging must never break the harness
  }
}

function brief(value, limit = 600) {
  try {
    const text = JSON.stringify(value);
    if (text === undefined) return 'undefined';
    return text.length > limit ? text.slice(0, limit) + '…' : text;
  } catch {
    return '[unserializable]';
  }
}

function service(ctx, name) {
  try {
    const svc = ctx.get?.(name);
    return svc ?? undefined;
  } catch {
    return undefined;
  }
}

// Pull a session id out of the many shapes a scoped event may offer.
function sessionIdOf(scope, ...candidates) {
  for (const c of candidates) {
    const id = c?.sessionId ?? c?.session?.id ?? c?.session?.sessionId
      ?? c?.agent?.sessionId ?? c?.agent?.session?.id
      ?? c?.childSessionId ?? c?.request?.sessionId;
    if (typeof id === 'string' && id.length > 0) return id;
  }
  const scopeId = scope?.agent?.sessionId ?? scope?.session?.id ?? scope?.sessionId;
  return typeof scopeId === 'string' && scopeId.length > 0 ? scopeId : undefined;
}

function cwdOf(ctx, session) {
  try {
    const wd = service(ctx, 'workingDirectory');
    const cwd = wd?.get?.(session);
    return typeof cwd === 'string' && cwd.length > 0 ? cwd : undefined;
  } catch {
    return undefined;
  }
}

function webuiOf(ctx) {
  try {
    return service(ctx, 'webApp')?.getAdvertisedUrl?.();
  } catch {
    return undefined;
  }
}

function envelope(ctx, sessionId, extra) {
  return {
    source: 'dsh',
    session_id: sessionId,
    webui: webuiOf(ctx),
    at: Date.now(),
    ...extra,
  };
}

function send(evt) {
  const line = JSON.stringify(evt);
  log(`send ${evt.event} session=${evt.session_id ?? '-'}`);
  const socket = net.connect(PIPE_PATH);
  socket.setTimeout(CONNECT_TIMEOUT_MS);
  const done = () => socket.destroy();
  socket.on('connect', () => {
    socket.setTimeout(0);
    socket.write(line + '\n', () => socket.end());
  });
  socket.on('timeout', done);
  socket.on('error', () => {
    log(`pipe send failed (${evt.event}): island not running`);
    done();
  });
  socket.on('close', () => {});
}

// One connection per interactive request; the island answers with one line.
function sendInteractive(ctx, sessionId, extra) {
  const requestId = `dsh${Date.now().toString(36)}${(reqSeq++).toString(36)}`;
  const evt = envelope(ctx, sessionId, { ...extra, request_id: requestId });
  log(`interactive -> ${evt.event} tool=${evt.tool_name ?? '-'} req=${requestId}`);
  return new Promise((resolve) => {
    let settled = false;
    let buffer = '';
    const finish = (value, why) => {
      if (settled) return;
      settled = true;
      log(`interactive <- ${evt.event} req=${requestId} ${why}`);
      try { socket.destroy(); } catch { /* already gone */ }
      resolve(value);
    };

    const socket = net.connect(PIPE_PATH);
    socket.setTimeout(CONNECT_TIMEOUT_MS);
    socket.on('connect', () => {
      socket.setTimeout(INTERACTIVE_TIMEOUT_MS);
      socket.write(JSON.stringify(evt) + '\n');
    });
    socket.on('data', (chunk) => {
      buffer += chunk.toString('utf8');
      const newline = buffer.indexOf('\n');
      if (newline < 0) return;
      const line = buffer.slice(0, newline).trim();
      try {
        const parsed = JSON.parse(line);
        finish(parsed, `decision=${parsed?.decision ?? 'invalid'}`);
      } catch {
        finish(null, 'unparseable response');
      }
    });
    socket.on('timeout', () => finish(null, socket.readyState === 'opening' ? 'island not running' : 'island timeout'));
    socket.on('error', () => finish(null, 'island not running'));
  });
}

export function apply(ctx) {
  const disposers = [];
  const on = (event, handler) => {
    try {
      const disposer = ctx.on(event, handler);
      if (typeof disposer === 'function') disposers.push(disposer);
    } catch (error) {
      log(`register ${event} failed: ${error?.message ?? error}`);
    }
  };

  log('bridge active');

  on('session/created', function (session) {
    const id = session?.id ?? session?.sessionId;
    if (!id) return;
    send(envelope(ctx, id, {
      event: 'SessionStart',
      cwd: cwdOf(ctx, session),
    }));
  });

  on('session/disposed', function (session) {
    const id = session?.id ?? session?.sessionId;
    if (!id) return;
    send(envelope(ctx, id, { event: 'SessionEnd' }));
  });

  on('agent/status', function (payload) {
    if (payload?.status !== 'running') return;
    const id = sessionIdOf(this, payload);
    if (!id) return;
    send(envelope(ctx, id, { event: 'UserPromptSubmit' }));
  });

  on('agent/turn-stopping', function (payload) {
    const id = sessionIdOf(this, payload);
    if (!id) return;
    send(envelope(ctx, id, { event: 'Stop' }));
  });

  on('tools/execute', async function (exec, next) {
    const id = sessionIdOf(this, exec);
    const toolName = exec?.name ?? exec?.toolName ?? exec?.tool?.name;
    const toolInput = exec?.input ?? exec?.toolInput ?? exec?.args;
    const toolUseId = exec?.callId ?? exec?.id ?? exec?.toolUseId;
    if (id) {
      send(envelope(ctx, id, {
        event: 'PreToolUse',
        tool_name: toolName,
        tool_input: toolInput,
        tool_use_id: toolUseId,
      }));
    }
    const result = await next();
    if (id) {
      send(envelope(ctx, id, {
        event: 'PostToolUse',
        tool_name: toolName,
        tool_use_id: toolUseId,
      }));
    }
    return result;
  });

  on('approval/request', async function (req, next) {
    const id = sessionIdOf(this, req);
    log(`approval/request id=${id} shape=${brief(req)}`);
    const response = await sendInteractive(ctx, id, {
      event: 'PermissionRequest',
      tool_name: req?.toolName ?? req?.tool_name ?? req?.tool?.name,
      tool_input: req?.input ?? req?.toolInput ?? req?.tool_input,
      tool_use_id: req?.callId ?? req?.toolCallId ?? req?.tool_use_id,
    });
    if (response?.decision === 'allow') {
      return { kind: 'allow' };
    }
    if (response?.decision === 'deny') {
      return { kind: 'deny', reason: response.reason || 'Denied from Vibe Notch' };
    }
    // Island timeout / not running / explicit ask -> DSH's own answerers.
    const outcome = await next();
    log(`approval pass-through outcome shape=${brief(outcome, 300)}`);
    return outcome;
  });

  on('user-questions/request', async function (req, next) {
    const id = sessionIdOf(this, req);
    log(`user-questions/request id=${id} shape=${brief(req)}`);
    const questions = req?.questions ?? req?.request?.questions;
    const response = await sendInteractive(ctx, id, {
      event: 'AskUserQuestion',
      tool_name: 'AskUserQuestion',
      tool_input: { questions },
      tool_use_id: req?.callId ?? req?.request?.callId,
    });
    const answers = response?.updatedInput?.answers;
    if (response?.decision === 'allow' && answers && typeof answers === 'object') {
      log(`question answered from island: ${brief(answers, 300)}`);
      return { answers };
    }
    return await next();
  });

  on('subagent/start', function (info) {
    const id = info?.childSessionId ?? info?.sessionId ?? info?.child?.id ?? info?.session?.id;
    if (!id) return;
    send(envelope(ctx, id, { event: 'SubagentStart' }));
  });

  on('subagent/end', function (info) {
    const id = info?.childSessionId ?? info?.sessionId ?? info?.child?.id ?? info?.session?.id;
    if (!id) return;
    send(envelope(ctx, id, { event: 'SubagentStop' }));
  });

  return () => {
    for (const disposer of disposers) {
      try { disposer(); } catch { /* ignore */ }
    }
    log('bridge stopped');
  };
}
