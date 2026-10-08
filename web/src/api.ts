import type {
  Analytics,
  ItemDetail,
  PreventionReport,
  WorklistResponse,
  WorklistRow,
} from "./types";

/**
 * One place where a token is attached and one place where a failure becomes a message.
 *
 * The token lives in localStorage because it is a bearer credential the reviewer types in to try
 * the product; it is never sent anywhere but the API's own origin, and the proxy means the
 * browser only ever talks to one host. Swapping this for a cookie session would be the change to
 * make before this went anywhere real.
 */
const TOKEN_KEY = "denials.token";

export function getToken(): string {
  return localStorage.getItem(TOKEN_KEY) ?? "";
}

export function setToken(token: string): void {
  if (token) localStorage.setItem(TOKEN_KEY, token);
  else localStorage.removeItem(TOKEN_KEY);
}

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      ...(getToken() ? { Authorization: `Bearer ${getToken()}` } : {}),
      ...(init.headers ?? {}),
    },
  });

  if (response.status === 401 || response.status === 403) {
    // The API returns an identical body for "no token" and "wrong token", so the message must
    // not claim to know which one it was — it does not, and pretending otherwise would be the
    // UI inventing a fact the server deliberately withheld.
    throw new ApiError(
      response.status,
      response.status === 401
        ? "Not signed in — check the bearer token."
        : "Signed in, but this token's role may not do that.",
    );
  }

  if (!response.ok) {
    let detail = response.statusText;
    try {
      const body = (await response.json()) as { error?: string };
      if (body.error) detail = body.error;
    } catch {
      /* a non-JSON error body is fine; the status already said what happened */
    }
    throw new ApiError(response.status, detail);
  }

  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

export const api = {
  worklist: (params: Record<string, string> = {}) => {
    const query = new URLSearchParams(
      Object.entries(params).filter(([, v]) => v !== "" && v !== undefined),
    );
    const suffix = query.toString() ? `?${query.toString()}` : "";
    return request<WorklistResponse>(`/api/worklist${suffix}`);
  },

  item: (claimId: string) =>
    request<ItemDetail>(`/api/worklist/${encodeURIComponent(claimId)}`),

  setStatus: (claimId: string, status: string, note?: string) =>
    request<{ claimId: string; status: string; changed: boolean; row: WorklistRow }>(
      `/api/worklist/${encodeURIComponent(claimId)}/status`,
      { method: "POST", body: JSON.stringify({ status, note: note ?? null }) },
    ),

  assign: (claimId: string, assignee: string | null, note?: string) =>
    request<{ claimId: string; assignee: string | null; changed: boolean }>(
      `/api/worklist/${encodeURIComponent(claimId)}/assign`,
      { method: "POST", body: JSON.stringify({ assignee, note: note ?? null }) },
    ),

  draft: (claimId: string) =>
    request<{ claimId: string; produced: boolean; verdict: string | null; reason: string | null }>(
      `/api/worklist/${encodeURIComponent(claimId)}/draft`,
      { method: "POST" },
    ),

  draftAll: (limit?: number) =>
    request<{
      attempted: number;
      produced: number;
      unavailable: number;
      stillMissing: number;
      needsReview: number;
    }>(`/api/worklist/drafts${limit ? `?limit=${limit}` : ""}`, { method: "POST" }),

  analytics: () => request<Analytics>("/api/analytics"),

  prevention: () => request<PreventionReport>("/api/prevention"),
};

export const money = (value: number): string =>
  value.toLocaleString("en-US", { style: "currency", currency: "USD", maximumFractionDigits: 0 });

export const money2 = (value: number): string =>
  value.toLocaleString("en-US", { style: "currency", currency: "USD", minimumFractionDigits: 2 });
