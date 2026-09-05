# Contributing to Rot

Rot is a small Windows application for YouTube playback during Rocket League
training. Changes should improve that workflow while keeping the game integration
receive-only and the Player nonactivating.

## Report a problem

Use a GitHub issue and include the Rot version shown in Settings, Windows version,
display mode, reproduction steps, expected behavior and actual behavior. Mention
whether the problem occurs during training, a transition or an online match.

Do not post access tokens, cookies, your settings profile, raw Stats recordings,
player-identifying information or private filesystem paths. A short redacted error
message is usually enough to begin investigating.

## Development

Install the .NET 10 SDK, a current Node.js LTS release and the WebView2 Evergreen
Runtime on Windows. From the repository root:

```powershell
dotnet restore Rot.sln
dotnet format Rot.sln --verify-no-changes --no-restore
dotnet build Rot.sln -c Release --no-restore
dotnet test Rot.sln -c Release --no-build
node --test
node scripts/check-repository.mjs
```

For the owned Player and Settings browser preview:

```powershell
node scripts/preview-ui.mjs
```

Open `http://127.0.0.1:4174/settings/index.html`. This is a preview; native window,
hotkey and game behavior requires the Windows app. Labeled fixtures use isolated
test preferences and must never be packaged as production behavior.

## Submit a change

Keep each pull request focused. Describe the problem, the changed behavior and
the checks you ran. Add a regression for a bug when practical. Test UI changes
with keyboard input, narrow windows and enlarged text. Use clear copy without
em dashes.

Do not add game injection, game memory or screen access, simulated game input,
telemetry, credential collection, YouTube page modifications, cookie transfer or
an unrequested background service. Preserve match/process/focus guards and do not
weaken tests to make a change pass. New failure recovery should remain muted and
hidden until the current lifecycle allows presentation.

Native integration tests should use their own windows, profiles and ephemeral
loopback ports. Never point a test server at the user's live Stats API port.
Document any live validation separately, including unobserved steps and exact
tested versions. A passing unit suite is not an anti-cheat compatibility claim.

By contributing, you agree that your contribution can be distributed under the
repository's MIT license.
