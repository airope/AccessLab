import { it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { RequestReview } from "./requests";
import { api } from "./api";
it("renders only the accepted diagnostic snapshot as read-only inert text", async () => {
  vi.spyOn(api, "get").mockResolvedValue({
    id: "r",
    operation: "Add",
    groupName: "Analysis",
    status: "Approved",
    reason: "Research",
    startsAt: "2026-09-19T10:00:00Z",
    endsAt: "2026-09-20T10:00:00Z",
    acceptedPreviewId: "accepted-p",
    decisionContext: {
      accountRevision: 7,
      ruleVersion: 3,
      fingerprint: "safe-fingerprint",
      calculatedAt: "2026-09-19T09:00:00Z",
      expiresAt: "2026-09-19T09:05:00Z",
      command: {
        accountId: "a",
        groupId: "analysis",
        operation: "Add",
        startsAt: "2026-09-19T10:00:00Z",
        endsAt: "2026-09-20T10:00:00Z",
        reason: "<img src=x onerror=alert(1)>",
        membershipId: "m1",
      },
      secret: "must-not-render",
    },
  });
  const { container } = render(<RequestReview id="r" />);
  await screen.findByRole("heading", { name: "Accepted decision context" });
  expect(screen.getByText("accepted-p")).toBeTruthy();
  expect(screen.getByText("safe-fingerprint")).toBeTruthy();
  expect(
    screen.getByText("Account revision").nextElementSibling?.textContent,
  ).toBe("7");
  expect(screen.getByText("Rule version").nextElementSibling?.textContent).toBe(
    "3",
  );
  expect(screen.getByText("<img src=x onerror=alert(1)>")).toBeTruthy();
  expect(container.querySelector("img, input, textarea")).toBeNull();
  expect(screen.queryByText("must-not-render")).toBeNull();
  expect(screen.getByText("safe-fingerprint").closest("dl")?.className).toBe(
    "facts",
  );
});
