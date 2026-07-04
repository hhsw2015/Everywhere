import { I18nProvider } from "@embra/i18n/react";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { MemoryRouter } from "react-router";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createAppI18n } from "./i18n";
import { App, loadRuntimeData, nextAuthLoadState, nextLogoutState } from "./ui";

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("App", () => {
  it("does not render the console shell before the initial auth check finishes", () => {
    const markup = renderToStaticMarkup(
      createElement(
        I18nProvider,
        { i18n: createAppI18n("en") },
        createElement(MemoryRouter, { initialEntries: ["/"] }, createElement(App)),
      ),
    );

    expect(markup).not.toContain("app-shell");
    expect(markup).toContain("Loading runtime data");
  });
});

describe("nextLogoutState", () => {
  it("keeps the current auth state when logout fails", () => {
    const state = {
      authSession: { adminAuthConfigured: true, authenticated: true },
    };

    expect(nextLogoutState(state, false)).toBe(state);
  });

  it("clears the current auth state when logout succeeds", () => {
    expect(
      nextLogoutState(
        {
          authSession: { adminAuthConfigured: true, authenticated: true },
        },
        true,
      ),
    ).toEqual({
      authSession: { adminAuthConfigured: true, authenticated: false },
    });
  });
});

describe("nextAuthLoadState", () => {
  it("clears the pending unlock token after the session is authenticated", () => {
    expect(
      nextAuthLoadState(
        {
          pendingUnlockToken: "local-token",
          authSession: { adminAuthConfigured: true, authenticated: false },
        },
        { adminAuthConfigured: true, authenticated: true },
      ),
    ).toEqual({
      pendingUnlockToken: "",
      authSession: { adminAuthConfigured: true, authenticated: true },
    });
  });
});

describe("loadRuntimeData", () => {
  it("uses the unlock token only when reading the auth session", async () => {
    const calls: Array<{ path: string; headers: Headers }> = [];
    vi.stubGlobal(
      "fetch",
      vi.fn(async (path: RequestInfo | URL, init?: RequestInit) => {
        calls.push({ path: String(path), headers: new Headers(init?.headers) });
        if (path === "/api/auth/session") {
          return Response.json({ adminAuthConfigured: true, authenticated: true });
        }
        if (path === "/api/runs") {
          return Response.json({ items: [], nextCursor: null });
        }
        return Response.json([]);
      }),
    );

    await loadRuntimeData("local-token");

    expect(calls.map((call) => call.path)).toEqual([
      "/api/auth/session",
      "/api/providers",
      "/api/connections",
      "/api/oauth/configs",
      "/api/runtime-tokens",
      "/api/runs",
    ]);
    expect(calls[0]?.headers.get("authorization")).toBe("Bearer local-token");
    for (const call of calls.slice(1)) {
      expect(call.headers.get("authorization")).toBeNull();
    }
  });
});
