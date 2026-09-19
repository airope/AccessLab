import { it, expect, vi } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import { SessionGate } from "./session";
import { api, ApiError } from "./api";
import { AccessPage } from "./access";
import { act } from "@testing-library/react";
import type { Access } from "./types";

it.each([403, 503])(
  "closes current authorization details when polling fails with %s",
  async (status) => {
    HTMLDialogElement.prototype.showModal = function () {
      this.setAttribute("open", "");
    };
    const access = {
      accountId: "a",
      accountName: "Alice",
      sync: { status: "InSync", desiredRevision: 2, observedRevision: 2 },
      resources: [
        {
          id: "library",
          name: "Library",
          projectId: "atlas",
          description: "Shared documentation",
          allowed: true,
          observed: true,
          effective: true,
          ruleVersion: 1,
          reasons: [],
        },
      ],
    } satisfies Access;
    vi.spyOn(api, "get")
      .mockResolvedValueOnce(access)
      .mockRejectedValue(
        new ApiError(status, "Authorization could not be verified"),
      );
    vi.useFakeTimers();
    try {
      render(<AccessPage />);
      await act(async () => {});
      fireEvent.click(
        screen.getByRole("button", { name: "View Library details" }),
      );
      expect(screen.getByRole("dialog").textContent).toContain("Available");
      await act(async () => {
        await vi.advanceTimersByTimeAsync(10000);
      });
      expect(screen.queryByRole("dialog")).toBeNull();
      expect(screen.getByRole("alert").textContent).toContain(
        "Authorization could not be verified",
      );
    } finally {
      vi.useRealTimers();
    }
  },
);
it("uses the real OIDC login endpoint when unauthenticated, without credential fields", async () => {
  vi.spyOn(api, "get").mockRejectedValue(new ApiError(401, "Sign in"));
  render(<SessionGate>{() => <div>Workspace</div>}</SessionGate>);
  expect(
    (
      await screen.findByRole("link", { name: "Continue with Keycloak" })
    ).getAttribute("href"),
  ).toContain("/auth/login?returnUrl=");
  expect(screen.queryByLabelText(/password/i)).toBeNull();
  const alternate = screen.getByRole("link", { name: "Use another account" });
  const url = new URL(
    alternate.getAttribute("href")!,
    "https://localhost:5443",
  );
  expect(url.pathname).toBe("/auth/login");
  expect(url.searchParams.get("prompt")).toBe("login");
  expect(url.searchParams.get("returnUrl")).toBe("/access");
});
it("does not reveal the workspace to unknown or inactive identities", async () => {
  vi.spyOn(api, "get").mockRejectedValue(
    new ApiError(403, "No active account is configured."),
  );
  render(<SessionGate>{() => <div>Private workspace</div>}</SessionGate>);
  await screen.findByText("Access is not configured");
  expect(screen.queryByText("Private workspace")).toBeNull();
  expect(screen.getByRole("button", { name: "Sign out" })).toBeTruthy();
});
it.each(["Account is Suspended.", "Account is outside its validity window."])(
  "explains denial despite existing group paths: %s",
  async (denialReason) => {
    HTMLDialogElement.prototype.showModal = function () {
      this.setAttribute("open", "");
    };
    vi.spyOn(api, "get").mockResolvedValue({
      accountId: "a",
      accountName: "Alice",
      sync: { status: "Pending", desiredRevision: 4, observedRevision: 3 },
      resources: [
        {
          id: "library",
          name: "Library",
          projectId: "atlas",
          description: "Library",
          allowed: false,
          observed: true,
          effective: false,
          ruleVersion: 2,
          ruleDescription: "Active account and valid membership",
          denialReason,
          reasons: [
            {
              groupId: "knowledge",
              groupName: "Knowledge",
              startsAt: "2026-01-01T00:00:00Z",
              endsAt: "2026-12-01T00:00:00Z",
            },
            {
              groupId: "analysis",
              groupName: "Analysis",
              startsAt: "2026-01-01T00:00:00Z",
              endsAt: "2026-12-01T00:00:00Z",
            },
          ],
        },
      ],
    });
    render(<AccessPage accountId="a" />);
    fireEvent.click(
      await screen.findByRole("button", { name: "View Library details" }),
    );
    expect(screen.getByText(denialReason).className).toContain("warning");
    const dialog = screen.getByRole("dialog");
    expect(dialog.textContent).toContain("Denied");
    expect(dialog.textContent).toContain("Knowledge");
    expect(dialog.textContent).toContain("Analysis");
    expect(dialog.textContent).toContain(
      "do not override account status or validity restrictions",
    );
  },
);
it("shows access paths, local and observed distinction, and tests protected content", async () => {
  vi.spyOn(api, "get").mockImplementation(async (path) =>
    path.includes("/content")
      ? { resourceId: "library", message: "Protected library" }
      : ({
          accountId: "a",
          accountName: "Alice",
          sync: { status: "Pending", desiredRevision: 2, observedRevision: 1 },
          resources: [
            {
              id: "library",
              name: "Library",
              projectId: "atlas",
              description: "Shared documentation",
              allowed: true,
              observed: true,
              effective: true,
              ruleVersion: 2,
              ruleDescription: "Active account and valid Knowledge membership",
              denialReason: null,
              reasons: [
                {
                  groupId: "knowledge",
                  groupName: "Knowledge",
                  startsAt: "2026-01-01T00:00:00Z",
                  endsAt: "2026-12-01T00:00:00Z",
                },
              ],
            },
          ],
        } as never),
  );
  HTMLDialogElement.prototype.showModal = function () {
    this.setAttribute("open", "");
  };
  render(<AccessPage />);
  fireEvent.click(
    await screen.findByRole("button", { name: "View Library details" }),
  );
  expect(screen.getByRole("dialog").textContent).toContain("Knowledge");
  expect(screen.getByRole("dialog").textContent).toContain(
    "Active account and valid Knowledge membership",
  );

  fireEvent.click(screen.getByRole("button", { name: "Test resource access" }));
  expect(await screen.findByText("Protected library")).toBeTruthy();
});
