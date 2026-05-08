export class ApiClient {
  async request<T>(path: string, init: RequestInit = {}): Promise<T> {
    const headers = new Headers(init.headers);
    if (init.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
    const response = await fetch(path, { ...init, headers });
    if (!response.ok) {
      const text = await response.text();
      let message = text || `${response.status} ${response.statusText}`;
      try {
        const parsed = JSON.parse(text);
        message = parsed.message ?? parsed.title ?? message;
      } catch {
        // Keep the raw response text when the body is not JSON.
      }
      throw new Error(message);
    }
    return response.json() as Promise<T>;
  }
}

export const api = new ApiClient();

export function rangeQuery(hours: number): string {
  const end = Math.floor(Date.now() / 1000);
  const start = end - hours * 3600;
  return `start=${start}&end=${end}`;
}

export function rangeBody(hours: number) {
  const end = Math.floor(Date.now() / 1000);
  const start = end - hours * 3600;
  return { start, end };
}
