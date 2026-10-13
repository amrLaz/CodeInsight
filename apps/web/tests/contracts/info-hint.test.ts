import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import { test } from 'vitest';
import { sourceUrl } from '../test-path';

test('info hint contracts', async () => {
const state = [];
let index = 0;
let x = 360;
const viewport = { innerWidth: 390, innerHeight: 844 };
const exports = {};
const hooks = {
  useId: () => 'information',
  useRef: () => ({ current: { getBoundingClientRect: () => ({ left: x, top: 100, bottom: 128 }) } }),
  useState: (initial) => {
    const slot = index++;
    if (!(slot in state)) state[slot] = initial;
    return [
      state[slot],
      (value) => {
        state[slot] = value;
      },
    ];
  },
};
const source = readFileSync(new URL('./components/common/InfoHint.tsx', sourceUrl), 'utf8');
assert.ok(source.includes("label = 'Information'"), 'The default accessible label follows the English application language');
vm.runInNewContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX } }).outputText, {
  exports,
  window: viewport,
  document: { activeElement: null, body: {} },
  require: (name) => (name === 'react' ? hooks : name === 'react-dom' ? { createPortal: (node) => node } : { jsx: (type, props) => ({ type, props }), jsxs: (type, props) => ({ type, props }) }),
});
const render = () => {
  index = 0;
  return exports.default({ children: 'Useful context' });
};
let tree = render();
assert.equal(tree.props.children[0].props['aria-expanded'], false);
tree.props.onFocus();
tree = render();
assert.equal(tree.props.children[0].props['aria-describedby'], 'information');
assert.equal(tree.props.children[1].props.role, 'tooltip');
for (const width of [390, 768, 1440]) {
  viewport.innerWidth = width;
  for (x of [20, width / 2, width - 30]) {
    render().props.onMouseEnter();
    const left = render().props.children[1].props.style.left;
    assert.ok(left >= 16 && left + Math.min(360, width * 0.75) <= width - 16, 'Tooltip stays inside viewport');
  }
}
let stopped = false;
render().props.onKeyDown({
  key: 'Escape',
  stopPropagation: () => {
    stopped = true;
  },
});
assert.equal(render().props.children[1], false);
assert.ok(stopped);
render().props.children[0].props.onClick();
assert.equal(render().props.children[0].props['aria-expanded'], true);
render().props.onBlur();
assert.equal(render().props.children[1], false);
console.log('Information hint keyboard, click and viewport checks passed.');
});
