# clear-all-metadata

Cove extension for resetting a misidentified video immediately, without deleting its library record or rescanning the file.

Open a video's triple-dot **Operations** menu and choose **Clear all metadata**, directly above **Delete** in the same section. A confirmation modal shows exactly which fields will be cleared and kept. Confirm to queue a job; follow its progress and result in Jobs. There is no success alert. By default, it clears title, code, details, director, date and date precision, studio, performers, non-AI tags and applications, URLs, remote IDs, explicit cover art, organized status, and provenance for those fields. AI Tagging tags are kept unless explicitly included. This clears manual edits to the selected fields too.

For multiple videos, select them in a video list and use **Clear all metadata** on the selection actions bar, immediately left of **Delete**. Both actions use red text, a red eraser icon, and the same confirmation modal. One queued job processes the selection sequentially, with a result for each video. A failed video does not prevent the remaining selected videos from being processed. Successful resets are retained; the job reports any failures. Duplicate IDs are processed once, and up to 10,000 videos can be selected.

In **Settings → Extensions → Clear all metadata**, select what gets wiped. Each descriptive field can be turned on or off. Thumbnails, sprite sheets, pHash fingerprints, video previews, and MD5 checksums are **off by default**, so they are preserved. Clearing explicit cover art reveals the existing generated cover without regenerating it. The action no longer runs FFmpeg or requires a readable source video to clear descriptive metadata. The job uses a snapshot of the choices shown in its confirmation modal.

Optional thumbnail wiping removes the generated cover, timestamped thumbnails, and VR cards. Sprite wiping removes the sprite image and VTT index. Preview wiping removes regular, VR, and segment previews. Hash wiping removes only the selected hash type from the selected videos' files; other fingerprints such as oshash are retained. Generated files are removed under Cove's per-video generation lock. Filesystem changes follow the metadata commit, so a file-removal failure is reported in Jobs while committed metadata changes remain applied. To recreate cleared generated assets, run Generate separately.

Under **Tags**, **Also clear AI tags** is **off by default**, including when upgrading existing settings. With Tags checked and this child unchecked, the action clears non-AI tag applications while preserving tags recorded by the AI Tagging extension with the exact source key `ext:ai.tagging`. Their tag links, application model/run evidence, and AI tag field provenance remain. If a tag has both AI and scraper/manual evidence, the non-AI applications are removed while the visible AI-backed tag stays. Turning on the child clears all tag links and applications. Unchecking Tags keeps everything, regardless of the child setting. The confirmation modal states which behavior will run.

Tag names or model membership do not prove origin: a scraped-only **Boobs** tag is cleared even though it appears in `body_20.tags.txt`. This also applies to names in the actions, BDSM, and positions models. Conversely, an AI Tagging tag outside those lists is preserved when its recorded source matches. Evidence is checked on the selected video; an AI application for the same shared tag on another video does not protect it.

Always keeps the video ID, original media files, technical media properties, captions, VR configuration, clips, galleries/groups, custom fields, ratings, bookmarks, and watch history. Shared studios, performers and tags are not deleted. Detached explicit-cover cleanup uses Cove's blob-reference coordinator after the metadata transaction commits, preserving covers still referenced elsewhere.

Requires Cove 1.5.1+, video read/write permission, and jobs.run permission. Saving shared defaults requires system.settings.write; clearing file fingerprints also requires files.write. The server checks authorization before queueing and again in the background job, using a fresh extension service scope and the requesting principal. Database changes are committed in one transaction, followed by optional generated-file cleanup and a VideoUpdated event to refresh the UI. Jobs honor cancellation and extension shutdown.

Version 0.1.4 adds configurable wipes, preservation of generated assets and hashes by default, a confirmation modal, and a red eraser icon. It replaces the automatic cover regeneration from 0.1.2–0.1.3. Version 0.1.3 added bulk clearing. Version 0.1.1 fixed the original transaction errors.
Version 0.1.5 preserves AI Tagging tags by default, adds the opt-in **Also clear AI tags** subsetting, and colors the action text red.

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

The PostgreSQL integration check enables Cove's actual retry strategy and save interceptors. It verifies default asset/hash preservation, isolated optional wipes using the real host generated-path service, selective provenance removal, settings persistence, bulk continuation, cancellation, and permissions. No FFmpeg is needed. PostgreSQL must have pgvector available. Frontend tests cover modal confirmation/cancellation, exact saved settings, inline errors, settings persistence requests, and red icon placement.

Menu and actions-bar placement use a scoped DOM reorder because Cove's current action API has no section placement option. The same hook replaces Cove's generic extension icon with eraser paths, preserving the loading spinner while the action is queueing. If a future UI changes the Delete label or markup, the action remains available in Cove's regular extension action slot.
