export const sessionEvents = new EventTarget();

export async function apiFetch(url: string, token?: string, init?: RequestInit): Promise<Response> {
  const headers = new Headers(init?.headers);
  if (token) headers.set('Authorization', `Bearer ${token}`);
  const response = await fetch(url, { ...init, headers });
  if (response.status === 401 && token) {
    sessionEvents.dispatchEvent(new CustomEvent('expired', { detail: token }));
  }
  if (!response.ok) {
    const payload: unknown = await response.json().catch(() => null);
    const detail = payload && typeof payload === 'object' && 'detail' in payload ? payload.detail : null;
    const message = typeof detail === 'string' ? detail : `HTTP ${response.status}`;
    throw new Error(message);
  }
  return response;
}

export async function apiJson<T>(url: string, token?: string, init?: RequestInit): Promise<T> {
  return (await apiFetch(url, token, init)).json() as Promise<T>;
}

export async function apiPage<T>(url: string, token?: string, init?: RequestInit): Promise<{ items: T[]; next: string | null }> {
  const response = await apiFetch(url, token, init);
  return { items: (await response.json()) as T[], next: response.headers.get('X-Next-Cursor') };
}
