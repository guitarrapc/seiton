// OG Screenshot Automation for Seiton Playground
// Usage: npx playwright install chromium && node sandbox/scripts/take-ogp.mjs
// Requires: Playground server running on https://localhost:7025/
//
// Configuration:
//   --output <path>    Output file path (default: ogp.png)
//   --sample <name>    Sample workflow name: default|simple|minimal|fixPermissions|matrix|actionComposite (default: simple)
//   --theme <name>     Theme: dark|light|system (default: dark)
//   --font-size <px>   Editor font size in px (default: 16)

import { chromium } from "playwright";
import { parseArgs } from "node:util";
import { stat } from "node:fs/promises";

const { values: args } = parseArgs({
  options: {
    output: { type: "string", default: "ogp.png" },
    sample: { type: "string", default: "simple" },
    theme: { type: "string", default: "dark" },
    "font-size": { type: "string", default: "16" },
  },
});

const WIDTH = 1200;
const HEIGHT = 670;
const URL = "https://localhost:7025/";

async function main() {
  const browser = await chromium.launch({ headless: true });
  const colorScheme = args.theme === "light" ? "light" : args.theme === "dark" ? "dark" : undefined;
  const context = await browser.newContext({
    viewport: { width: WIDTH, height: HEIGHT },
    ignoreHTTPSErrors: true,
    ...(colorScheme && { colorScheme }),
  });
  const page = await context.newPage();

  // Set localStorage BEFORE navigation so the inline <script> picks up the
  // theme on first paint and CodeMirror initialises with the correct theme.
  console.log(`Setting theme to "${args.theme}" via localStorage...`);
  await context.addInitScript((theme) => {
    localStorage.setItem("seiton-playground-color-mode", theme);
  }, args.theme);

  console.log(`Opening ${URL} ...`);
  await page.goto(URL, { waitUntil: "networkidle" });

  // Wait for WASM editor to load
  console.log("Waiting for editor to load...");
  await page.waitForSelector(".CodeMirror", { timeout: 120_000 });

  // Ensure CodeMirror uses the correct theme (in case addInitScript timing
  // didn't cover the WASM-loaded editor initialisation).
  const cmTheme = args.theme === "light" ? "default" : "material-darker";
  await page.evaluate((theme) => {
    const cm = document.querySelector(".CodeMirror");
    if (cm?.CodeMirror) cm.CodeMirror.setOption("theme", theme);
  }, cmTheme);

  // Select sample workflow
  await page.selectOption("#sample-select", args.sample);
  await page.waitForTimeout(1500); // wait for lint results

  // Apply CSS tweaks: hide version badge, hide controls, set editor font size
  const fontSize = args["font-size"];
  await page.evaluate((fs) => {
    const style = document.createElement("style");
    style.textContent = `
      .version-badge { display: none !important; }
      #controls { display: none !important; }
      .CodeMirror { font-size: ${fs}px !important; }
    `;
    document.head.appendChild(style);
  }, fontSize);

  // Refresh CodeMirror after font size change
  await page.evaluate(() => {
    const cm = document.querySelector(".CodeMirror");
    if (cm?.CodeMirror) cm.CodeMirror.refresh();
  });

  await page.waitForTimeout(500);

  // Take screenshot (viewport is already 1200x670, capture as-is)
  const output = args.output;
  await page.screenshot({ path: output });

  const { size } = await stat(output);
  const kb = (size / 1024).toFixed(1);
  console.log(`Saved: ${output} (${kb} KB, ${WIDTH}x${HEIGHT})`);

  await browser.close();
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
