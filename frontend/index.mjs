import { extensionFetch } from '@cove/runtime/api';
import { createElement as h, useState, useEffect, useRef } from '@cove/runtime/react';
import { createRoot } from '@cove/runtime/react-dom-client';
import { metadataFields, assetFields, normalizedSettings, selectedLabels } from './options.mjs';

async function api(path, options = {}) {
  const response = await extensionFetch('/api/ext/clear-all-metadata' + path, {
    ...options, headers: { 'Content-Type': 'application/json', ...options.headers },
  });
  const result = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(result.message || result.detail || `Cove returned ${response.status}.`);
  return result;
}

export function ClearMetadataSettings() {
  const [settings, setSettings] = useState(null);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    const controller = new AbortController();
    api('/settings', { signal: controller.signal }).then(value => setSettings(normalizedSettings(value)))
      .catch(e => { if (!controller.signal.aborted) setError(e.message); });
    return () => controller.abort();
  }, []);
  async function save(e) {
    e.preventDefault(); setBusy(true); setMessage(''); setError('');
    try { setSettings(normalizedSettings(await api('/settings', { method: 'PUT', body: JSON.stringify(settings) }))); setMessage('Settings saved.'); }
    catch (e) { setError(e.message); }
    finally { setBusy(false); }
  }
  const group = (label, fields) => h('fieldset', null, h('legend', null, label), ...fields.map(([key, title]) =>
    h('label', { key, className: 'clear-option' }, h('input', { type: 'checkbox', checked: settings[key], disabled: busy,
      onChange: e => { setSettings({ ...settings, [key]: e.target.checked }); setMessage(''); } }), title)));
  return h('section', { className: 'clear-metadata-ui' }, h('h2', null, 'Clear all metadata'),
    h('p', null, 'Checked items will be cleared. Unchecked items will be kept. These defaults apply to single-video and bulk actions.'),
    h('p', null, 'Thumbnails, sprite sheets, pHash, video previews, and MD5 checksums are kept by default. Original media files are always kept.'),
    error && h('p', { role: 'alert' }, error),
    settings ? h('form', { onSubmit: save }, group('Descriptive metadata', metadataFields), group('Generated assets and fingerprints', assetFields),
      h('button', { type: 'submit', disabled: busy }, busy ? 'Saving…' : 'Save settings'), message && h('p', { role: 'status' }, message)) : h('p', null, 'Loading settings…'));
}

function ConfirmClear({ ids, finish }) {
  const [settings, setSettings] = useState(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const dialog = useRef(null);
  const cancel = useRef(null);
  const queueing = useRef(false);
  useEffect(() => {
    const controller = new AbortController();
    cancel.current?.focus();
    api('/settings', { signal: controller.signal }).then(value => setSettings(normalizedSettings(value)))
      .catch(e => { if (!controller.signal.aborted) setError(e.message); });
    return () => controller.abort();
  }, []);
  async function queue() {
    if (queueing.current) return;
    queueing.current = true;
    setBusy(true); setError('');
    try { finish({ ...await api('/clear', { method: 'POST', body: JSON.stringify({ entityIds: ids, settings }) }), suppressToast: true }); }
    catch (e) { queueing.current = false; setError(e.message); setBusy(false); }
  }
  function keyboard(e) {
    if (e.key === 'Escape' && !busy) { e.preventDefault(); finish({ cancelled: true, suppressToast: true }); }
    if (e.key === 'Tab') {
      const elements = [...dialog.current.querySelectorAll('button:not(:disabled)')];
      const first = elements[0], last = elements.at(-1);
      if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last?.focus(); }
      if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first?.focus(); }
    }
  }
  const clear = settings ? selectedLabels(settings) : [];
  const keep = settings ? selectedLabels(Object.fromEntries(Object.entries(settings).map(([key, value]) => [key, !value]))) : [];
  return h('div', { className: 'clear-backdrop' }, h('section', { className: 'clear-metadata-ui clear-dialog', role: 'dialog', 'aria-modal': true,
    'aria-labelledby': 'clear-confirm-title', ref: dialog, onKeyDown: keyboard },
    h('h2', { id: 'clear-confirm-title' }, 'Clear selected metadata?'),
    h('p', null, `${ids.length} selected video${ids.length === 1 ? '' : 's'}. This clears manual edits to the checked metadata fields too.`),
    error && h('p', { role: 'alert' }, error),
    settings ? h('div', null, h('h3', null, 'Will clear'), clear.length ? h('ul', null, ...clear.map(label => h('li', { key: label }, label))) : h('p', null, 'Nothing is selected for clearing.'),
      h('h3', null, 'Will keep'), h('p', null, ['Original video files', ...keep].join(', ')),
      h('p', null, 'Change these choices in Settings → Extensions → Clear all metadata.')) : h('p', null, 'Loading saved settings…'),
    h('div', { className: 'clear-buttons' }, h('button', { type: 'button', ref: cancel, disabled: busy,
      onClick: () => finish({ cancelled: true, suppressToast: true }) }, 'Cancel'),
      h('button', { type: 'button', className: 'clear-danger', disabled: busy || !settings || !clear.length, onClick: queue }, busy ? 'Queueing…' : 'Queue clearing'))));
}

let activeDialog;

export async function clearMetadata(_action, payload) {
  const selected = payload?.entityIds ?? payload?.selectedIds;
  if (!Array.isArray(selected) || selected.length === 0 || selected.length > 10000 || selected.some(id => !Number.isSafeInteger(id) || id <= 0 || id > 2147483647))
    throw new Error('Select between 1 and 10000 videos with positive IDs.');
  const ids = [...new Set(selected)];
  if (activeDialog) return activeDialog.promise;
  const previousFocus = document.activeElement;
  const previousOverflow = document.body.style.overflow;
  const container = document.createElement('div'); document.body.appendChild(container);
  const root = createRoot(container);
  document.body.style.overflow = 'hidden';
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  const finish = result => {
    root.unmount(); container.remove(); document.body.style.overflow = previousOverflow;
    activeDialog = undefined; previousFocus?.focus?.(); resolve(result);
  };
  activeDialog = { promise, finish };
  root.render(h(ConfirmClear, { ids, finish }));
  return promise;
}

// Cove currently has no action section declaration. Reorder only our own native
// menu button, within the same parent, after the destructive-section separator.
export function placeMenuAction(doc = document) {
  for (const button of doc.querySelectorAll('button')) {
    if (button.textContent.trim() !== 'Clear all metadata') continue;
    const parent = button.parentElement;
    const deleteButton = [...parent.children].find(el => el.tagName === 'BUTTON' && el.textContent.trim() === 'Delete');
    if (deleteButton && button.nextElementSibling !== deleteButton) parent.insertBefore(button, deleteButton);
    // Cove currently renders a generic puzzle icon for extension actions even
    // when a manifest supplies Icon. Use Lucide's eraser paths on our button.
    const svg = button.querySelector('svg');
    if (svg && !svg.classList.contains('animate-spin')) {
      const eraser = '<path d="m7 21-4.3-4.3c-1-1-1-2.5 0-3.4l9.6-9.6c1-1 2.5-1 3.4 0l5.6 5.6c1 1 1 2.5 0 3.4L13 21"></path><path d="M22 21H7"></path><path d="m5 11 9 9"></path>';
      if (svg.innerHTML !== eraser) svg.innerHTML = eraser;
      svg.setAttribute('aria-hidden', 'true');
      svg.style.color = '#f87171';
    }
  }
}
let observer;
function onLoad() {
  observer?.disconnect();
  observer = new MutationObserver(() => placeMenuAction());
  observer.observe(document.body, { childList: true, subtree: true });
  placeMenuAction();
}
function onUnload() {
  observer?.disconnect(); observer = undefined;
  activeDialog?.finish({ cancelled: true, suppressToast: true });
}
export default { components: { ClearMetadataSettings }, actionHandlers: { clearMetadata }, onLoad, onUnload };
