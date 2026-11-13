import assert from "node:assert/strict";

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE || "playwright");
const url = process.env.SAMPLE_URL || "http://127.0.0.1:8080";
assert.equal(new URL(url).hostname, "127.0.0.1", "UI checks must use a local sample");
const browser = await chromium.launch({
  executablePath: process.env.EDGE_EXECUTABLE,
  headless: true,
});
try {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1100 }, permissions: ["clipboard-read", "clipboard-write"] });
  const page = await context.newPage();
  const errors = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.goto(url);
  await page.locator("#environment").filter({ hasText: "Azure export is off" }).waitFor();
  await page.locator("#setup-status").filter({ hasText: "settings are missing" }).waitFor();
  assert.equal(await page.getByRole("link", { name: "Connect monitoring to this deployed app" }).getAttribute("href"), "./README.md#setup");
  await page.route("**/api/setup", route => route.fulfill({ json: { code: "ingestion_denied" } }));
  await page.locator("#setup-check").click();
  await page.locator("#setup-status").filter({ hasText: "Azure denied ingestion" }).waitFor();
  await page.unroute("**/api/setup");
  await page.locator("#setup-check").click();
  await page.locator("#setup-status").filter({ hasText: "settings are missing" }).waitFor();
  for (const [scenario, expected] of [["success", 200], ["slow", 200], ["error", 500], ["dependency", 200]]) {
    await page.locator(`[data-scenario="${scenario}"]`).click();
    await page.locator("#result:not([hidden])").waitFor();
    assert.equal(await page.locator("#http-status").textContent(), String(expected));
    assert.equal(await page.locator("#scenario").textContent(), scenario);
    assert.match(await page.locator("#operation-id").textContent(), /^[a-f0-9]{32}$/);
    if (scenario === "slow") assert.ok(parseFloat(await page.locator("#duration").textContent()) >= 600);
    if (scenario === "error") assert.match(await page.locator("#request-status").textContent(), /Intentional 500/);
    if (scenario === "dependency") assert.match(await page.locator("#message").textContent(), /real HTTP.*loopback/);
  }
  await page.locator("#copy").click();
  await page.locator("#request-status").filter({ hasText: "Operation ID copied" }).waitFor();
  assert.match(await page.locator("#request-status").textContent(), /copied/);
  assert.equal(await page.evaluate(() => navigator.clipboard.readText()), await page.locator("#operation-id").textContent());
  await page.locator("#health").click();
  await page.locator("#health-status").filter({ hasText: "HTTP 200" }).waitFor();
  assert.match(await page.locator("#health-status").textContent(), /ingestion is not verified/);
  assert.equal(await page.locator("#health").isEnabled(), true);
  if (process.env.SCREENSHOT_DIR) {
    await page.screenshot({ path: `${process.env.SCREENSHOT_DIR}/desktop1440.png`, fullPage: true });
  }
  await page.route("**/api/demo/success", (route) => route.abort());
  await page.locator('[data-scenario="success"]').click();
  await page.locator("#request-status").filter({ hasText: "retry explicitly" }).waitFor();
  assert.equal(await page.locator("#result").isVisible(), false, "Failed request must not retain stale successful result");
  assert.equal(await page.locator('[data-scenario="success"]').isEnabled(), true);
  await page.unroute("**/api/demo/success");
  await page.locator('[data-scenario="success"]').click();
  await page.locator("#result:not([hidden])").waitFor();
  await page.route("**/health", (route) => route.abort());
  await page.locator("#health").click();
  await page.locator("#health-status").filter({ hasText: "could not be checked" }).waitFor();
  assert.equal(await page.locator("#health").isEnabled(), true);
  await page.unroute("**/health");
  await page.locator("#health").click();
  await page.locator("#health-status").filter({ hasText: "HTTP 200" }).waitFor();
  await page.setViewportSize({ width: 390, height: 844 });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.getByText("Customize this signal lab", { exact: true }).click();
  assert.equal(await page.getByText(/Rebuild, test and deploy your source/).isVisible(), true);
  if (process.env.SCREENSHOT_DIR) {
    await page.screenshot({ path: `${process.env.SCREENSHOT_DIR}/mobile390.png`, fullPage: true });
  }
  assert.equal(await page.locator('a[download]').count(), 0);
  await page.getByRole("link", { name: "Connect monitoring to this deployed app" }).click();
  await page.locator("#setup").waitFor();
  assert.equal(await page.locator("h2").count(), 5);
  assert.ok((await page.locator("pre code").allTextContents()).every(code => !/\\\n\n/.test(code)), "Reader preserves shell continuations");
  assert.match(await page.locator("body").textContent(), /infra\/main.bicep/);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.goBack();
  const assets = await page.locator("img").evaluateAll((images) => images.map((image) => ({ src: image.getAttribute("src"), loaded: image.complete && image.naturalWidth > 0 })));
  assert.ok(assets.every((asset) => asset.loaded));
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ sample: "application-insights", mode: "off", scenarios: 4, actualLocalHttp: true, clipboard: "passed", healthRetry: "passed", staleResults: "passed", desktop: 1440, mobile: 390, assets, pageErrors: errors, cloudVerified: false }, null, 2));
} finally {
  await browser.close();
}
