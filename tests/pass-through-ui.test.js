import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { buildPassThroughPresentation } from "../src/Rot.App/Web/player/pass-through.js";

const repositoryRoot = fileURLToPath(new URL("../", import.meta.url));

test("active pass-through presentation uses live recovery and Browse bindings", () => {
  const presentation = buildPassThroughPresentation(true, {
    interactivity: "Alt+P",
    toggleBrowse: "Alt+F",
  });

  assert.equal(presentation.active, true);
  assert.equal(presentation.interactivityBinding, "Alt+P");
  assert.equal(presentation.browseBinding, "Alt+F");
  assert.match(presentation.settingsDescription, /Settings stays interactive/);
  assert.match(presentation.settingsDescription, /Alt\+P/);
  assert.match(presentation.savedMessage, /Alt\+P/);
});

test("pass-through presentation falls back to the shipped recovery bindings", () => {
  const presentation = buildPassThroughPresentation(true, {});

  assert.equal(presentation.interactivityBinding, "Ctrl+Shift+P");
  assert.equal(presentation.browseBinding, "Ctrl+Shift+F");
});

test("player Settings control opens the deliberate recovery surface", async () => {
  const playerScript = await readFile(
    `${repositoryRoot}src/Rot.App/Web/player/player.js`,
    "utf8",
  );

  assert.match(
    playerScript,
    /settingsButton\.addEventListener\("click",[\s\S]*?fireWindowAction\("open-settings"\)/,
  );
  const directToggleAction = ["toggle", "pass-through"].join("-");
  assert.equal(playerScript.includes(directToggleAction), false);
});
