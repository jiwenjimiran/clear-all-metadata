# clear-all-metadata

Cove extension for resetting a misidentified video immediately, without deleting its library record or rescanning the file.

Open a video's triple-dot **Operations** menu and choose **Clear all metadata**, directly above **Delete** in the same section. This queues a job without a popup. Follow its progress and result in Jobs. It clears title, code, details, director, date and date precision, studio, performers, tags (including derived tag applications), URLs, remote IDs, explicit cover art, organized status, and field provenance. This clears manual edits to those fields too. Run Identify again after the job completes.

Keeps the video ID, files, hashes, generated thumbnails/previews, technical media properties, captions, VR configuration, clips, galleries/groups, custom fields, ratings, bookmarks, and watch history. These are not written by Cove's Identify pipeline. Shared studios, performers and tags are not deleted. Detached cover cleanup uses Cove's blob-reference coordinator after the metadata transaction commits, preserving any covers still referenced elsewhere.

Requires Cove 1.5.1+, video read/write permission, and jobs.run permission. The server checks authorization before queueing and again in the background job, using a fresh extension service scope and the requesting principal. Changes are committed in one database transaction, followed by a VideoUpdated event to refresh the UI. Jobs honor cancellation and extension shutdown.

Version 0.1.1 fixes the 500 errors in 0.1.0 by wrapping the transaction in Cove's PostgreSQL execution strategy and coordinating cover-reference changes with the host blob interceptor.

## Install

Install the release ZIP using Cove's extension installer, then enable the extension.

## Build

Requires .NET 10 and a Cove source checkout beside this repository (or supply `-CoveSourceRoot`).

```powershell
./scripts/package.ps1
```

The package contains only the extension assembly, its manifests, and frontend assets; Cove assemblies are supplied by the host.

## Tests

```powershell
npm ci
npm test
dotnet run --project tests/Backend/Backend.csproj -c Release -p:CoveCiVersion=true
# Use a fresh disposable test database, never a library database:
dotnet run --project tests/Backend/Backend.csproj -c Release -p:CoveCiVersion=true -- 'Host=127.0.0.1;Port=55441;Username=postgres;Database=clear_metadata_regression'
```

The PostgreSQL integration check enables Cove's actual retry strategy and save interceptors, reproduces both old transaction failures, and checks persisted clearing, provenance removal, cover cleanup, media preservation, repeated clearing, and missing-video handling. PostgreSQL must have pgvector available.

Menu placement uses a scoped DOM reorder because Cove's current action API has no section placement option. If a future UI changes the Delete label or markup, the action remains available in Cove's regular extension action slot.
