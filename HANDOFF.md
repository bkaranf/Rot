# Rot public launch handoff

Status: all five proposals are implemented and local verification has passed. Nothing from this
launch has been pushed publicly yet.

The user authorized all five proposals, a researched GitHub front page and marketing
strategy, then a public repository and downloadable release under bkaranf/Rot.
No external community messages are authorized.

## Current review gate

Lead and orchestrator: native GPT-6 Astra, ultra.
Execution workers: native GPT-5.6 Luna, max.
Independent final reviewer: native GPT-6 Astra, max.

The user replaced the earlier Sol reviewer with Astra max during integration.
The Astra reviewer must inspect the final candidate and pass it before publication.
The existing goal remains active; this reviewer assignment supersedes its original
Sol wording. No custom model routing overrides are used.

## Implemented proposals

1. Serialized Settings transactions preserve committed values on failed saves,
   including size/layout, concurrent placement/resume changes and shutdown.
   Section feedback handles overlapping saves.
2. Receive-only Stats inactivity detection and bounded WebView process recovery.
   Recovery preserves game classification and requires fresh Local/current-process
   evidence, fresh bridge readiness and the existing focus gate. Player callback
   races and pause/mute failures have behavior tests.
3. Editable shortcuts, validation, conflicts, registration rollback, truthful
   availability and active Settings capture. Healthy custom startup bindings survive
   another shortcut's conflict.
4. .NET 10 LTS, real build identity, repeat-launch Settings forwarding and explicit
   staged updates. The old app waits for a prepared candidate before closing. New
   startup readiness gates success; failed startup restores the previous folder.
5. Minimal-permission Send to Rot extension and native messaging. Only a selected
   YouTube address transfers. One pending selection waits for verified training and
   focus. Native messaging registration remains outside the install folder.

Public README, architecture decisions, troubleshooting, privacy, contribution,
license, component notices, issue templates and CI are being completed. The launch
strategy and verified README research are in docs/LAUNCH.md.

## Verification status

The Release build passed with zero warnings and errors. All 271 app tests, 8 browser-host tests and 41 JavaScript tests passed. The repository contract check passed for 101 source/test files. The actual portable app opened Settings on a repeat launch with exactly one Rot instance. The existing preferences file, including all three window placements, remained byte-for-byte unchanged. Narrow and desktop browser checks passed. The final artifact review, public URLs, hashes and CI results will be recorded after publication.

Owned test windows and temporary WebView profiles are used for failure tests.
The user's game is not driven, captured or stopped. No new live game cycle is
active. Prior live observations and their limits are summarized in VALIDATION.md.

## Publication plan

Preserve the local development history and private evidence. Export a clean initial
public main snapshot containing intended source, tests, docs and assets. Exclude
private logs, profiles, machine paths, handoff bundles, binaries and historical
patch series. Build versioned portable artifacts from the public source revision,
then publish only after the independent Astra gate passes.

Target repository: https://github.com/bkaranf/Rot
Target release: v2.1.0
Assets: Rot-win-x64.zip, Send-to-Rot.zip and SHA256SUMS.

Keep the installed portable folder stable and preserve the user's latest preferences
and all three window placements when updating the local running build. Verify one
latest Rot instance, public visibility, downloadable assets, hashes and CI results.
Only then mark the goal complete.