export const metadataFields = [
  ['title', 'Title'], ['code', 'Code'], ['details', 'Details'], ['director', 'Director'],
  ['date', 'Date'], ['studio', 'Studio'], ['performers', 'Performers'], ['tags', 'Tags'],
  ['urls', 'URLs'], ['remoteIds', 'Remote IDs'], ['cover', 'Cover image (restore default frame)'], ['organized', 'Organized status'],
];
export const assetFields = [
  ['thumbnails', 'Thumbnails (including generated cover and VR cards)'],
  ['spriteSheets', 'Sprite sheets and VTT indexes'], ['phash', 'pHash fingerprints'],
  ['videoPreviews', 'Video previews (including VR and segment previews)'], ['md5', 'MD5 checksums'],
];
export const defaults = Object.fromEntries([...metadataFields.map(([key]) => [key, true]), ...assetFields.map(([key]) => [key, false]), ['alsoClearAiTags', false]]);
export function normalizedSettings(value) { return Object.fromEntries(Object.entries(defaults).map(([key, fallback]) => [key, typeof value?.[key] === 'boolean' ? value[key] : fallback])); }
export function selectedLabels(settings) {
  return [...metadataFields, ...assetFields].filter(([key]) => settings[key]).map(([key, label]) => key === 'tags'
    ? settings.alsoClearAiTags ? 'Tags (including AI tags)' : 'Non-AI tags'
    : key === 'cover' && settings.thumbnails ? 'Scraped / explicit cover art' : label);
}
export function keptLabels(settings) {
  const kept = [...metadataFields, ...assetFields].filter(([key]) => !settings[key]).map(([key, label]) => key === 'tags' ? 'Tags (including AI tags)'
    : key === 'thumbnails' && settings.cover ? 'Other thumbnails and VR cards' : label);
  if (settings.tags && !settings.alsoClearAiTags) kept.push('AI tags added by AI Tagging');
  return kept;
}
