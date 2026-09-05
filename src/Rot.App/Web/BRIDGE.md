# Rot WebView2 bridge contract

Rot serves its owned pages as `https://rot.local/` through a virtual-host
mapping. The Player and Settings pages use this bridge. Browse renders YouTube
in a separate, permanently muted WebView; that external page never receives the
bridge and Rot never injects scripts or styles into it.

## Envelope

Web-to-native request:

```json
{ "type": "state.get", "requestId": "settings-mabc-1", "payload": {} }
```

Native response:

```json
{ "type": "bridge.response", "requestId": "settings-mabc-1", "ok": true, "payload": { "state": {} } }
```

Notifications omit `requestId`. JavaScript posts objects through
`chrome.webview.postMessage`; the host may post objects or JSON strings. The
native bridge accepts messages only from the `rot.local` origin.

## Web to native

| Type | Kind | Payload | Result |
|---|---|---|---|
| `bridge.ready` | notification | `{view, version, href}` | none |
| `state.get` | request | `{}` | `{state}` |
| `settings.patch` | request | `{patch}` | `{state}` |
| `layout.reset` | request | `{}` | `{state}` |
| `stats.repair` | request | `{}` | `{state, message}` |
| `playback.save` | notification | `{resume}` | none |
| `player.capabilities` | notification | `{ready, appControls, reason}` | none |
| `player.status` | notification | `{state, videoId?, seconds?, errorCode?}` | none |
| `player.command.result` | notification | `{commandId, command, ok, error, state, seconds, desiredPlaying}` | none |
| `browse.parse-result` | notification | `{correlationId, media, error}` | none |
| `external.open` | request | `{url}` | `{}` |
| `hotkeys.set` | Settings request | `{bindings}` with all seven known actions | `{state}` |
| `hotkeys.capture` | Settings request | `{active}` while the editor has focus | `{state}` |
| `updates.check` | Settings request | `{}` | `{state,update}` |
| `updates.install` | Settings request | `{}` after a successful availability check | `{state,update}` |
| `player.recover` | Settings request | `{}` | `{state}` after a bounded retry |
| `project.open` | request | `{target:"repository|releases|help"}` | `{}` |
| `window.action` | notification | see below | none |

Media is `{videoId?,playlistId?,startSeconds?,canonicalUrl?,title?,thumbnailUrl?}`.
Resume uses the same identity fields plus `seconds` and `updatedAt`.

Window actions used by the owned pages:

```text
{window:"player",   action:"drag"}
{window:"player",   action:"resize", edge:"bottom-right"}
{window:"player",   action:"hide"}
{window:"player",   action:"show-browse"}
{window:"player",   action:"open-settings"}
{window:"settings", action:"drag|hide"}
```

The web and native sides both validate any address before opening it in the
system browser.

The native tray can also open Settings with a desktop presentation origin. This
does not add a web bridge action or grant Player/Browse permissions. The same
Settings controls and `settings.patch` persistence work in either presentation;
the native controller owns focus restoration and game interaction checks.

## Native to web

| Type | Target | Payload |
|---|---|---|
| `state.changed` | Player, Settings | `{state}` |
| `player.command` | Player | command payload below |
| `browse.parse` | Player | `{correlationId,input}` |
| `pointer.activity` | Player | `{}` |
| `settings.focus` | Settings | `{}` |
| `hotkeys.captured` | Settings | `{modifiers,virtualKey}` |
| `runtime.notice` | Player | `{kind?,message,durationMs?}` |

The native Browse surface sends each typed value or detected source address to
`browse.parse`. Player calls the shared `parseYouTubeInput` implementation and
echoes the correlation ID in `browse.parse-result`. Native code owns the match
epoch and Browse generation checks before loading media or changing windows.
PlayerWindow also reports pointer movement from the native window rectangle so
activity over the cross-origin YouTube iframe can reset the two-second chrome
timer without injecting into the frame.

Player commands:

```text
{commandId, command:"load", media}
{commandId, command:"clear"}
{commandId, command:"toggle-play-pause"}
{commandId, command:"play"}
{commandId, command:"pause"}
{commandId, command:"toggle-mute"}
{commandId, command:"next"}
{commandId, command:"apply-audio", volume, muted}
{commandId, command:"retry"}
{commandId, command:"save-position"}
```

`next` calls YouTube's `nextVideo()` only when the current media is a playlist.
Player answers every command with `player.command.result`. `play` and `pause`
update a persistent desired state before the IFrame Player becomes ready, so a
late callback cannot reverse a lifecycle decision.

Each native bridge attachment belongs to one WebView generation. Detaching it
cancels outstanding handlers and prevents old responses from reaching a replacement
page. A failed native post is reported as unavailable instead of successful dispatch.
Shortcut capture forwards registered chords only while Settings is active and ready.

Runtime includes `version`, `revision`, `hotkeyBindings`, `hotkeyDefaults`,
`recoveryMessage`, `recoveryCanRetry`, and `update`. Update state contains
`currentVersion`, `latestVersion`, `isUpdateAvailable`, `message`, `busy` and `notice`.
Only native code chooses the fixed release repository and staged package. Pages do
not supply download URLs or installation paths. Update requests use longer bounded
timeouts than ordinary Settings saves.

The separate current-user instance pipe accepts `open-settings` and `send-to-rot`.
It uses bounded length-prefixed JSON. External selections pass through the same
shared parser and retain only the latest valid media until normal game and focus
rules allow loading. This pipe never transfers account credentials or browser data.

## State shape consumed by owned pages

Unknown fields are ignored and missing fields use conservative defaults.

```json
{
  "schemaVersion": 2,
  "settings": {
    "volume": 75,
    "muted": false,
    "opacity": 1,
    "sizePreset": "custom",
    "passThrough": false,
    "autoRestoreAfterMatch": true
  },
  "resume": null,
  "runtime": {
    "detectionState": "disconnected|connected-idle|local|transition|online",
    "detectionAvailable": false,
    "detectionMessage": "",
    "restartRequired": false,
    "borderlessWarning": false,
    "playerCapabilities": { "ready": false, "appControls": false, "reason": "" },
    "hotkeyFailures": [
      { "action": "toggle-interactivity", "chord": "Ctrl+Shift+P", "message": "" }
    ],
    "hotkeys": {
      "togglePlayer": "Ctrl+Shift+Y",
      "toggleBrowse": "Ctrl+Shift+F",
      "playPause": "Ctrl+Shift+K",
      "mute": "Ctrl+Shift+M",
      "next": "Ctrl+Shift+N",
      "opacity": "Ctrl+Shift+O",
      "interactivity": "Ctrl+Shift+P"
    }
  }
}
```
