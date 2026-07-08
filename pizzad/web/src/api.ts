export type AuthTokenRequest = {
  method: string;
  path: string;
};

export type AuthTokenProvider = (request: AuthTokenRequest) => Promise<string | null>;

export class ApiClient {
  private authTokenProvider: AuthTokenProvider | null = null;

  setAuthTokenProvider(provider: AuthTokenProvider | null) {
    this.authTokenProvider = provider;
  }

  async request<T>(path: string, init: RequestInit = {}): Promise<T> {
    const response = await this.fetchWithAuth(path, init, false);
    if (response.status === 401) {
      localStorage.removeItem("pizzawave-admin-token");
      const token = await this.authTokenProvider?.({ method: init.method || "GET", path });
      if (token?.trim()) {
        localStorage.setItem("pizzawave-admin-token", token.trim());
        const retry = await this.fetchWithAuth(path, init, true);
        if (retry.ok)
          return retry.json() as Promise<T>;
        return this.throwResponse(retry);
      }
      throw new Error("PizzaWave admin token is required for this action.");
    }

    if (!response.ok) {
      return this.throwResponse(response);
    }
    return response.json() as Promise<T>;
  }

  async download(path: string, init: RequestInit = {}): Promise<Response> {
    const response = await this.fetchWithAuth(path, init, false);
    if (response.status === 401) {
      localStorage.removeItem("pizzawave-admin-token");
      const token = await this.authTokenProvider?.({ method: init.method || "GET", path });
      if (token?.trim()) {
        localStorage.setItem("pizzawave-admin-token", token.trim());
        const retry = await this.fetchWithAuth(path, init, true);
        if (retry.ok)
          return retry;
        return this.throwResponse(retry);
      }
      throw new Error("PizzaWave admin token is required for this action.");
    }

    if (!response.ok) {
      return this.throwResponse(response);
    }
    return response;
  }

  private async fetchWithAuth(path: string, init: RequestInit, forceAuth: boolean): Promise<Response> {
    const headers = new Headers(init.headers);
    if (init.body && !(init.body instanceof FormData) && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
    const token = localStorage.getItem("pizzawave-admin-token");
    if ((forceAuth || token) && token && !headers.has("Authorization"))
      headers.set("Authorization", `Bearer ${token}`);
    return fetch(path, { ...init, headers });
  }

  private async throwResponse(response: Response): Promise<never> {
    const text = await response.text();
    let message = text || `${response.status} ${response.statusText}`;
    try {
      const parsed = JSON.parse(text);
      message = parsed.message ?? parsed.error ?? parsed.title ?? message;
    } catch {
      // Keep the raw response text when the body is not JSON.
    }
    throw new Error(message);
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
