import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";

import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import { normalizeWorkableSubdomain, validateWorkableCredential, workableActionHandlers } from "./runtime.ts";

const service = "workable";

export const executors: ProviderExecutors = defineProviderExecutors({
  service,
  handlers: workableActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch) {
    const credential = await requireApiKeyCredential(context, service);
    return {
      apiKey: credential.apiKey,
      subdomain: normalizeWorkableSubdomain(credential.values.subdomain),
      fetcher,
      signal: context.signal,
    };
  },
  fallbackMessage: "workable request failed",
});

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateWorkableCredential(input.apiKey, input.values.subdomain, fetcher, signal);
  },
};
