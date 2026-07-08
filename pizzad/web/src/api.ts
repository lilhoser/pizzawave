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
    try {
      return await fetch(path, { ...init, headers });
    } catch (error) {
      if (init.signal)
        throw error;
      return this.xhrFallback(path, { ...init, headers });
    }
  }

  private xhrFallback(path: string, init: RequestInit): Promise<Response> {
    return new Promise((resolve, reject) => {
      const xhr = new XMLHttpRequest();
      xhr.open(init.method || "GET", path, true);
      const headers = new Headers(init.headers);
      headers.forEach((value, key) => xhr.setRequestHeader(key, value));
      xhr.onload = () => {
        const responseHeaders = new Headers();
        for (const line of xhr.getAllResponseHeaders().trim().split(/[\r\n]+/)) {
          if (!line) continue;
          const index = line.indexOf(":");
          if (index > 0)
            responseHeaders.append(line.slice(0, index), line.slice(index + 1).trim());
        }
        resolve(new Response(xhr.responseText, {
          status: xhr.status,
          statusText: xhr.statusText,
          headers: responseHeaders
        }));
      };
      xhr.onerror = () => reject(new Error("Failed to fetch"));
      xhr.ontimeout = () => reject(new Error("Failed to fetch"));
      xhr.onabort = () => reject(new Error("Failed to fetch"));
      if (typeof init.body === "string" || init.body instanceof FormData || init.body == null) {
        xhr.send(init.body ?? null);
      } else {
        reject(new Error("Failed to fetch"));
      }
    });
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
