/**
 * JSON-RPC 2.0 with LSP Content-Length framing — the same wire format StreamJsonRpc
 * uses by default on the Visual Studio side. A stray write to stdout would desync
 * the stream, so every diagnostic goes to stderr.
 */

export type RpcRequest = { jsonrpc: '2.0'; id: number | string; method: string; params?: unknown };
export type RpcNotification = { jsonrpc: '2.0'; method: string; params?: unknown };
export type RpcSuccess = { jsonrpc: '2.0'; id: number | string; result: unknown };
export type RpcFailure = { jsonrpc: '2.0'; id: number | string; error: { code: number; message: string } };
export type RpcMessage = RpcRequest | RpcNotification | RpcSuccess | RpcFailure;

export function encode(message: RpcMessage): Buffer {
  const body = Buffer.from(JSON.stringify(message), 'utf8');
  const header = Buffer.from(`Content-Length: ${body.length}\r\n\r\n`, 'ascii');
  return Buffer.concat([header, body]);
}

export function parseParams(params: unknown): unknown {
  if (Array.isArray(params)) return params[0];
  return params ?? {};
}

/**
 * Incremental reader for framed messages. Incomplete frames stay buffered;
 * a malformed header is refused rather than guessed.
 */
export class FrameReader {
  private buffer = Buffer.alloc(0);

  push(chunk: Buffer): RpcMessage[] {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    const messages: RpcMessage[] = [];
    while (true) {
      const message = this.next();
      if (message === undefined) break;
      messages.push(message);
    }
    return messages;
  }

  private next(): RpcMessage | undefined {
    const separator = this.buffer.indexOf('\r\n\r\n');
    if (separator < 0) return undefined;
    const header = this.buffer.subarray(0, separator).toString('ascii');
    const match = /^Content-Length:\s*(\d+)\s*$/im.exec(header);
    if (match === null || match[1] === undefined) throw new Error(`en-tête JSON-RPC illisible : ${header}`);
    const length = Number(match[1]);
    const start = separator + 4;
    if (this.buffer.length < start + length) return undefined;
    const body = this.buffer.subarray(start, start + length).toString('utf8');
    this.buffer = this.buffer.subarray(start + length);
    return JSON.parse(body) as RpcMessage;
  }
}

export function isRequest(message: RpcMessage): message is RpcRequest {
  return 'method' in message && 'id' in message;
}

export function isNotification(message: RpcMessage): message is RpcNotification {
  return 'method' in message && !('id' in message);
}
