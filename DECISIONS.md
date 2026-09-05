# Architecture decisions

Rot is a Windows desktop YouTube player for Rocket League training. These are the
current implementation contracts. Historical local test logs are kept outside the
public repository because game payloads can contain player information.

## Desktop and game boundaries

- Three ordinary WPF windows: Player, Browse and Settings. Standard child-window
  WebView2 controls render the owned UI and YouTube pages.
- Player never activates or takes keyboard focus. Browse and Settings can accept
  input. Desktop Settings is available from the notification area or repeat launch
  even while Rocket League is closed.
- The game integration receives the local Stats API stream. It does not send game
  commands, inject code, inspect game memory, capture the game or simulate input.
- Exact process identity, process start time, detection epoch and current foreground
  ownership gate presentation. Rot-owned focus can preserve an existing game lease
  but cannot create a new one. External focus revokes the lease.
- On uncertainty, core mute and pause dispatch precede Player hiding and auxiliary
  cleanup. Delayed work rechecks its captured epochs before acting.

## Lifecycle evidence

The five states are Disconnected, ConnectedIdle, Local, Transition and Online.
A socket connection alone does not establish training. A populated match GUID is
online evidence. Empty MatchInitialized or RoundStarted establish local training.
Two consecutive empty UpdateState messages can hydrate a late connection in idle;
that fallback is disabled during Transition. Empty MatchCreated alone is ambiguous.
Training MatchDestroyed enters Transition immediately. MatchEnded and PodiumStart
do not restore playback. These rules reflect observed game sessions, not a promise
about every future Rocket League update.

Rot repairs only PacketSendRate=1, Port=0 and WebPort=49124 in the effective
per-user TAStatsAPI.ini, preserving other content and creating a backup. A repair
while the game runs requires a full game restart. Documents uses the Windows known
folder so redirected folders work. Borderless or Windowed mode is required.

The Stats client reconnects with bounded backoff. Once valid data has arrived,
five seconds without another valid event closes the stale connection. Quiet
startup is not misclassified as a stalled active stream. No WebSocket pings or
application messages are sent.

## Player, Browse and recovery

Local files map to https://rot.local. YouTube receives the corresponding embed
origin. The shared JavaScript parser validates all media, including native Browse
selections and external browser handoffs. Correlation IDs and operation generations
reject stale parse results.

YouTube uses its official iframe API, a bounded alternative-host retry, and a
controls-only fallback. Native core mute remains the audio boundary when script
commands fail. Desired playback and current media generation prevent late callbacks
from undoing newer lifecycle decisions.

Browse is a top-level YouTube page with native navigation controls. It is always
core-muted, rejects downloads and permissions, limits navigation, and never injects
scripts into YouTube. Hiding attempts bounded suspension and handles late completion.

Failed WebView surfaces are replaced in the existing WPF windows. Browser-process
failure resets all affected controls before recreating the shared environment.
Recovery invalidates old bridge sessions, commands and readiness. Playback requires
a fresh Player bridge plus fresh local evidence and the existing game focus rules.
Online or Transition classification is not erased merely by a browser failure.

## Preferences and shortcuts

Changes are serialized and persisted before runtime effects. Failed writes retain
committed values. Debounced placement and resume changes cannot write a stale
candidate over a rollback. Writes flush a unique temporary sibling and replace the
main file while preserving a previous snapshot. Corrupt input is preserved before
recovery. Initial window geometry settles before placement notifications can save it.

Shortcuts use RegisterHotKey with repeat suppression. Only the seven known actions
are accepted. Chords are validated for supported keys, modifiers, duplicates and
reserved combinations. A rejected change restores prior registrations and reports
actual availability. Capture forwarding applies only while Settings is active.
The notification-area Settings entry remains a recovery route for click-through.

## Browser handoff and updates

The optional MV3 extension requests activeTab and nativeMessaging. A current-user
named pipe passes a bounded YouTube URL to the existing Rot instance. Cold startup
launches the sibling app once. Connected requests that time out are not resent.
Only the latest valid selection is retained, with playback deferred until verified
training and focus. No account credentials, cookies or Premium session transfer.

Rot targets .NET 10 LTS and publishes a self-contained x64 portable folder.
Build version and revision come from assembly metadata. Updates are explicitly
requested from Settings. Release metadata is limited to this repository; bounded
packages require a SHA-256 match and safe archive extraction. A separate helper
copies a sibling candidate, waits for the exact old process, keeps the old folder,
and requires a new-process readiness acknowledgment. Failed startup restores the
previous installation. Preferences and native messaging registration live outside
the install folder. Backups remain available for manual recovery.

## Distribution and privacy

No Rot accounts, telemetry, analytics or automatic community outreach. GitHub is
contacted for explicit update checks/downloads or project links. YouTube controls
its own ads, account restrictions and network traffic. Opt-in validation writes
local diagnostics that may include identifying game data and are excluded from
public source and release artifacts.

Public source uses a clean initial history. Private machine-specific development
records are preserved locally instead of being exported. Public releases include
component licenses, setup instructions and checksums. See CONTRIBUTING.md,
VALIDATION.md and docs/PRIVACY.md for operating and testing boundaries.