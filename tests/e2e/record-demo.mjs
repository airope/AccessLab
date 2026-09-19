// Record a paced, real browser demonstration. No login exchange is recorded.
import { chromium } from "@playwright/test";
import { readFileSync, mkdirSync } from "node:fs";
import { resolve } from "node:path";
import {
  requireIsolated,
  cleanupRun,
  readMemberships,
  closeAll,
} from "./cleanup.mjs";

// No credential read or browser launch against a shared demo database.
requireIsolated();
const root = resolve(import.meta.dirname, "../..");
const baseURL = process.env.ACCESSLAB_URL ?? "https://localhost:5443";
const artifacts = resolve(root, ".local/artifacts");
mkdirSync(artifacts, { recursive: true });
const credentials = JSON.parse(
  readFileSync(
    process.env.ACCESSLAB_CREDENTIALS ??
      resolve(root, ".local/credentials.json"),
    "utf8",
  ),
);
const browser = await chromium.launch({ channel: "chrome" });
const actors = {};
async function login(name) {
  const context = await browser.newContext({
    baseURL,
    ignoreHTTPSErrors: true,
  });
  actors[name] = context; // Track immediately, including failed logins.
  const page = await context.newPage();
  await page.goto(baseURL + "/auth/login?returnUrl=/api/session");
  await page.locator("[name=username]").fill(name);
  try {
    await page
      .locator("[name=password]")
      .fill(credentials.users[name].password);
  } catch {
    throw new Error("Local fixture input failed; value omitted.");
  }
  await page.locator("#kc-login").click();
  await page.waitForURL((url) => url.origin === baseURL);
  if ((await context.request.get("/api/session")).status() !== 200)
    throw new Error("Demo login failed.");
  actors[name] = context;
}
async function post(actor, path, data) {
  const csrf = await (await actors[actor].request.get("/api/csrf")).json();
  const response = await actors[actor].request.post(path, {
    data,
    headers: {
      "X-CSRF-TOKEN": csrf.token,
      "Idempotency-Key": crypto.randomUUID(),
    },
  });
  if (!response.ok())
    throw new Error(`Demo mutation failed: ${response.status()} ${path}`);
  return response.json();
}
async function get(path) {
  const response = await actors.alice.request.get(path);
  if (!response.ok())
    throw new Error(`Demo cleanup read failed: ${response.status()}`);
  return response.json();
}
async function cleanupRequest(requestId, beforeIds) {
  await cleanupRun({
    requestId,
    beforeIds,
    get,
    post: (path, data) => post("alice", path, data),
    remove: async (membership) => {
      let removalId;
      try {
        const preview = await post("alice", "/api/previews", {
          accountId: membership.accountId,
          groupId: membership.groupId,
          operation: "Remove",
          membershipId: membership.membershipId,
          startsAt: membership.startsAt,
          endsAt: membership.endsAt,
          reason:
            "Demonstration complete; remove only this run's temporary membership.",
        });
        const request = await post("alice", "/api/requests", {
          previewId: preview.id,
        });
        removalId = request.id;
        const review = await post(
          "bob",
          `/api/requests/${request.id}/preview`,
          {},
        );
        await post("bob", `/api/requests/${request.id}/approve`, {
          previewId: review.id,
        });
      } finally {
        if (removalId) await cleanupRequest(removalId, []);
      }
    },
  });
}
let beforeIds = [];
let recording;
let requestId;
try {
  for (const name of ["alice", "bob", "claire"]) await login(name);
  beforeIds = (
    await readMemberships(
      get,
      "00000000-0000-0000-0000-000000000001",
      "analysis",
    )
  ).map((m) => m.membershipId);
  recording = await browser.newContext({
    baseURL,
    ignoreHTTPSErrors: true,
    viewport: { width: 1440, height: 1000 },
    storageState: await actors.alice.storageState(),
    recordVideo: {
      dir: resolve(artifacts, "demo-recording"),
      size: { width: 1440, height: 1000 },
    },
  });
  const page = await recording.newPage();
  const video = page.video();
  const hold = () => page.waitForTimeout(2400); // Deliberate presentation pacing, not readiness polling.
  async function visit(actor, path) {
    await recording.clearCookies();
    await recording.addCookies(await actors[actor].cookies());
    await page.goto(baseURL + path);
    await page.getByRole("heading", { level: 1 }).waitFor();
    await hold();
  }
  await visit("alice", "/access");
  await page.goto(baseURL + "/requests/new");
  await page
    .getByRole("combobox", { name: "Group", exact: true })
    .selectOption("analysis");
  await page
    .getByLabel("End date", { exact: true })
    .fill(new Date(Date.now() + 86400000).toISOString().slice(0, 16));
  await page
    .getByLabel("Reason", { exact: true })
    .fill("Temporary results access for a fictional research session.");
  await hold();
  await page
    .getByRole("button", { name: "Preview impact", exact: true })
    .click();
  await page
    .getByRole("heading", { name: "Review the exact impact" })
    .scrollIntoViewIfNeeded();
  await hold();
  const [submitted] = await Promise.all([
    page.waitForResponse(
      (response) =>
        response.request().method() === "POST" &&
        new URL(response.url()).pathname === "/api/requests",
    ),
    page.getByRole("button", { name: "Submit request", exact: true }).click(),
  ]);
  if (!submitted.ok())
    throw new Error(`Demo submission failed: ${submitted.status()}`);
  requestId = (await submitted.json()).id;
  await page.waitForURL(/\/requests\/(?!new)[^/]+$/);
  await hold();
  await visit("bob", "/requests/" + requestId);
  await page
    .getByRole("button", { name: "Preview impact", exact: true })
    .click();
  await page
    .getByRole("heading", { name: "Review the exact impact" })
    .scrollIntoViewIfNeeded();
  await hold();
  await page.getByRole("button", { name: "Approve", exact: true }).click();
  await page.getByRole("heading", { name: "Decision recorded" }).waitFor();
  await hold();
  await visit("alice", "/access");
  await page
    .getByRole("button", { name: /View .* details/ })
    .last()
    .click();
  await hold();
  await page.keyboard.press("Escape");
  await visit("claire", "/operations");
  await visit("claire", "/audit");
  await recording.close();
  recording = null;
  await video.saveAs(resolve(artifacts, "AccessLab-walkthrough.webm"));
  console.log(
    "Real-browser walkthrough recorded without the authentication exchange.",
  );
} finally {
  try {
    if (requestId && actors.alice && actors.bob)
      await cleanupRequest(requestId, beforeIds);
  } finally {
    try {
      await closeAll(browser.contexts());
    } finally {
      await browser.close();
    }
  }
}
