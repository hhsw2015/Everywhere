import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";
import type { ClickupActionContext } from "./runtime.ts";

import { defineProviderExecutors, ProviderRequestError } from "../provider-runtime.ts";
import { clickupActionHandlers, validateClickupCredential } from "./runtime.ts";

const service = "clickup";

export const executors: ProviderExecutors = defineProviderExecutors({
  service,
  handlers: clickupActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch): Promise<ClickupActionContext> {
    const credential = await context.getCredential(service);
    if (credential?.authType === "oauth2") {
      return {
        authType: "oauth2",
        accessToken: credential.accessToken,
        fetcher,
        signal: context.signal,
      };
    }
    if (credential?.authType === "api_key") {
      return {
        authType: "api_key",
        accessToken: credential.apiKey,
        fetcher,
        signal: context.signal,
      };
    }

    throw new ProviderRequestError(401, "Configure clickup credentials first.");
  },
});

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validateClickupCredential(input.apiKey, "api_key", fetcher, signal);
  },
  async oauth2(input, { fetcher, signal }) {
    return validateClickupCredential(input.accessToken, "oauth2", fetcher, signal);
  },
};
