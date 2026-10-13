import { realpathSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { Session } from './session';
import { encode, FrameReader, isNotification, isRequest, parseParams, type RpcMessage } from './rpc';
import { METHODS, VERSION, type HostSettings, type InitializeParams, type Method } from './protocol';

export { encode, FrameReader, isNotification, isRequest, parseParams } from './rpc';
export { METHODS, VERSION, defaultSettings } from './protocol';
export { Session } from './session';

/**
 * stdio JSON-RPC host. stdout is the framed protocol only: a stray console.log
 * would desynchronize Visual Studio and the host would look idle.
 */

const session = new Session((level, message) => {
  notify(level === 'warn' ? 'logWarn' : 'logInfo', { message });
});

const handlers: Record<Method, (params: unknown) => Promise<unknown>> = {
  initialize: (params) => session.initialize(params as InitializeParams),
  setSettings: (params) => session.setSettings(params as HostSettings),
  setToken: (params) => {
    const token = (params as { token?: string }).token;
    return session.setToken(typeof token === 'string' ? token : undefined);
  },
  setCwd: (params) => session.setCwd((params as { cwd: string }).cwd),
  refresh: () => session.refresh(),
  getSnapshot: () => Promise.resolve(session.snapshot()),
  filterIssues: (params) => session.filterIssues(params as Parameters<Session['filterIssues']>[0]),
  clearIssueFilters: () => session.clearIssueFilters(),
  filterRules: (params) => session.filterRules(params as Parameters<Session['filterRules']>[0]),
  clearRuleFilters: () => session.clearRuleFilters(),
  selectIssue: (params) => session.selectIssue((params as { issueId: number }).issueId),
  analyzeChanges: () => session.analyzeChanges(),
  triage: (params) => session.triage(params as Parameters<Session['triage']>[0]),
  prompt: (params) => session.prompt((params as { issueId: number }).issueId),
  fileContext: (params) => session.fileContext((params as { path: string }).path),
  hubUrl: (params) => {
    const body = params as { kind?: 'report' | 'issue' | 'rule'; id?: number | string };
    return session.hubLink(body.kind ?? 'report', body.id);
  },
  installHook: () => session.installHook(),
  shutdown: async () => {
    process.exit(0);
  },
};

function notify(method: string, params: unknown): void {
  process.stdout.write(encode({ jsonrpc: '2.0', method, params }));
}

async function dispatch(message: RpcMessage): Promise<void> {
  if (isNotification(message)) {
    if (message.method === 'shutdown') process.exit(0);
    return;
  }
  if (!isRequest(message)) return;
  const method = message.method as Method;
  const handler = handlers[method];
  if (handler === undefined || !METHODS.includes(method)) {
    process.stdout.write(
      encode({ jsonrpc: '2.0', id: message.id, error: { code: -32601, message: `méthode inconnue : ${message.method}` } }),
    );
    return;
  }
  try {
    const result = await handler(parseParams(message.params));
    if (session.tookUnauthorized()) notify('unauthorized', {});
    process.stdout.write(encode({ jsonrpc: '2.0', id: message.id, result }));
  } catch (error) {
    const text = error instanceof Error ? error.message : String(error);
    process.stderr.write(`codeinsights-host: ${text}\n`);
    process.stdout.write(encode({ jsonrpc: '2.0', id: message.id, error: { code: -32000, message: text } }));
  }
}

export function listen(input: NodeJS.ReadableStream = process.stdin, _output: NodeJS.WritableStream = process.stdout): void {
  const reader = new FrameReader();
  input.on('data', (chunk: Buffer | string) => {
    const buffer = typeof chunk === 'string' ? Buffer.from(chunk, 'utf8') : chunk;
    let messages: RpcMessage[];
    try {
      messages = reader.push(buffer);
    } catch (error) {
      process.stderr.write(`codeinsights-host: ${error instanceof Error ? error.message : String(error)}\n`);
      return;
    }
    for (const message of messages) void dispatch(message);
  });
  input.on('end', () => process.exit(0));
}

function launched(): boolean {
  const entry = process.argv[1];
  if (entry === undefined) return false;
  try {
    return realpathSync(entry) === fileURLToPath(import.meta.url);
  } catch {
    return false;
  }
}

if (launched()) {
  process.stderr.write(`codeinsights-host ${VERSION}\n`);
  listen();
}
