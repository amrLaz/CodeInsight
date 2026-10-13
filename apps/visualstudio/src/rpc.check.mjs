import assert from 'node:assert/strict';
import { encode, FrameReader, isNotification, isRequest, parseParams } from '../dist/host.mjs';

const request = { jsonrpc: '2.0', id: 1, method: 'refresh', params: {} };
const framed = encode(request);
assert.match(framed.toString('ascii'), /^Content-Length: \d+\r\n\r\n/);
const reader = new FrameReader();
assert.deepEqual(reader.push(framed.subarray(0, 8)), []);
const messages = reader.push(framed.subarray(8));
assert.equal(messages.length, 1);
assert.equal(isRequest(messages[0]), true);
assert.equal(isNotification(messages[0]), false);
assert.equal(messages[0].method, 'refresh');

const split = encode({ jsonrpc: '2.0', id: 2, method: 'getSnapshot' });
const second = new FrameReader();
assert.deepEqual(second.push(Buffer.concat([framed, split])).map((message) => message.id), [1, 2]);

assert.deepEqual(parseParams([{ cwd: '/tmp' }]), { cwd: '/tmp' });
assert.deepEqual(parseParams({ cwd: '/tmp' }), { cwd: '/tmp' });
assert.deepEqual(parseParams(undefined), {});

console.log('rpc checks passed');
