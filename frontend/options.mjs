export const metadataFields = [
  ['title', 'Title'], ['code', 'Code'], ['details', 'Details'], ['director', 'Director'],
  ['date', 'Date'], ['studio', 'Studio'], ['performers', 'Performers'], ['tags', 'Tags'],
  ['urls', 'URLs'], ['remoteIds', 'Remote IDs'], ['cover', 'Scraped / explicit cover art'], ['organized', 'Organized status'],
];
export const assetFields = [
  ['thumbnails', 'Thumbnails (including generated cover and VR cards)'],
  ['spriteSheets', 'Sprite sheets and VTT indexes'], ['phash', 'pHash fingerprints'],
  ['videoPreviews', 'Video previews (including VR and segment previews)'], ['md5', 'MD5 checksums'],
];
export const defaults = Object.fromEntries([...metadataFields.map(([key]) => [key, true]), ...assetFields.map(([key]) => [key, false])]);
export function normalizedSettings(value) { return Object.fromEntries(Object.entries(defaults).map(([key, fallback]) => [key, typeof value?.[key] === 'boolean' ? value[key] : fallback])); }
export function selectedLabels(settings) { return [...metadataFields, ...assetFields].filter(([key]) => settings[key]).map(([, label]) => label); }
