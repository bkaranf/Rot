# Validation

Rot 2.1.0 was checked on Windows with .NET SDK 10.0.400 and WebView2 Evergreen.
The following local checks passed on September 5, 2026:

- Release build: zero warnings and errors.
- Native tests: 271 app tests and 8 browser-host tests passed.
- JavaScript tests: 41 passed.
- Repository contract check: 101 source/test files passed.
- Actual portable app startup and repeat-launch Settings forwarding passed with
  one Rot instance. The existing preferences file, including all three saved
  window placements, remained byte-for-byte unchanged after the update.
- Actual desktop Settings showed version 2.1.0 and the compiled source revision.
- Browser Settings checks covered a 390-pixel narrow viewport and a 1440-pixel
  desktop viewport, shortcut capture focus, manual update feedback and overflow.

These are local results. Public CI status is shown in the repository's Actions
tab. Release assets and checksums are attached to the versioned GitHub release.

## Automated coverage

- Settings persistence, corrupt-file recovery, failed writes, concurrent mutations,
  layout restoration and minimum window sizes.
- Stats parsing, five-state classification, process/focus policy and receive-only
  inactivity recovery using isolated local test servers.
- Shortcut validation, native registration conflicts, restoration and capture gates.
- WebView control replacement, generation guards, placement and nonactivation.
  The 11 recovery tests include an owned browser-process failure, failed page
  initialization, missing bridge readiness, bounded timeout and successful retry.
- Instance IPC framing, cancellation, allowlists, cold startup, timeout handling,
  extension permissions and the shared-parser handoff.
- Release metadata, digest mismatch, malicious archives, staged replacement,
  readiness failure, rollback and preference preservation.
- Settings UI behavior and player lifecycle races through JavaScript behavior tests.

Run the commands in CONTRIBUTING.md. Native tests require Windows and WebView2.
Tests use owned windows, temporary data folders and isolated endpoints. They do not
drive or capture a running game, or change the user's live preferences.

## Prior live observations

Development sessions used the Epic Games client with EAC enabled. The local Stats
stream distinguished training initialization, training teardown, online evidence
and a later return to training. A match GUID could still be empty on MatchCreated
while loading an online arena, so that event alone is not sufficient to show Rot.
PacketSendRate=0 disabled the listener on the tested build. See DECISIONS.md for the
resulting conservative rules.

Earlier user-led sessions also checked foreground loss, training transitions,
Settings, window placement and the notification-area icon. Those observations
apply to their tested builds. They do not certify every current feature or future
game version. Raw payloads, personal paths and private recordings are not published.

## Limits

- The new release still needs broader community testing across Windows builds,
  displays, DPI changes, GPU drivers and browser versions.
- No anti-cheat certification is claimed. Rot uses ordinary external windows.
- YouTube playback, ads, sign-in restrictions, embedding failures and network
  availability depend on external services.
- An actual game match is not started or manipulated by automated tests.
- No public user-count, performance guarantee or fabricated test result is claimed.
