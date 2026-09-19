import { it, expect, vi } from "vitest";
import {
  render,
  screen,
  fireEvent,
  waitFor,
  within,
} from "@testing-library/react";
import { OperationsPage, AuditPage } from "./operations";
import { api } from "./api";
import type { AuditEvent, Page } from "./types";
const session = {
  accountId: "c",
  displayName: "Claire",
  status: "Active",
  roles: [{ role: "Operator", projectId: null }],
  projectIds: ["atlas"],
  demoMode: false,
};
it("pages operations and their persisted attempt history without creating a retry", async () => {
  HTMLDialogElement.prototype.showModal = function () {
    this.setAttribute("open", "");
  };
  const get = vi.spyOn(api, "get").mockImplementation(
    async (path) =>
      (path.includes("/attempts")
        ? {
            items: [
              {
                id: 1,
                accountId: "a",
                revision: 9,
                attemptedAt: "2026-09-18T10:00:00Z",
                outcome: "Failed",
                error: "<b>Unavailable</b>",
              },
            ],
            total: 26,
            page: 1,
            pageSize: 25,
          }
        : {
            items: [
              {
                accountId: "a",
                accountName: "Alice",
                status: "Failed",
                desiredRevision: 9,
                observedRevision: 8,
                attempts: 5,
                updatedAt: "2026-09-18T10:00:00Z",
                lastError: "Unavailable",
              },
            ],
            total: 26,
            page: 1,
            pageSize: 25,
            failureMode: false,
          }) as never,
  );
  const post = vi.spyOn(api, "post");
  render(<OperationsPage session={session} />);
  fireEvent.click(await screen.findByRole("button", { name: "Next" }));
  await waitFor(() =>
    expect(
      get.mock.calls.some(([p]) => p === "/api/operations?page=2&pageSize=25"),
    ).toBe(true),
  );
  fireEvent.click(
    await screen.findByRole("button", { name: "Attempt history for Alice" }),
  );
  const dialog = await screen.findByRole("dialog");
  await screen.findByText("<b>Unavailable</b>");
  expect(dialog.querySelector("b")).toBeNull();
  expect(dialog.textContent).toContain("Revision");
  fireEvent.click(within(dialog).getByRole("button", { name: "Next" }));
  await waitFor(() =>
    expect(
      get.mock.calls.some(
        ([p]) => p === "/api/operations/a/attempts?page=2&pageSize=25",
      ),
    ).toBe(true),
  );
  expect(post).not.toHaveBeenCalled();
});
it("keeps failure simulation controls absent outside demo mode and retries with a reason", async () => {
  HTMLDialogElement.prototype.showModal = function () {
    this.setAttribute("open", "");
  };
  vi.spyOn(api, "get").mockResolvedValue({
    failureMode: false,
    items: [
      {
        accountId: "a",
        accountName: "Alice",
        status: "Failed",
        desiredRevision: 4,
        observedRevision: 2,
        attempts: 5,
        lastError: "Simulator unavailable",
        updatedAt: "2026-09-18T10:00:00Z",
      },
    ],
  });
  const post = vi.spyOn(api, "post").mockResolvedValue({ accepted: true });
  render(<OperationsPage session={session} />);
  fireEvent.click(await screen.findByRole("button", { name: "Retry Alice" }));
  expect(screen.queryByRole("button", { name: /Enable failure/ })).toBeNull();
  fireEvent.change(screen.getByLabelText("Reason"), {
    target: { value: "Destination recovered" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Retry reconciliation" }));
  await waitFor(() =>
    expect(post.mock.calls[0][1]).toEqual({ reason: "Destination recovered" }),
  );
  expect(await screen.findByRole("status")).toBeTruthy();
});
it("filters the audit server-side and sends bounded pagination without interpreting reason as HTML", async () => {
  const get = vi.spyOn(api, "get").mockResolvedValue({
    items: [
      {
        id: 1,
        at: "2026-09-18T10:00:00Z",
        actorName: "Alice",
        action: "RequestCreated",
        targetId: "r1",
        projectId: null,
        outcome: "Success",
        reason: "<script>alert(1)</script>",
        correlationId: "trace",
        accountRevision: 12,
      },
    ],
    total: 26,
    page: 1,
    pageSize: 25,
  } satisfies Page<AuditEvent>);
  render(<AuditPage />);
  await screen.findByText("<script>alert(1)</script>");
  expect(screen.getByText("Account revision 12")).toBeTruthy();
  fireEvent.change(screen.getByLabelText("Search audit"), {
    target: { value: "trace" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Apply filters" }));
  await waitFor(() =>
    expect(get.mock.calls.some(([p]) => p.includes("search=trace"))).toBe(true),
  );
  fireEvent.click(await screen.findByRole("button", { name: "Next" }));
  await waitFor(() =>
    expect(
      get.mock.calls.some(
        ([p]) =>
          p.includes("page=2") &&
          p.includes("pageSize=25") &&
          p.includes("search=trace"),
      ),
    ).toBe(true),
  );
  expect(document.querySelector("script")).toBeNull();
});
