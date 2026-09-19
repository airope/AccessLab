import { defineConfig } from "@playwright/test";
import { requireIsolated } from "./cleanup.mjs";

// Fail before browser launch, credential reads, or any suite mutation.
requireIsolated();

export default defineConfig({
  testDir: "./specs",
  timeout: 120_000,
  expect: { timeout: 15_000 },
  workers: 1,
  fullyParallel: false,
  retries: 0,
  reporter: [
    ["list"],
    ["json", { outputFile: "../../.local/artifacts/e2e-results.json" }],
  ],
  outputDir: "../../.local/artifacts/playwright",
  use: {
    actionTimeout: 15_000,
    navigationTimeout: 30_000,
    baseURL: process.env.ACCESSLAB_URL ?? "https://localhost:5443",
    ignoreHTTPSErrors: true, // Only the disposable local development certificate.
    browserName: "chromium",
    channel: process.env.ACCESSLAB_BROWSER ?? "chrome",
    trace: "off", // Do not retain OIDC tokens or fixture credentials in traces.
    screenshot: "only-on-failure",
    viewport: { width: 1440, height: 1000 },
  },
});
