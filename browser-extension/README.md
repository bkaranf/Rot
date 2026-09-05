# Send to Rot

Choose a YouTube video in your signed-in Chrome or Edge browser and send its
address to Rot. The Player loads the selection after local training is verified
and Rocket League has focus. Sending another video replaces the pending selection.

## Install

Requires Rot 2.1.1 or later. If you tried 2.1.0, see [the recovery steps](../docs/TROUBLESHOOTING.md#native-host-registration-fails).

Use matching files from one Rot release. `Rot-win-x64.zip` contains the
portable app, native host, registration scripts, and `browser-extension`.
`Send-to-Rot.zip` contains the extension files only; it does not replace the
native host in the portable app.

1. Download [Rot-win-x64.zip](https://github.com/bkaranf/Rot/releases/latest/download/Rot-win-x64.zip)
   and extract the whole `Rot-win-x64` folder into a stable, user-writable
   folder. Keep its `browser-extension` folder beside `Rot.exe`.
2. Open PowerShell in that extracted `Rot-win-x64` folder and run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\install-browser-host.ps1
   ```

   This registers a native messaging host for your Windows user in Chrome and
   Edge. No administrator access is needed. Add `-Browser Chrome` or
   `-Browser Edge` to register only one browser. Add `-WhatIf` to preview changes.

   A successful run prints `HostName`, `ExtensionId`, `ManifestPath`, and
   `Browsers`. Confirm the manifest was created before continuing:

   ```powershell
   Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Rot\BrowserHost\com.rot.send_to_rot.json') -PathType Leaf
   ```

   The result must be `True`. If the command fails or returns `False`, stop and
   use [the native-host troubleshooting steps](../docs/TROUBLESHOOTING.md#native-host-registration-fails).
3. Open `chrome://extensions` or `edge://extensions`, enable **Developer mode**,
   choose **Load unpacked**, and select the extracted `browser-extension` folder.
4. Pin **Send to Rot** from the browser's extensions menu.
5. Open a YouTube video or playlist and click the red R. `Sent` confirms Rot
   accepted the address. Hover over the extension for an error if it shows `!`.

The extension ID is `ajakpkcchbjafafhjbobkaobhgpdikjd`. The committed public key
keeps that ID stable for unpacked installs. It is a public identifier, not a
credential.

If Rot is closed, the native host starts the sibling `Rot.exe` quietly. If Rot
is already running, it sends the address to the existing instance. An unavailable
host or parser produces an error rather than repeatedly sending the video.

## Updates and moving folders

The native host manifest lives at
`%LOCALAPPDATA%\Rot\BrowserHost\com.rot.send_to_rot.json` and points to the stable
portable folder. Rot's staged updater preserves that registration. After an
extension update, click **Reload** on its browser extensions page.

If you move Rot to a different folder, rerun the registration script from the new
folder and reload the unpacked extension from its new location. Developer-mode
reminders are controlled by your browser. This release is not listed in a browser
extension store.

For a source checkout, first run `scripts\publish.ps1`, then run the registration
script from the repository root. It finds `dist\Rot-win-x64` automatically.

## Uninstall

From the same portable folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-browser-host.ps1
```

Remove **Send to Rot** from the browser extensions page. Uninstall removes only
registrations and the manifest belonging to that Rot installation. Rot preferences
and the browser profile are kept.

## Permissions and limits

- `activeTab` reads the current tab address only when you click the extension.
- `nativeMessaging` delivers that address to Rot's local helper.
- No cookies, passwords, account tokens, browsing history, page injection or
  Google sign-in transfer. YouTube Premium does not transfer to the embedded
  player.
- Video availability, ads, embedding restrictions and sign-in requirements remain
  controlled by YouTube.

See [privacy](../docs/PRIVACY.md) and [troubleshooting](../docs/TROUBLESHOOTING.md).
