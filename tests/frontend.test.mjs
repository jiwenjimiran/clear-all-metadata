import { test } from 'node:test';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';
import bundle, { clearMetadata, placeMenuAction, ClearMetadataSettings } from '../frontend/index.mjs';
import { defaults } from '../frontend/options.mjs';
import { createElement } from 'react';
import { createRoot } from 'react-dom/client';

function setup() {
  const dom = new JSDOM('<body><button id="trigger">Open</button></body>', { url: 'http://localhost' });
  globalThis.window = dom.window; globalThis.document = dom.window.document;
  globalThis.MutationObserver = dom.window.MutationObserver;
  window.alert = () => { throw new Error('Unexpected success alert'); };
  return dom;
}
async function until(predicate) {
  for (let i = 0; i < 100; i++) { if (predicate()) return; await new Promise(r => setTimeout(r, 10)); }
  throw new Error('UI did not reach expected state.');
}
function button(text) { return [...document.querySelectorAll('button')].find(b => b.textContent === text); }

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

test('invalid selection never queues', async () => {
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
});

test('confirm modal shows exact saved options and queues multiple selected videos only after confirmation', async () => {
  const dom = setup(); let queues = 0;
  const settings = { ...defaults, title: false, phash: true };
  document.querySelector('#trigger').focus();
  globalThis.fetch = async (url, options) => {
    if (url.endsWith('/settings')) return { ok: true, json: async () => settings };
    queues++;
    assert.deepEqual(JSON.parse(options.body), { entityIds: [42, 43, 44], settings });
    return { ok: true, json: async () => ({ jobId: 'bulk-job' }) };
  };
  const result = clearMetadata(null, { selectedIds: [42, 43, 42, 44] });
  await until(() => button('Queue clearing') && !button('Queue clearing').disabled);
  assert.equal(queues, 0);
  assert.match(document.querySelector('[role=dialog]').textContent, /3 selected videos/);
  assert.match(document.querySelector('[role=dialog] ul').textContent, /pHash fingerprints/);
  assert.doesNotMatch(document.querySelector('[role=dialog] ul').textContent, /Title/);
  button('Queue clearing').click();
  assert.deepEqual(await result, { jobId: 'bulk-job', suppressToast: true });
  assert.equal(queues, 1); assert.equal(document.querySelector('[role=dialog]'), null);
  assert.equal(document.activeElement.id, 'trigger'); dom.window.close();
});

test('bulk action sits immediately left of Delete with eraser icon and preserves pending spinner', () => {
  const dom = new JSDOM('<div><button>Identify</button><button>Clear all metadata<svg class="w-3 h-3" viewBox="0 0 24 24"><path d="puzzle"></path></svg></button><button>Other extension</button><button>Delete</button></div>');
  const doc = dom.window.document;
  placeMenuAction(doc);
  const button = [...doc.querySelectorAll('button')].find(b => b.textContent === 'Clear all metadata');
  assert.equal(button.nextElementSibling.textContent, 'Delete');
  assert.equal(button.querySelectorAll('svg path').length, 3);
  assert.equal(button.querySelector('svg').getAttribute('viewBox'), '0 0 24 24');
  assert.equal(button.querySelector('svg').style.color, 'rgb(248, 113, 113)');
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

test('cancel and Escape never queue; keyboard stays in modal; unload closes modal', async () => {
  const dom = setup(); let queues = 0;
  globalThis.fetch = async url => { if (url.endsWith('/clear')) queues++; return { ok: true, json: async () => defaults }; };
  let result = clearMetadata(null, { entityIds: [42] });
  await until(() => button('Queue clearing') && !button('Queue clearing').disabled);
  button('Cancel').focus();
  button('Cancel').dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true, cancelable: true }));
  assert.equal(document.activeElement.textContent, 'Queue clearing');
  button('Cancel').click(); assert.equal((await result).cancelled, true);
  result = clearMetadata(null, { entityIds: [42] });
  await until(() => document.querySelector('[role=dialog]'));
  document.querySelector('[role=dialog]').dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
  assert.equal((await result).cancelled, true);
  result = clearMetadata(null, { entityIds: [42] });
  await until(() => document.querySelector('[role=dialog]'));
  bundle.onUnload(); assert.equal((await result).cancelled, true);
  assert.equal(queues, 0); assert.equal(document.body.style.overflow, ''); dom.window.close();
});

test('queue failure stays in modal as inline error and supports retry', async () => {
  const dom = setup(); let attempts = 0;
  globalThis.fetch = async url => {
    if (url.endsWith('/settings')) return { ok: true, json: async () => defaults };
    attempts++;
    return attempts === 1 ? { ok: false, status: 403, json: async () => ({ message: 'Forbidden' }) } : { ok: true, json: async () => ({ jobId: 'retry' }) };
  };
  const result = clearMetadata(null, { entityIds: [42] });
  await until(() => button('Queue clearing') && !button('Queue clearing').disabled);
  button('Queue clearing').click(); await until(() => document.querySelector('[role=alert]'));
  assert.equal(document.querySelector('[role=alert]').textContent, 'Forbidden');
  button('Queue clearing').click(); assert.equal((await result).jobId, 'retry'); dom.window.close();
});

test('settings default assets to kept and save individual wipe choices', async () => {
  const dom = setup(); let saved;
  globalThis.fetch = async (_url, options) => {
    if (options.method === 'PUT') saved = JSON.parse(options.body);
    return { ok: true, json: async () => saved || defaults };
  };
  const container = document.createElement('div'); document.body.appendChild(container);
  const root = createRoot(container); root.render(createElement(ClearMetadataSettings));
  await until(() => document.querySelectorAll('input').length === 17);
  const checks = [...document.querySelectorAll('input')];
  assert.equal(checks.slice(12).some(input => input.checked), false);
  checks[14].click(); button('Save settings').click();
  await until(() => document.querySelector('[role=status]'));
  assert.equal(saved.phash, true); assert.equal(saved.thumbnails, false); assert.equal(saved.md5, false);
  root.unmount(); dom.window.close();
});
