import { test } from 'node:test';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';
import bundle, { clearMetadata, placeMenuAction } from '../frontend/index.mjs';

test('native action is placed after separator and directly before Delete; unrelated menus stay intact', () => {
  const dom = new JSDOM('<div id="ops"><button>Clear all metadata</button><button>Generate</button><div class="border-t"></div><button>Delete</button></div><div id="other"><button>Delete</button></div>');
  placeMenuAction(dom.window.document);
  const ops = dom.window.document.querySelector('#ops');
  assert.equal(ops.children[1].tagName, 'DIV');
  assert.equal(ops.children[2].textContent, 'Clear all metadata');
  assert.equal(ops.children[3].textContent, 'Delete');
  placeMenuAction(dom.window.document);
  assert.equal(dom.window.document.querySelector('#other').children.length, 1);
});

test('lifecycle handles menus opening later and disconnects on unload', async () => {
  const dom = new JSDOM('<body></body>');
  globalThis.document = dom.window.document;
  globalThis.MutationObserver = dom.window.MutationObserver;
  bundle.onLoad();
  document.body.innerHTML = '<div><button>Clear all metadata</button><hr><button>Delete</button></div>';
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(document.querySelector('hr').nextElementSibling.textContent, 'Clear all metadata');
  bundle.onUnload();
  dom.window.close();
});

test('invalid selection never queues; valid selection queues without popups; failures propagate', async () => {
  let calls = 0;
  globalThis.window = { confirm: () => { throw new Error('Unexpected popup'); }, alert: () => { throw new Error('Unexpected popup'); } };
  globalThis.fetch = async (_url, options) => {
    calls++;
    assert.deepEqual(JSON.parse(options.body), { entityIds: [42] });
    return { ok: true, json: async () => ({ jobId: 'queued-job' }) };
  };
  await assert.rejects(clearMetadata(null, { entityIds: [42, 43] }), /exactly one/);
  assert.equal(calls, 0);
  assert.deepEqual(await clearMetadata(null, { entityIds: [42] }), { jobId: 'queued-job', suppressToast: true });
  assert.equal(calls, 1);
  globalThis.fetch = async () => ({ ok: false, status: 403, json: async () => ({ message: 'Forbidden' }) });
  await assert.rejects(clearMetadata(null, { entityIds: [42] }), /Forbidden/);
});
