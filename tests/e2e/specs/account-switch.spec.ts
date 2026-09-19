import { test, expect } from "@playwright/test";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

const baseURL = process.env.ACCESSLAB_URL ?? "https://localhost:5443";
import { closeAll } from "../cleanup.mjs";

// Also covers partial login/setup failures and exceptions thrown by cleanup.
test.afterEach(async ({ browser }) => {
  await closeAll(browser.contexts());
});

const root = resolve(import.meta.dirname, "../../..");
function passwordFor(user: string): string {
  const data = JSON.parse(
    readFileSync(
      process.env.ACCESSLAB_CREDENTIALS ??
        resolve(root, ".local/credentials.json"),
      "utf8",
    ),
  );
  const password = data.users?.[user]?.password;
  if (!password) throw new Error("Generated fixture credentials missing.");
  return password;
}

test("Account switch: retain SSO convenience but choose Alice after Bob signs out", async ({
  browser,
}) => {
  const context = await browser.newContext({
    baseURL,
    ignoreHTTPSErrors: true,
  });
  const page = await context.newPage();
  async function enterCredentials(user: string) {
    await page.locator('[name="username"]').fill(user);
    try {
      await page.locator('[name="password"]').fill(passwordFor(user));
    } catch {
      throw new Error("Fixture credential input failed; value omitted.");
    }
    await page.locator("#kc-login").click();
    await page.waitForURL((url) => url.origin === baseURL);
  }
  async function identity() {
    const response = await context.request.get("/api/session");
    expect(response.status()).toBe(200);
    return (await response.json()).displayName;
  }
  try {
    await page.goto(baseURL + "/auth/login?returnUrl=/access");
    await enterCredentials("bob");
    expect(await identity()).toBe("Bob");
    await page.getByRole("button", { name: "Sign out", exact: true }).click();
    await expect(
      page.getByRole("heading", { name: "Sign in", exact: true }),
    ).toBeVisible();
    expect((await context.request.get("/api/session")).status()).toBe(401);
    // Same browser context: normal login should still reuse Keycloak's SSO cookie.
    await page.getByRole("link", { name: "Continue with Keycloak" }).click();
    await expect(
      page.getByRole("heading", { name: "My access", exact: true }),
    ).toBeVisible();
    expect(await identity()).toBe("Bob");
    await page.getByRole("button", { name: "Sign out", exact: true }).click();
    await expect(
      page.getByRole("heading", { name: "Sign in", exact: true }),
    ).toBeVisible();
    const switchAccount = page.getByRole("link", {
      name: "Use another account",
      exact: true,
    });
    expect(
      new URL(
        (await switchAccount.getAttribute("href"))!,
        baseURL,
      ).searchParams.get("prompt"),
    ).toBe("login");
    await switchAccount.click();
    // Keycloak keeps the current username on re-authentication; its native
    // restart action opens the account-entry form without clearing cookies.
    await expect(
      page.getByRole("button", { name: "Restart login" }),
    ).toBeVisible();

    await page.getByRole("button", { name: "Restart login" }).click();
    await expect(page.locator('[name="username"]')).toBeVisible();
    await enterCredentials("alice");
    await expect(
      page.getByRole("heading", { name: "My access", exact: true }),
    ).toBeVisible();
    expect(await identity()).toBe("Alice");
    const session = await (await context.request.get("/api/session")).json();
    expect(
      session.roles.some((r: { role: string }) => r.role === "GroupOwner"),
    ).toBe(false);
  } finally {
    await context.close();
  }
});
