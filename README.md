# clear-all-metadata

Cove extension for resetting a misidentified video immediately, without deleting its library record or rescanning the file.

Open a video's triple-dot **Operations** menu and choose **Clear all metadata**, directly above **Delete** in the same section. Confirm to clear title, code, details, director, date and date precision, studio, performers, tags (including derived tag applications), URLs, remote IDs, explicit cover art, organized status, and field provenance. This clears manual edits to those fields too. Run Identify again when ready.

Keeps the video ID, files, hashes, generated thumbnails/previews, technical media properties, captions, VR configuration, clips, galleries/groups, custom fields, ratings, bookmarks, and watch history. These are not written by Cove's Identify pipeline. Shared studios, performers and tags are not deleted. Removed cover blobs are left for Cove's normal orphan cleanup.

Requires Cove 1.5.1+ and video read/write permission. The server checks both global and per-video authorization. Changes are committed in one database transaction, followed by a VideoUpdated event and UI query refresh.

## Install

Install the release ZIP using Cove's extension installer, then enable the extension.

## Build

Requires .NET 10 and a Cove source checkout beside this repository (or supply `-CoveSourceRoot`).

```powershell
./scripts/package.ps1
```

The package contains only the extension assembly, its manifests, and frontend assets; Cove assemblies are supplied by the host.

Menu placement uses a scoped DOM reorder because Cove's current action API has no section placement option. If a future UI changes the Delete label or markup, the action remains available in Cove's regular extension action slot.
