export class ApiClient {
  token = localStorage.getItem("pizzad-token") ?? "";

  async request<T>(path: string, init: RequestInit = {}): Promise<T> {
    const headers = new Headers(init.headers);
    if (this.token) headers.set("Authorization", `Bearer ${this.token}`);
    if (init.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
    let response = await fetch(path, { ...init, headers });
    if (response.status === 401) {
      const token = window.prompt("PizzaWave token");
      if (token) {
        this.token = token;
        localStorage.setItem("pizzad-token", token);
        headers.set("Authorization", `Bearer ${token}`);
        response = await fetch(path, { ...init, headers });
      }
    }
    if (!response.ok) throw new Error(await response.text());
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
