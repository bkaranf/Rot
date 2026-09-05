# Privacy and local data

Rot has no telemetry, analytics, accounts, subscriptions or tracking identifiers.
There is no Rot server. YouTube and Microsoft WebView2 remain third-party services
with their own behavior and terms.

## What stays on your computer

Preferences, resume position, shortcuts, and three window placements are stored
under `%LOCALAPPDATA%\Rot`. The WebView2 browser profile is stored there too.

Preferences use `settings.v1.json`. Despite its historical filename, the document
contains the current schema version. A complete temporary file is flushed before
replacement, and the prior snapshot is retained as `settings.v1.json.previous`.
Malformed files are preserved as `settings.v1.json.corrupt-*` before recovery.

Optional `--validation-session` logs are local and never uploaded by Rot. They can
contain machine-specific details. Review and redact any excerpt before sharing.

## Network and browser access

- Rot receives Rocket League's local Stats API WebSocket data. It sends no Stats
  messages or commands.
- Browse and the official YouTube player contact YouTube and its supporting
  services for the content you use. Rot does not scrape or modify YouTube pages.
- **Check for updates** contacts the public Rot GitHub repository only when you
  choose it. Installing an update downloads the selected official release assets.
- The optional browser extension reads the active tab's address only when clicked
  and sends a supported YouTube URL to Rot through native messaging. It requests
  `activeTab` and `nativeMessaging`, with no content script or cookie access.

The extension does not copy Google sign-in, browsing history or Premium status.
It does not need your email, password or a Google authorization flow.

## Game integration

Rot uses ordinary Windows windows, shortcuts and foreground process identity. It
does not inject code into Rocket League, read game memory, capture the game screen,
simulate game input or change game traffic. It checks three local Stats API
configuration values and preserves unrelated configuration content.

These design choices are not an anti-cheat certification or a promise about every
future game update. See [validation and known limits](../VALIDATION.md).
