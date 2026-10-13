# clear-all-metadata v0.1.5

- **Tags → Also clear AI tags** is off by default, including existing installations.
- Preserve tags backed by the AI Tagging extension's exact `ext:ai.tagging` application source, including their visible links and model/run/field provenance.
- Clear scraper/manual evidence even when the tag name appears in an AI model list. Mixed-source tags remain visible only because their AI evidence remains.
- Turning on the child wipes all tags. Unchecking Tags preserves all tags regardless of the child setting.
- Confirmation shows whether AI tags will be kept or cleared.
- “Clear all metadata” action text is red alongside its red eraser icon.

Validation: 10 frontend tests and PostgreSQL integration using Cove's real execution strategy, interceptors, jobs, and tag/provenance entities. Covers scraped-only Boobs, mixed sources, custom AI tags, unrelated-video isolation, parent gating, explicit AI clearing, and existing asset/hash/authorization regressions.

Requires Cove 1.5.1 or newer. Install the attached ZIP through Cove's extension installer, enable the extension, and refresh Cove.
