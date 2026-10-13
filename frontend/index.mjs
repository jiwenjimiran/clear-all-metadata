import { extensionFetch } from '@cove/runtime/api';

export async function clearMetadata(_action, payload) {
  const ids = payload?.entityIds;
  if (!Array.isArray(ids) || ids.length !== 1 || !Number.isInteger(ids[0]) || ids[0] <= 0)
    throw new Error('Select exactly one video.');
  if (!window.confirm('Clear all metadata for this video?\n\nThis removes title, code, details, director, date, studio, performers, tags, URLs, remote IDs, cover art, organized status, and metadata provenance, including manual edits to these fields.\n\nThe video and its files stay in your library.'))
    return { cancelled: true };
  const response = await extensionFetch('/api/ext/clear-all-metadata/clear', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ entityIds: ids }),
  });
  const result = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(result.message || result.detail || `Cove returned ${response.status}.`);
  return result;
}

// Cove currently has no action section declaration. Reorder only our own native
// menu button, within the same parent, after the destructive-section separator.
export function placeMenuAction(doc = document) {
  for (const button of doc.querySelectorAll('button')) {
    if (button.textContent.trim() !== 'Clear all metadata') continue;
    const parent = button.parentElement;
    const deleteButton = [...parent.children].find(el => el.tagName === 'BUTTON' && el.textContent.trim() === 'Delete');
    if (deleteButton && button.nextElementSibling !== deleteButton) parent.insertBefore(button, deleteButton);
  }
}
let observer;
function onLoad() {
  observer?.disconnect();
  observer = new MutationObserver(() => placeMenuAction());
  observer.observe(document.body, { childList: true, subtree: true });
  placeMenuAction();
}
function onUnload() { observer?.disconnect(); observer = undefined; }
export default { actionHandlers: { clearMetadata }, onLoad, onUnload };
