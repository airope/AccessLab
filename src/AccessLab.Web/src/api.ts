export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
    public correlationId?: string,
    public code?: string,
  ) {
    super(message);
    this.name = "ApiError";
  }
}
export class ApiClient {
  private csrf?: string;
  constructor(private fetcher: typeof fetch = (...args) => fetch(...args)) {}
  private async request<T>(path: string, init: RequestInit = {}): Promise<T> {
    let response: Response;
    try {
      response = await this.fetcher(path, {
        ...init,
        credentials: "include",
        headers: { Accept: "application/json", ...init.headers },
      });
    } catch {
      throw new ApiError(
        0,
        "The service could not be reached. Your input is preserved. Check your connection and try again.",
      );
    }
    if (!response.ok) {
      const problem = await response.json().catch(() => ({}));
      throw new ApiError(
        response.status,
        problem.detail ||
          problem.title ||
          "The request could not be completed.",
        problem.correlationId,
        problem.code,
      );
    }
    if (response.status === 204) return null as T;
    return (await response.json()) as T;
  }
  get<T>(path: string, signal?: AbortSignal): Promise<T> {
    return this.request<T>(path, { signal });
  }
  async post<T>(path: string, body: unknown, key: string): Promise<T> {
    if (!this.csrf)
      this.csrf = (await this.get<{ token: string }>("/api/csrf")).token;
    try {
      return await this.request<T>(path, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-CSRF-TOKEN": this.csrf,
          "Idempotency-Key": key,
        },
        body: JSON.stringify(body),
      });
    } catch (error) {
      if (
        error instanceof ApiError &&
        (error.status === 401 || error.status === 403)
      )
        this.csrf = undefined;
      throw error;
    }
  }
}
export const api = new ApiClient();
export const query = (values: Record<string, string | number | undefined>) => {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values))
    if (value !== undefined && value !== "") params.set(key, String(value));
  return params.toString();
};
