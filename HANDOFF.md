# Rot public launch handoff

All five proposals are implemented, publicly released and verified. Rot 2.1.0 is
running locally with the user's preferences preserved. GitHub CI is green.

## Review

Lead and orchestrator: native GPT-6 Astra, ultra.
Execution workers: native GPT-5.6 Luna, max.
Independent final reviewer: native GPT-6 Astra, max.

The user replaced the original Sol assignment with Astra max. Astra issued a
publication PASS with no unresolved P0/P1/P2 findings for release commit
`f6a2836c5f5cb5e087644a10966b82fa7e318f0c` and the assets below. The original goal's
reviewer wording is superseded by that user instruction.

## Implemented proposals

1. Settings transactions preserve committed preferences and runtime behavior after
   failed saves, including size/layout and concurrent placement/resume changes.
2. Stats inactivity and WebView process failures recover with bounded retries,
   current bridge readiness and fresh training evidence. Player lifecycle races
   have regression coverage. Existing process and focus gates remain in force.
3. Shortcuts can be edited and reset, with validation, native conflict feedback,
   rollback and active Settings capture. Unknown saved actions are not registered.
4. .NET 10 LTS, real build identity, repeat-launch Settings forwarding and explicit
   staged updates. A prepared candidate and startup readiness gate replacement;
   failed startup restores the previous install. Preferences stay outside it.
5. The optional Send to Rot extension passes a YouTube address through current-user
   native messaging. The selection waits for verified training and focus. Google
   sign-in, cookies and Premium status do not transfer into Rot.

## Public release

- Repository: https://github.com/bkaranf/Rot
- Download and notes: https://github.com/bkaranf/Rot/releases/tag/v2.1.0
- Release source: `f6a2836c5f5cb5e087644a10966b82fa7e318f0c`
- License: MIT, with bundled component notices.
- Default branch: main. Issue templates, contribution guidance and Windows CI are included.

| Asset | SHA-256 |
|---|---|
| Rot-win-x64.zip | c835ead9d2b733fd493ff7608fe217e27fdaf2d3886a32a99c5e7d0368d07bc1 |
| Send-to-Rot.zip | 0f1fbf3199abd89e81a16ec302530454deb828f5d08668fb4daf4c03e77ec5a7 |

Both packages and SHA256SUMS are attached to the release. Anonymous downloads and
the README's latest-download link were verified. The release tag and approved
assets remain unchanged by the subsequent test and documentation corrections.

The public repository began from a clean source snapshot. Private development
history, logs, profiles, recordings, patches and handoff bundles were retained
locally and excluded from publication. Commit identity uses GitHub noreply email.
Do not push the original private history into the public repository.

## Verification

- Local Release build: zero warnings and errors.
- Native tests: 271 app tests and 8 browser-host tests passed.
- JavaScript: 41 tests passed. Repository contract check: 101 files passed.
- Recovery coverage includes browser-process failure, missing page/bridge readiness,
  timeout, repaired page and successful retry using owned temporary WebViews.
- The final release ZIP was extracted and started. One current Rot instance remained;
  a second launch exited successfully and opened the existing desktop Settings.
- The user's complete preferences file, including all three window placements,
  remained byte-for-byte unchanged. The actual app showed the release revision.
- A live update check against the public release returned that Rot was up to date.
- Browser checks covered narrow and desktop Settings, shortcut focus, update feedback
  and overflow. Published README visuals and download links were checked.
- The 523-file portable package contains the app, helpers, web assets and licenses,
  with no PDBs, user profiles, private evidence or private machine-path markers.

Public CI passed on source commit `70e24734d3cb30554413bbcec50a663e1c2390ad`:
https://github.com/bkaranf/Rot/actions/runs/33951150812

The clean runner passed formatting, build, 271 app tests, 8 browser-host tests,
41 JavaScript tests, repository contracts and portable publishing. The test-path
correction selects the executing tests' exact build output; it does not change
the released runtime. Astra also passed that test/documentation follow-up.

See VALIDATION.md for detailed scope and limits. Actual Chrome/Edge extension
installation and broader live-game, display and driver coverage remain manual
validation gaps. No game was driven, captured or stopped by automated checks, and
no new user-led game test was claimed. No anti-cheat certification is claimed.

## Marketing

The researched README structure, positioning, staged marketing strategy and ready
launch copy are in docs/LAUNCH.md. The public front page includes an original
workflow graphic, an actual Settings screenshot, clear setup, features, limitations,
privacy, help and contribution links. Research sources and dated observations are
recorded in the strategy. No external community posts or direct messages were sent.