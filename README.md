# clear-all-metadata

Cove extension for resetting a misidentified video immediately, without deleting its library record or rescanning the file.

Open a video's triple-dot **Operations** menu and choose **Clear all metadata**, directly above **Delete** in the same section. This queues a job without a popup. Follow its progress and result in Jobs. It clears title, code, details, director, date and date precision, studio, performers, tags (including derived tag applications), URLs, remote IDs, explicit cover art, organized status, and field provenance. This clears manual edits to those fields too. Run Identify again after the job completes.

The same job regenerates the default cover using Cove's own thumbnail generator, with its default frame timestamp and VR rendering settings. It overwrites the existing generated cover and refreshes the cover URL when finished. An unreadable source or unavailable FFmpeg marks the job as failed with an explanation; metadata already cleared remains cleared.

Keeps the video ID, files, hashes, generated previews/sprites, technical media properties, captions, VR configuration, clips, galleries/groups, custom fields, ratings, bookmarks, and watch history. These are not written by Cove's Identify pipeline. Shared studios, performers and tags are not deleted. Detached cover cleanup uses Cove's blob-reference coordinator after the metadata transaction commits, preserving any covers still referenced elsewhere.

Requires Cove 1.5.1+, video read/write permission, and jobs.run permission. The server checks authorization before queueing and again in the background job, using a fresh extension service scope and the requesting principal. Changes are committed in one database transaction, followed by a VideoUpdated event to refresh the UI. Jobs honor cancellation and extension shutdown.

Version 0.1.2 restores the generated default cover as part of the reset job. Version 0.1.1 fixes the 500 errors in 0.1.0 by wrapping the transaction in Cove's PostgreSQL execution strategy and coordinating cover-reference changes with the host blob interceptor.

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
ffmpeg -v error -y -f lavfi -i 'testsrc2=size=320x180:rate=24:duration=5' -c:v libx264 -pix_fmt yuv420p artifacts/cover-fixture.mp4
dotnet run --project tests/Backend/Backend.csproj -c Release -p:CoveCiVersion=true -- 'Host=127.0.0.1;Port=55441;Username=postgres;Database=clear_metadata_regression' (Get-Command ffmpeg).Source artifacts/cover-fixture.mp4
```

The PostgreSQL integration check enables Cove's actual retry strategy and save interceptors, reproduces both old transaction failures, and checks persisted clearing, provenance removal, cover cleanup, media preservation, repeated clearing, and missing-video handling. It also runs the real host thumbnail service with FFmpeg, verifies that a stale cover is replaced by exactly the default generated image, and checks failure reporting for an unreadable source. PostgreSQL must have pgvector available.

Menu placement uses a scoped DOM reorder because Cove's current action API has no section placement option. If a future UI changes the Delete label or markup, the action remains available in Cove's regular extension action slot.
