# Troubleshooting Rot

Open **Settings** from the red R in the Windows notification area. If it is hidden,
open the **^** menu beside the clock. Settings works even while Rocket League is
closed. Launching Rot again also opens the existing instance's Settings.

## The player does not appear

1. Use the Epic Games version of Rocket League in **Borderless** or **Windowed** mode.
2. Enter **Play > Training > Free Play** and keep Rocket League in the foreground.
3. Open **Settings > Help > Connection & troubleshooting** for the detected state.
4. If a restart is required, close Rocket League fully and start it normally.
5. Use your Show or hide Player shortcut if you previously hid it manually.

Rot hides and pauses while the game is in the background, while changing arenas,
and during online play. The online show/hide shortcut can reveal a paused, muted
player with a warning. It does not authorize online playback.

## Detection is disconnected or needs repair

Choose **Check settings** under Connection & troubleshooting. Rot checks the
effective Windows Documents folder, including a redirected Documents folder, for:

```text
My Games\Rocket League\TAGame\Config\TAStatsAPI.ini
```

It preserves unrelated content and sets:

```ini
[TAGame.MatchStatsExporter_TA]
PacketSendRate=1
Port=0
WebPort=49124
```

Changes made while the game is running require a full game restart. Rot only
receives data from `ws://127.0.0.1:49124/`. A connected socket does not by itself
prove that training is active. Rot recognizes local training from events with an
explicitly empty match ID, enters transition handling after a local match-destroyed
event, and marks online play when a populated match ID arrives. During transition or
detected online play it mutes and pauses the Player and normally hides it. If no valid
event arrives for about five seconds, Rot marks detection disconnected and applies the
same automatic mute, pause, and hide safety path. An event without a usable match ID
can refresh liveness without changing the last recognized state.

With Rocket League focused, Player shortcuts remain available for manual playback
while detection is disconnected or connected before verified training. This
fallback does not verify training. When online play is detected, Show or hide can
reveal only a paused, muted warning. Automatic resume still requires fresh local
training evidence and current game and focus checks.

## A shortcut does not work

In Settings, open **Help > Keyboard shortcuts**. If a shortcut row reports a
conflict, for example `Cycle player opacity (Ctrl+Shift+O): Hot key is already
registered`, select the affected binding (`Unavailable` in this example), press
a new supported chord, and choose **Apply**. Use Ctrl, Alt or Win plus another
supported key.
Rot rejects duplicates and reports conflicts with other applications. **Restore
defaults** resets the seven shortcuts together.

If click-through is enabled and its recovery shortcut is unavailable, use Settings
from the tray and turn **Click through Player** off. Settings remains interactive.

## YouTube asks me to sign in or refuses playback

Rot's embedded browser is signed out. Google sign-in in an embedded WebView is not
supported. The optional Send to Rot extension lets you choose a video using your
normal browser's recommendations and send its address to Rot. It does not transfer
your account, cookies or YouTube Premium benefits into Rot.

Some videos cannot be embedded or require an account or age verification. Use the
player's **Open on YouTube** action to watch those in your normal browser. YouTube
controls ads and playback quality. Browse itself is always muted.

## Native host registration fails

The native host is required for **Send to Rot**. A successful registration run
prints `HostName`, `ExtensionId`, `ManifestPath`, and `Browsers`, then this
check returns `True`:

```powershell
Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Rot\BrowserHost\com.rot.send_to_rot.json') -PathType Leaf
```

If the script reports access denied, `WriteAllText` cannot open the manifest, or
the check returns `False`, inspect the expected path:

```powershell
$hostDirectory = Join-Path $env:LOCALAPPDATA 'Rot\BrowserHost'
Get-Item -LiteralPath $hostDirectory -Force -ErrorAction SilentlyContinue |
  Select-Object FullName, PSIsContainer, Length
```

The `v2.1.0` registration script can leave a zero-byte **file** at that path
when the directory did not exist. The `v2.1.1` script creates the directory and
rejects an existing file. Do not delete the failed file or rerun the `v2.1.0`
script after manually creating a directory.

From the matching `v2.1.1` or later `Rot-win-x64` folder, preserve the failed
file by moving it aside, then rerun the registration script:

```powershell
$hostDirectory = Join-Path $env:LOCALAPPDATA 'Rot\BrowserHost'
if (Test-Path -LiteralPath $hostDirectory -PathType Leaf) {
    $quarantinePath = "$hostDirectory.failed-file-$([guid]::NewGuid().ToString('N'))"
    Move-Item -LiteralPath $hostDirectory -Destination $quarantinePath
    Write-Host "Preserved failed path at $quarantinePath"
}
powershell -ExecutionPolicy Bypass -File .\scripts\install-browser-host.ps1
Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Rot\BrowserHost\com.rot.send_to_rot.json') -PathType Leaf
```

Run those commands from the matching extracted `Rot-win-x64` folder. The final
`Test-Path` result must be `True`; then load the matching `browser-extension`
folder in Chrome or Edge. The `v2.1.0` app and preferences remain usable;
moving the failed file preserves it for inspection and does not remove
`%LOCALAPPDATA%\Rot` data.

## A save fails or a layout is off screen

Settings changes save automatically. A failed write leaves the prior values in
place and shows an explanation beside the relevant controls. Check that your user
profile has free space and permits writing to `%LOCALAPPDATA%\Rot`.

Choose **Appearance > Reset positions** to restore the three windows. Rot stores
monitor-relative sizes and positions and adjusts when the display layout changes.
Mixed DPI and unusual monitor changes can still need a reset.

Rot retains a previous preferences snapshot and preserves a malformed file before
recovering it. Do not delete your profile as the first troubleshooting step.

## An update fails

Rot checks GitHub only when you choose **Check for updates** in About Rot.
The current process stays open until the helper has copied and checked a candidate
installation. Download, verification or preparation errors leave it running.

If the new app cannot report readiness after replacement, the helper restores and
restarts the previous installation. Preferences remain in `%LOCALAPPDATA%\Rot`.
A successful update keeps a `.rot-backup-...` folder beside Rot. A failed new
installation may remain in `.rot-failed-...` for diagnosis. After confirming the
updated app works, you can remove those backup folders manually.

If automatic restart fails, open `Rot.exe` in the restored portable folder.
Bounded updater diagnostics are kept in the matching
`%LOCALAPPDATA%\Rot\Updates\update-...\update-error.log`. Do not post that log
without removing private paths. Keep Rot in a normal user-writable folder, not
a drive root, repository root or linked folder.

## Report a reproducible problem

Include the version and build shown in **About Rot**, Windows version, display
setup, and concise steps. Read [the validation limits](../VALIDATION.md) before
assuming a configuration is supported. Use the
[issue form](https://github.com/bkaranf/Rot/issues/new/choose).

Do not post raw session logs, account information or private machine paths.
