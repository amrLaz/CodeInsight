/**
 * Minimal CodeInsights API client. It knows the transport contract of the hub —
 * Bearer token, snake_case JSON, `application/problem+json` errors and the
 * `X-Next-Cursor` pagination header — and nothing about the domain, so it stays
 * usable by every feature and testable without the VS Code runtime.
 */

export type Fetch = (input: string, init?: RequestInit) => Promise<Response>;

export type ClientOptions = {
  serverUrl: string;
  token: () => Promise<string | undefined>;
  fetch?: Fetch;
  onUnauthorized?: () => void;
};

export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

export type Page<T> = { items: T[]; next: string | null };

/** Joins the configured hub URL and a route without duplicating or dropping separators. */
export function resolveUrl(serverUrl: string, path: string): string {
  if (serverUrl.trim().length === 0) throw new ApiError(0, 'CodeInsights server URL is not configured');
  const base = serverUrl.trim().replace(/\/+$/, '');
  const suffix = path.startsWith('/') ? path : `/${path}`;
  return `${base}${suffix}`;
}

/** Extracts the `detail` of a problem+json body, falling back to the status code. */
export async function problemDetail(response: Response): Promise<string> {
  const payload: unknown = await response.json().catch(() => null);
  if (payload && typeof payload === 'object' && 'detail' in payload) {
    const detail = (payload as { detail?: unknown }).detail;
    if (typeof detail === 'string' && detail.length > 0) return detail;
  }
  return `HTTP ${response.status}`;
}

export class CodeInsightsApi {
  private readonly fetch: Fetch;

  constructor(private readonly options: ClientOptions) {
    this.fetch = options.fetch ?? ((input, init) => globalThis.fetch(input, init));
  }

  async request(path: string, init?: RequestInit): Promise<Response> {
    const headers = new Headers(init?.headers);
    headers.set('Accept', 'application/json');
    const token = await this.options.token();
    if (token) headers.set('Authorization', `Bearer ${token}`);
    const response = await this.fetch(resolveUrl(this.options.serverUrl, path), { ...init, headers });
    if (response.status === 401) {
      this.options.onUnauthorized?.();
      throw new ApiError(401, await problemDetail(response));
    }
    if (!response.ok) throw new ApiError(response.status, await problemDetail(response));
    return response;
  }

  async json<T>(path: string, init?: RequestInit): Promise<T> {
    return (await this.request(path, init)).json() as Promise<T>;
  }

  /** Reads one page and exposes the opaque continuation cursor returned by the hub. */
  async page<T>(path: string, init?: RequestInit): Promise<Page<T>> {
    const response = await this.request(path, init);
    return { items: (await response.json()) as T[], next: response.headers.get('X-Next-Cursor') };
  }

  /** Reads a JSON body of any shape together with the continuation cursor. */
  async jsonWithCursor<T>(path: string, init?: RequestInit): Promise<{ body: T; next: string | null }> {
    const response = await this.request(path, init);
    return { body: (await response.json()) as T, next: response.headers.get('X-Next-Cursor') };
  }

  /** Follows `X-Next-Cursor` until exhaustion or `limit` items, whichever comes first. */
  async all<T>(path: string, limit = 1000): Promise<T[]> {
    const items: T[] = [];
    let cursor: string | null = null;
    do {
      const url = cursor === null ? path : `${path}${path.includes('?') ? '&' : '?'}cursor=${encodeURIComponent(cursor)}`;
      const page: Page<T> = await this.page<T>(url);
      items.push(...page.items);
      cursor = page.next;
    } while (cursor !== null && items.length < limit);
    return items.slice(0, limit);
  }
}
