import type { AuthDefinition } from "./model";

import { describe, expect, it } from "vitest";
import {
  connectionSubmitLabel,
  createOAuthPopupFeatures,
  oauthClientActionLabel,
  oauthConfigForProvider,
  shouldEnableConnectionSubmit,
  shouldShowConnectionActions,
  shouldShowDisconnectAction,
  shouldShowOAuthClientForm,
} from "./providers-page";

describe("shouldShowOAuthClientForm", () => {
  it("keeps OAuth client settings collapsed until the user expands them", () => {
    const auth: AuthDefinition = { type: "oauth2", scopes: [] };

    expect(shouldShowOAuthClientForm(auth, false)).toBe(false);
  });

  it("hides OAuth client settings while API key auth is selected", () => {
    const auth: AuthDefinition = { type: "api_key" };

    expect(shouldShowOAuthClientForm(auth, true)).toBe(false);
  });

  it("shows OAuth client settings while OAuth auth is selected and expanded", () => {
    const auth: AuthDefinition = { type: "oauth2", scopes: [] };

    expect(shouldShowOAuthClientForm(auth, true)).toBe(true);
  });
});

describe("shouldShowConnectionActions", () => {
  it("hides connection actions for no-auth providers", () => {
    expect(shouldShowConnectionActions({ type: "no_auth" })).toBe(false);
  });

  it("shows connection actions when credentials or OAuth are required", () => {
    expect(shouldShowConnectionActions({ type: "api_key" })).toBe(true);
    expect(shouldShowConnectionActions({ type: "oauth2", scopes: [] })).toBe(true);
  });
});

describe("shouldShowDisconnectAction", () => {
  it("hides disconnect when the provider has no saved connection", () => {
    expect(shouldShowDisconnectAction(undefined)).toBe(false);
  });

  it("shows disconnect when the provider has a saved connection", () => {
    expect(shouldShowDisconnectAction({ service: "gmail", authType: "oauth2", metadata: {} })).toBe(true);
  });
});

describe("connectionSubmitLabel", () => {
  it("labels the OAuth action as a provider connection for new connections", () => {
    expect(connectionSubmitLabel({ type: "oauth2", scopes: [] }, false, "Gmail")).toBe("Connect Gmail");
  });

  it("labels the OAuth action as reconnect for existing connections", () => {
    expect(connectionSubmitLabel({ type: "oauth2", scopes: [] }, true, "Gmail")).toBe("Reconnect Gmail");
  });

  it("keeps credential submit labels generic", () => {
    expect(connectionSubmitLabel({ type: "api_key" }, false, "Stripe")).toBe("Save Connection");
  });
});

describe("shouldEnableConnectionSubmit", () => {
  it("disables OAuth start until an OAuth client is configured", () => {
    expect(shouldEnableConnectionSubmit({ type: "oauth2", scopes: [] }, undefined)).toBe(false);
  });

  it("enables OAuth start after an OAuth client is configured", () => {
    expect(
      shouldEnableConnectionSubmit(
        { type: "oauth2", scopes: [] },
        { service: "gmail", configured: true, clientId: "gmail-client-id" },
      ),
    ).toBe(true);
  });
});

describe("oauthClientActionLabel", () => {
  it("asks the user to configure missing OAuth client settings", () => {
    expect(oauthClientActionLabel(undefined)).toBe("Configure OAuth Client");
  });

  it("shows an edit action for saved OAuth client settings", () => {
    expect(oauthClientActionLabel({ service: "gmail", configured: true, clientId: "gmail-client-id" })).toBe(
      "Edit OAuth Client",
    );
  });
});

describe("createOAuthPopupFeatures", () => {
  it("creates centered OAuth popup window features", () => {
    expect(
      createOAuthPopupFeatures({
        screenX: 100,
        screenY: 50,
        outerWidth: 1200,
        outerHeight: 900,
      }),
    ).toBe("popup=yes,width=520,height=720,left=440,top=140,resizable=yes,scrollbars=yes,noopener,noreferrer");
  });
});

describe("oauthConfigForProvider", () => {
  it("finds the saved OAuth config for the selected provider", () => {
    expect(
      oauthConfigForProvider(
        [
          { service: "github", configured: true, clientId: "github-client-id" },
          { service: "gmail", configured: true, clientId: "gmail-client-id" },
        ],
        "gmail",
      ),
    ).toMatchObject({
      service: "gmail",
      clientId: "gmail-client-id",
    });
  });

  it("ignores unconfigured OAuth config summaries", () => {
    expect(oauthConfigForProvider([{ service: "gmail", configured: false, clientId: null }], "gmail")).toBeUndefined();
  });
});
