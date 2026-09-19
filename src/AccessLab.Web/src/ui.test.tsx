import { it, expect, vi } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import {
  ErrorNotice,
  ImpactPreview,
  PreviewConfirmation,
  useMutation,
} from "./ui";
import { ApiError, api } from "./api";
import { useState } from "react";
import { act, renderHook } from "@testing-library/react";
import { useLoad } from "./ui";

it.each(["success", "error"])(
  "serializes deferred polls before a newer %s response",
  async (outcome) => {
    vi.useFakeTimers();
    try {
      let resolve!: (value: { revision: number }) => void;
      const get = vi.spyOn(api, "get").mockImplementationOnce(
        () =>
          new Promise<{ revision: number }>((done) => {
            resolve = done;
          }),
      );
      if (outcome === "success")
        get.mockResolvedValue({ revision: 2 } satisfies { revision: number });
      else get.mockRejectedValue(new ApiError(403, "Access revoked"));
      const { result, unmount } = renderHook(() =>
        useLoad<{ revision: number }>("/api/access", 1000),
      );
      await act(async () => {
        await vi.advanceTimersByTimeAsync(2000);
      });
      expect(get).toHaveBeenCalledTimes(1);
      await act(async () => {
        resolve({ revision: 1 });
      });
      expect(result.current.data?.revision).toBe(1);
      await act(async () => {
        await vi.advanceTimersByTimeAsync(1000);
      });
      expect(get).toHaveBeenCalledTimes(2);
      if (outcome === "success") expect(result.current.data?.revision).toBe(2);
      else expect(result.current.error?.message).toBe("Access revoked");
      unmount();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(2000);
      });
      expect(get).toHaveBeenCalledTimes(2);
    } finally {
      vi.useRealTimers();
    }
  },
);
it("requires a fresh impact rather than confirmation after preview expiration", () => {
  const confirm = vi.fn(),
    refresh = vi.fn();
  render(
    <PreviewConfirmation
      expiresAt="2000-01-01T00:00:00Z"
      busy={false}
      label="Approve"
      onConfirm={confirm}
      onRefresh={refresh}
    />,
  );
  expect(screen.queryByRole("button", { name: "Approve" })).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "Refresh impact" }));
  expect(confirm).not.toHaveBeenCalled();
  expect(refresh).toHaveBeenCalledOnce();
});
it("explains stale impact and provides an explicit refresh action", () => {
  const refresh = vi.fn();
  render(
    <ErrorNotice
      error={new ApiError(409, "Membership changed", "trace-4")}
      onRetry={refresh}
      retryLabel="Refresh impact"
    />,
  );
  expect(screen.getByRole("alert").textContent).toContain("Membership changed");
  expect(screen.getByText("trace-4")).toBeTruthy();
  fireEvent.click(screen.getByRole("button", { name: "Refresh impact" }));
  expect(refresh).toHaveBeenCalledOnce();
});
it("separates immediate and scheduled server impact without hiding retained reasons", () => {
  render(
    <ImpactPreview
      preview={{
        id: "p1",
        accountRevision: 4,
        expiresAt: "2026-09-20T10:00:00Z",
        changes: [
          {
            resourceId: "library",
            resourceName: "Library",
            effect: "Retained",
            reasons: ["Knowledge remains valid"],
          },
        ],
        scheduledChanges: [
          {
            at: "2026-09-21T10:00:00Z",
            changes: [
              {
                resourceId: "results",
                resourceName: "Results",
                effect: "Added",
                reasons: ["Analysis starts"],
              },
            ],
          },
        ],
        warnings: ["Approval is not application"],
      }}
    />,
  );
  expect(
    screen.getByRole("heading", { name: "Immediate impact" }),
  ).toBeTruthy();
  expect(
    screen.getByRole("heading", { name: "Scheduled impact" }),
  ).toBeTruthy();
  expect(screen.getByText("Knowledge remains valid")).toBeTruthy();
  expect(screen.getByText("Account revision 4")).toBeTruthy();
});
it("blocks duplicate clicks and preserves mutation key after network failure", async () => {
  let reject!: (e: Error) => void;
  const send = vi
    .spyOn(api, "post")
    .mockImplementationOnce(
      () =>
        new Promise((_, r) => {
          reject = r;
        }),
    )
    .mockResolvedValueOnce({ id: "ok" });
  function Harness() {
    const mutation = useMutation();
    const [done, setDone] = useState(false);
    return (
      <button
        disabled={mutation.busy}
        onClick={() =>
          void mutation.run("/api/requests", { previewId: "p" }).then((r) => {
            if (r) setDone(true);
          })
        }
      >
        {done ? "Done" : "Send"}
      </button>
    );
  }
  render(<Harness />);
  fireEvent.click(screen.getByRole("button"));
  fireEvent.click(screen.getByRole("button"));
  await waitFor(() => expect(send).toHaveBeenCalledTimes(1));
  reject(new ApiError(503, "Down"));
  await waitFor(() =>
    expect((screen.getByRole("button") as HTMLButtonElement).disabled).toBe(
      false,
    ),
  );
  fireEvent.click(screen.getByRole("button"));
  await screen.findByText("Done");
  expect(send.mock.calls[0][2]).toBe(send.mock.calls[1][2]);
});
