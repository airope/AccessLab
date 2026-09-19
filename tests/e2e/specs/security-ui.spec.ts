import {
  test,
  expect,
  type Browser,
  type BrowserContext,
  type Page,
  type Locator,
} from "@playwright/test";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

import { closeAll } from "../cleanup.mjs";

// Also covers partial login/setup failures and exceptions thrown by cleanup.
test.afterEach(async ({ browser }) => {
  await closeAll(browser.contexts());
});

const root = resolve(import.meta.dirname, "../../..");
const baseURL = process.env.ACCESSLAB_URL ?? "https://localhost:5443";
// Never capture credentials, storageState, video or traces in these tests.
test.use({ trace: "off", screenshot: "off", video: "off" });

async function login(browser: Browser, user = "alice", status = 200) {
  const context = await browser.newContext({
    baseURL,
    ignoreHTTPSErrors: true,
    locale: "en-US",
    viewport: { width: 390, height: 844 },
  });
  const page = await context.newPage();
  try {
    await page.goto(`${baseURL}/auth/login?returnUrl=/api/session`);
    await page.locator('input[name="username"]').fill(user);
    try {
      const data = JSON.parse(
        readFileSync(
          process.env.ACCESSLAB_CREDENTIALS ??
            resolve(root, ".local/credentials.json"),
          "utf8",
        ),
      );
      const password = data.users?.[user]?.password;
      if (typeof password !== "string" || !password) throw new Error();
      await page.locator('input[name="password"]').fill(password);
    } catch {
      throw new Error(
        "Disposable fixture credential input unavailable; value omitted.",
      );
    }
    await page.locator("#kc-login").click();
    await page.waitForURL(`${baseURL}/api/session`);
    expect((await context.request.get("/api/session")).status()).toBe(status);
    return { context, page };
  } catch (error) {
    await context.close();
    throw error;
  }
}

function sanity(page: Page, allowedSessionStatuses: number[] = []) {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  page.on("console", (message) => {
    if (!["error", "warning"].includes(message.type())) return;
    const location = message.location().url;
    if (
      location === `${baseURL}/api/session` &&
      allowedSessionStatuses.some((status) =>
        new RegExp(`Failed to load resource:.*\\b${status}\\b`).test(
          message.text(),
        ),
      )
    )
      return;
    errors.push(message.text());
  });
  return () =>
    expect(errors, "No unexpected browser errors or warnings").toEqual([]);
}

async function mutate(
  context: BrowserContext,
  path: string,
  data: unknown,
  key = crypto.randomUUID(),
) {
  const csrf = await context.request.get("/api/csrf");
  expect(csrf.status()).toBe(200);
  return context.request.post(path, {
    data,
    headers: {
      "X-CSRF-TOKEN": (await csrf.json()).token,
      "Idempotency-Key": key,
    },
  });
}

async function cleanup(context: BrowserContext, id?: string) {
  if (!id) return;
  const response = await context.request.get(`/api/requests/${id}`);
  expect(response.status()).toBe(200);
  if ((await response.json()).status === "Pending") {
    expect(
      (
        await mutate(context, `/api/requests/${id}/cancel`, {
          reason: "Security acceptance cleanup",
        })
      ).status(),
    ).toBe(200);
  }
}

async function prepare(page: Page, reason: string) {
  await page.goto(`${baseURL}/requests/new`);
  await page
    .getByRole("combobox", { name: "Group", exact: true })
    .selectOption("analysis");
  await page
    .getByLabel("End date", { exact: true })
    .fill(new Date(Date.now() + 86400000).toISOString().slice(0, 16));
  await page.getByLabel("Reason", { exact: true }).fill(reason);
  await page
    .getByRole("button", { name: "Preview impact", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: "Review the exact impact" }),
  ).toBeVisible();
}

async function submit(page: Page) {
  await page
    .getByRole("button", { name: "Submit request", exact: true })
    .click();
  await expect(page).toHaveURL(/\/requests\/(?!new)[^/]+$/);
  return new URL(page.url()).pathname.split("/").at(-1)!;
}

// Uses actual Tab traversal, not locator.focus(), DOM focus(), fill() or selectOption().
async function tabTo(page: Page, target: Locator) {
  await expect(target).toBeVisible();
  for (let n = 0; n < 80; n++) {
    if (
      await target.evaluate((element) => element === document.activeElement)
    ) {
      await expect(target).toBeFocused();
      return;
    }
    await page.keyboard.press("Tab");
  }
  throw new Error("Target could not be reached within 80 Tab presses");
}
async function activate(page: Page, name: string) {
  const button = page.getByRole("button", { name, exact: true });
  await expect(button).toBeEnabled();
  await tabTo(page, button);
  await page.keyboard.press("Enter");
}

test("SEC-UI: persisted justification remains inert on author and reviewer screens", async ({
  browser,
}) => {
  const alice = await login(browser);
  const bob = await login(browser, "bob");
  const checks = [sanity(alice.page), sanity(bob.page)];
  const payload = '<img src=x onerror="window.__accesslabXss=1">';
  let id: string | undefined;
  try {
    await prepare(alice.page, payload);
    id = await submit(alice.page);
    const persisted = await alice.context.request.get(`/api/requests/${id}`);
    expect(persisted.status()).toBe(200);
    expect((await persisted.json()).reason).toBe(payload);
    for (const page of [alice.page, bob.page]) {
      // Reload from persistence rather than trusting form state.
      await page.goto(`${baseURL}/requests/${id}`);
      await expect(page.getByText(payload, { exact: true })).toBeVisible();
      expect(
        await page
          .getByText(payload, { exact: true })
          .evaluate((el) => el.childElementCount),
      ).toBe(0);
      await expect(page.locator('img[src="x"], [onerror]')).toHaveCount(0);
      expect(
        await page.evaluate(
          () => (window as Window & { __accesslabXss?: number }).__accesslabXss,
        ),
      ).toBeUndefined();
    }
    for (const check of checks) check();
  } finally {
    try {
      await cleanup(alice.context, id);
    } finally {
      await Promise.all([alice.context.close(), bob.context.close()]);
    }
  }
});

test("SEC-UI: narrow keyboard-only request preview submit and cancellation", async ({
  browser,
}) => {
  const { context, page } = await login(browser);
  const check = sanity(page);
  let id: string | undefined;
  try {
    // Login setup is excluded. After this navigation all UI input is keyboard-only.
    await page.goto(`${baseURL}/requests/new`);
    const group = page.getByRole("combobox", { name: "Group", exact: true });
    await tabTo(page, group);
    // Native macOS selects commit type-ahead on Enter (Tab can dismiss arrows).
    const label = await group.locator('option[value="analysis"]').textContent();
    expect(label).toBeTruthy();
    await page.keyboard.type(label!);
    await page.keyboard.press("Enter");
    await page.keyboard.press("Tab");
    await expect(group).toHaveValue("analysis");
    const end = page.getByLabel("End date", { exact: true });
    await tabTo(page, end);
    const tomorrow = new Date(Date.now() + 86400000);
    // Native date segments follow the host locale, not Playwright's locale.
    // Read AX focus only; all changes still come from keyboard events.
    const cdp = await context.newCDPSession(page);
    const values: Record<string, string> = {
      Day: String(tomorrow.getUTCDate()).padStart(2, "0"),
      Jour: String(tomorrow.getUTCDate()).padStart(2, "0"),
      Month: String(tomorrow.getUTCMonth() + 1).padStart(2, "0"),
      Mois: String(tomorrow.getUTCMonth() + 1).padStart(2, "0"),
      Year: String(tomorrow.getUTCFullYear()),
      Année: String(tomorrow.getUTCFullYear()),
      Hours: "11",
      Heures: "11",
      Minutes: "30",
      "AM/PM": "a",
    };
    const focusedSegment = async () => {
      const tree = await cdp.send("Accessibility.getFullAXTree");
      return tree.nodes.find(
        (node) =>
          node.role?.value === "spinbutton" &&
          node.properties?.some(
            (property) =>
              property.name === "focused" && property.value.value === true,
          ),
      )?.name?.value as string | undefined;
    };
    for (let n = 0; n < 6; n++) await page.keyboard.press("ArrowLeft");
    const segmentName = (raw?: string) =>
      raw &&
      Object.keys(values).find(
        (label) => raw === label || raw.startsWith(label + " "),
      );
    const entered = new Set<string>();
    for (let n = 0; n < 7; n++) {
      const rawName = await focusedSegment();
      // Labels may be repeated by Chromium when the input has an aria-label.
      const name = segmentName(rawName);
      if (!name || entered.has(name)) break;
      expect(
        values[name],
        `Supported native date segment: ${name}`,
      ).toBeTruthy();
      await page.keyboard.type(values[name]);
      entered.add(name);
      if (segmentName(await focusedSegment()) === name)
        await page.keyboard.press("ArrowRight");
    }
    await cdp.detach();
    await page.keyboard.press("Tab");
    await expect(end).toHaveValue(
      `${tomorrow.toISOString().slice(0, 10)}T11:30`,
    );
    await tabTo(page, page.getByLabel("Reason", { exact: true }));
    await page.keyboard.type(
      "Keyboard-only narrow viewport acceptance request",
    );
    await activate(page, "Preview impact");
    await expect(
      page.getByRole("heading", { name: "Review the exact impact" }),
    ).toBeVisible();
    await activate(page, "Submit request");
    await expect(page).toHaveURL(/\/requests\/(?!new)[^/]+$/);
    id = new URL(page.url()).pathname.split("/").at(-1)!;
    expect(
      (await (await context.request.get(`/api/requests/${id}`)).json()).status,
    ).toBe("Pending");
    await activate(page, "Cancel request");
    await tabTo(page, page.getByLabel("Reason", { exact: true }));
    await page.keyboard.type(
      "Keyboard cancellation completed without pointer input",
    );
    await activate(page, "Confirm cancellation");
    await expect(
      page.getByRole("heading", { name: "Decision recorded" }),
    ).toBeVisible();
    expect(
      (await (await context.request.get(`/api/requests/${id}`)).json()).status,
    ).toBe("Cancelled");
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth + 1,
      ),
    ).toBe(true);
    check();
  } finally {
    try {
      await cleanup(context, id);
    } finally {
      await context.close();
    }
  }
});

test("SEC-UI: invalid CSRF preserves pending request and stable key remains usable", async ({
  browser,
}) => {
  const { context, page } = await login(browser);
  const check = sanity(page);
  let id: string | undefined;
  try {
    await prepare(page, "CSRF acceptance request; no membership approval");
    id = await submit(page);
    const before = await (
      await context.request.get(`/api/requests/${id}`)
    ).json();
    const key = crypto.randomUUID();
    const data = { reason: "Stable-key CSRF cancellation verification" };
    for (let attempt = 0; attempt < 2; attempt++) {
      const refused = await context.request.post(`/api/requests/${id}/cancel`, {
        data,
        headers: {
          "X-CSRF-TOKEN": "invalid-acceptance-token",
          "Idempotency-Key": key,
        },
      });
      expect([400, 403]).toContain(refused.status());
      expect(
        await (await context.request.get(`/api/requests/${id}`)).json(),
      ).toEqual(before);
    }
    expect(
      (await mutate(context, `/api/requests/${id}/cancel`, data, key)).status(),
    ).toBe(200);
    expect(
      (await (await context.request.get(`/api/requests/${id}`)).json()).status,
    ).toBe("Cancelled");
    check();
  } finally {
    try {
      await cleanup(context, id);
    } finally {
      await context.close();
    }
  }
});

test("SEC-UI: active Alice logout rejects missing CSRF then keyboard signout revokes session", async ({
  browser,
}) => {
  const { context, page } = await login(browser);
  const check = sanity(page, [401]);
  try {
    const before = await (await context.request.get("/api/session")).json();
    expect(before.accountId).toBe("00000000-0000-0000-0000-000000000001");
    expect([400, 403]).toContain(
      (await context.request.post("/auth/logout", { data: {} })).status(),
    );
    const stillActive = await context.request.get("/api/session");
    expect(stillActive.status()).toBe(200);
    expect((await stillActive.json()).accountId).toBe(before.accountId);
    await page.goto(`${baseURL}/access`);
    await activate(page, "Menu");
    await activate(page, "Sign out");
    await expect
      .poll(async () => (await context.request.get("/api/session")).status())
      .toBe(401);
    await expect(
      page.getByRole("button", { name: "Sign out", exact: true }),
    ).toHaveCount(0);
    expect((await context.request.get("/api/access")).status()).toBe(401);
    await expect(
      page.getByRole("link", { name: "Continue with Keycloak" }),
    ).toBeVisible();
    check();
  } finally {
    await context.close();
  }
});

test("SEC-UI: real unconfigured OIDC cookie can sign out without ending shared identities", async ({
  browser,
}) => {
  // This covers the unmapped-identity 403 gate, NOT the distinct Ended lifecycle.
  const { context, page } = await login(browser, "unknown", 403);
  const check = sanity(page, [403, 401]);
  try {
    await page.goto(`${baseURL}/access`);
    await expect(
      page.getByText("Access is not configured", { exact: true }),
    ).toBeVisible();
    expect((await context.request.get("/api/access")).status()).toBe(403);
    check();
    // The post-logout page legitimately receives 401 instead of the earlier 403.
    await activate(page, "Sign out");
    await expect
      .poll(async () => (await context.request.get("/api/session")).status())
      .toBe(401);
    await expect(
      page.getByRole("link", { name: "Continue with Keycloak" }),
    ).toBeVisible();
    check();
  } finally {
    await context.close();
  }
});
