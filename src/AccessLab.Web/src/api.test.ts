import { describe, it, expect, vi } from "vitest";
import { ApiClient } from "./api";
describe("authenticated mutation boundary", () => {
  it("treats a successful empty logout response as success", async () => {
    const fetcher = vi
      .fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ token: "csrf" })))
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    await expect(
      new ApiClient(fetcher).post("/auth/logout", {}, crypto.randomUUID()),
    ).resolves.toBeNull();
  });
  it("fetches CSRF and sends credentials and stable idempotency key on a retry", async () => {
    const fetcher = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ token: "csrf-value" })),
      )
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            title: "Unavailable",
            detail: "Try again",
            correlationId: "c-1",
          }),
          { status: 503 },
        ),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ id: "created" }), { status: 201 }),
      );
    const api = new ApiClient(fetcher);
    const key = crypto.randomUUID();
    await expect(
      api.post("/api/requests", { previewId: "p-1" }, key),
    ).rejects.toMatchObject({ status: 503, correlationId: "c-1" });
    await expect(
      api.post("/api/requests", { previewId: "p-1" }, key),
    ).resolves.toEqual({ id: "created" });
    const mutations = fetcher.mock.calls.filter(
      ([url]) => url === "/api/requests",
    );
    expect(mutations).toHaveLength(2);
    for (const [, init] of mutations) {
      expect(init.credentials).toBe("include");
      expect(init.headers["X-CSRF-TOKEN"]).toBe("csrf-value");
      expect(init.headers["Idempotency-Key"]).toBe(key);
      expect(init.body).toBe('{"previewId":"p-1"}');
    }
  });
});
