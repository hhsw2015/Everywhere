import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineProviderExecutors, requireApiKeyCredential, ProviderRequestError } from "../provider-runtime.ts";
import { tombaActionHandlers, validateTombaCredential } from "./runtime.ts";

const service = "tomba";

export const executors: ProviderExecutors = defineProviderExecutors({
  service,
  handlers: tombaActionHandlers,
  async createContext(context, fetcher) {
    const credential = await requireApiKeyCredential(context, service);
    const apiSecret = credential.values.apiSecret || credential.values.secret;
    if (!apiSecret) {
      throw new ProviderRequestError(401, "Configure Tomba API secret first.");
    }
    return {
      credential: {
        apiKey: credential.apiKey,
        apiSecret,
      },
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateTombaCredential(
      {
        apiKey: input.apiKey,
        apiSecret: input.values.apiSecret || input.values.secret,
      },
      fetcher,
      signal,
    );
  },
};
