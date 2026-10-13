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
  for (const ids of [[], [0], [-1], [1.5], ['42'], [2147483648], Array(10001).fill(1)])
    await assert.rejects(clearMetadata(null, { entityIds: ids }), /between 1 and 10000/);
  assert.equal(calls, 0);
  assert.deepEqual(await clearMetadata(null, { entityIds: [42] }), { jobId: 'queued-job', suppressToast: true });
  assert.equal(calls, 1);
  globalThis.fetch = async () => ({ ok: false, status: 403, json: async () => ({ message: 'Forbidden' }) });
  await assert.rejects(clearMetadata(null, { entityIds: [42] }), /Forbidden/);
});

test('multiple selected videos queue together, deduplicate IDs and suppress popups', async () => {
  globalThis.fetch = async (_url, options) => {
    assert.deepEqual(JSON.parse(options.body), { entityIds: [42, 43, 44] });
    return { ok: true, json: async () => ({ jobId: 'bulk-job' }) };
  };
  assert.deepEqual(await clearMetadata(null, { selectedIds: [42, 43, 42, 44] }), { jobId: 'bulk-job', suppressToast: true });
});

test('bulk action sits immediately left of Delete with eraser icon and preserves pending spinner', () => {
  const dom = new JSDOM('<div><button>Identify</button><button>Clear all metadata<svg class="w-3 h-3" viewBox="0 0 24 24"><path d="puzzle"></path></svg></button><button>Other extension</button><button>Delete</button></div>');
  const doc = dom.window.document;
  placeMenuAction(doc);
  const button = [...doc.querySelectorAll('button')].find(b => b.textContent === 'Clear all metadata');
  assert.equal(button.nextElementSibling.textContent, 'Delete');
  assert.equal(button.querySelectorAll('svg path').length, 3);
  assert.equal(button.querySelector('svg').getAttribute('viewBox'), '0 0 24 24');
  placeMenuAction(doc);
  button.querySelector('svg').classList.add('animate-spin');
  button.querySelector('svg').innerHTML = '<circle cx="12" cy="12" r="10"></circle>';
  placeMenuAction(doc);
  assert.ok(button.querySelector('svg circle'));
  button.querySelector('svg').classList.remove('animate-spin');
  placeMenuAction(doc);
  assert.equal(button.querySelectorAll('svg path').length, 3);
  dom.window.close();
});
