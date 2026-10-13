import { extensionFetch } from '@cove/runtime/api';

export async function clearMetadata(_action, payload) {
  const selected = payload?.entityIds ?? payload?.selectedIds;
  if (!Array.isArray(selected) || selected.length === 0 || selected.length > 10000 || selected.some(id => !Number.isSafeInteger(id) || id <= 0 || id > 2147483647))
    throw new Error('Select between 1 and 10000 videos with positive IDs.');
  const ids = [...new Set(selected)];
  const response = await extensionFetch('/api/ext/clear-all-metadata/clear', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ entityIds: ids }),
  });
  const result = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(result.message || result.detail || `Cove returned ${response.status}.`);
  return { ...result, suppressToast: true };
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
function onUnload() { observer?.disconnect(); observer = undefined; }
export default { actionHandlers: { clearMetadata }, onLoad, onUnload };
