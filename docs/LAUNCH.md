# Rot launch strategy

This plan supports the 2.1 release. Publication follows implementation, integrated
verification and the independent final review. Community posts below are drafts;
creating this document does not send them anywhere.

## Positioning

**Watch during Free Play. Focus when the match starts.**

Rot is a free, open-source Windows YouTube player for Rocket League training. It
uses the local Stats API to pause and hide as training ends, then resumes after
training is verified again. Its strongest audience is players who already watch
videos while warming up or waiting for a match.

Lead with the actual workflow. Avoid promises of improved rank, perfect detection,
anti-cheat certification, ad-free YouTube or embedded Google sign-in. Preserve
the distinction between Rot's external Windows window and an injected game overlay.

## GitHub front-page research

Research date: September 5, 2026. GitHub's repository search was sorted by stars,
with `stars:>100000 archived:false`. Counts below are a dated snapshot, not live
badges or endorsements. The app examples provide a closer comparison than the
large educational lists. A common README pattern is not evidence that it caused
the repository's popularity.

| Repository | Stars observed | Useful presentation pattern | Application to Rot |
|---|---:|---|---|
| [Build your own X](https://github.com/codecrafters-io/build-your-own-x) | 545,275 | A direct purpose statement and immediate navigation to useful content. | Explain the training-to-match workflow in one sentence and place download/setup links near the top. |
| [Awesome](https://github.com/sindresorhus/awesome) | 503,075 | Recognizable visual identity and organized topic navigation. | Use Rot's existing red R and a short feature list. Keep the main action visible. |
| [freeCodeCamp](https://github.com/freeCodeCamp/freeCodeCamp) | 455,021 | Clear audience, concrete value and explicit routes to help and contribute. | Say who Rot is for and link directly to troubleshooting, issues and contributing. |
| [yt-dlp](https://github.com/yt-dlp/yt-dlp) | 189,018 | A concise product description, release information and complete installation/update guidance. | Provide a Windows download, requirements, versioned release notes and honest browser limitations. |
| [PowerToys](https://github.com/microsoft/PowerToys) | 138,394 | Product identity, a plain description, prominent navigation and a scannable utility overview. | Put a short workflow visual and the primary capabilities before implementation details. |
| [ShareX](https://github.com/ShareX/ShareX) | 39,447 | A real screenshot, release links and concrete workflows for a Windows utility. | Show the actual Rot interface and explain an everyday training session. |

The resulting README structure is: identity and purpose, download and quickstart,
visual workflow, short feature overview, browser integration, limitations and
privacy, troubleshooting, contribution and license. Technical details belong in
linked documentation. Use original wording and Rot assets, with no copied slogans,
borrowed screenshots or invented badges and statistics.

## Launch sequence

| Stage | Action | Evidence of success |
|---|---|---|
| Release readiness | Pass the integrated suite and final critic. Check the public source, release ZIP, checksum, extension instructions and a clean install/update. | Public download works and an unfamiliar user can complete the documented setup. |
| GitHub launch | Publish the source, versioned Windows package, extension package, release notes and issue templates. Use relevant topics and a concise About description. | README links resolve, CI is green and release hashes match the files. |
| First users | Invite a small group of willing Free Play users through channels where the maintainer already participates and promotion is permitted. | Gather reproducible setup, playback and update reports across supported Windows/display configurations. |
| Demonstration | Record a short manual clip showing training playback, match transition and training return. Include the tested build version. | The clip shows observed behavior without exposing other players' identities or claiming untested behavior. |
| Wider sharing | Share the clip and a brief explanation in appropriate Rocket League communities after reading their current posting rules. Offer the GitHub download and one clear way to report problems. | Relevant users try the app and provide actionable feedback. |
| Follow-through | Fix the most frequent setup and reliability problems, publish concise release notes and credit contributors. | Fewer repeated support questions and resolved reproducible issues. |

No paid advertising, automated outreach, unsolicited direct messages or fake
engagement are part of this plan. Community posting remains a separate user action.

## What to measure

Use existing GitHub information: release asset downloads, issue categories,
reproducible failures, resolution time and voluntary feedback. Treat stars as a
secondary signal of interest. Rot should not add analytics, tracking identifiers
or account requirements to measure a launch. Do not infer active users from ZIP
downloads or publish unsupported reliability percentages.

## Ready-to-use launch copy

### Short community post

**I built Rot: a YouTube player that gets out of the way when your match starts.**

I like watching videos while warming up in Rocket League Free Play. Rot keeps a
small player on screen during training, pauses and hides as training ends, and
resumes when training is verified again.

It is a normal Windows app with saved layouts, customizable shortcuts and an
optional Send to Rot browser button. It uses the local Stats API without injecting
into the game. Borderless or Windowed mode is required, and YouTube's normal
embedding and account limitations still apply.

Source, download and setup: https://github.com/bkaranf/Rot

If you try it, a report with your Rot version, Windows version and the steps that
caused a problem is especially helpful. Please leave private logs out of public
issues.

### Short announcement

Watch during Free Play. Focus when the match starts.

Rot is an open-source Windows YouTube player for Rocket League training, with
automatic pause/resume, saved layouts and configurable shortcuts.

Download and source: https://github.com/bkaranf/Rot

### Repository About text

A small YouTube player for Rocket League training. Pauses and hides for matches,
then resumes in Free Play. Windows, open source, no game injection.

Suggested topics: `rocket-league`, `youtube`, `windows`, `wpf`, `webview2`,
`training`, `desktop-app`, `dotnet`, `open-source`.

## Publication checklist

- Use actual interface screenshots and identify any isolated demonstration fixture.
- Verify instructions from a clean extracted package, including browser setup.
- Publish a real version and matching checksum for each downloadable artifact.
- Link to the tested limitations instead of claiming broad anti-cheat approval.
- Include MIT licensing and third-party notices.
- Keep credentials, local profile data, raw Stats payloads and machine-specific
  test records out of public source, Git history and release archives.
- Confirm the final critic's findings are resolved before the first public push.

