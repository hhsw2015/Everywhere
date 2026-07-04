import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";

import { optionalString } from "../../core/cast.ts";
import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import { plausibleAnalyticsActionHandlers, validatePlausibleAnalyticsCredential } from "./runtime.ts";

const service = "plausible_analytics";

export const executors: ProviderExecutors = defineProviderExecutors({
  service,
  handlers: plausibleAnalyticsActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch) {
    const credential = await requireApiKeyCredential(context, service);
    return {
      apiKey: credential.apiKey,
      siteId: optionalString(credential.values.siteId) ?? optionalString(credential.metadata.siteId),
      baseUrl: optionalString(credential.values.baseUrl) ?? optionalString(credential.metadata.baseUrl),
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validatePlausibleAnalyticsCredential(
      input.apiKey,
      optionalString(input.values.siteId),
      optionalString(input.values.baseUrl),
      fetcher,
      signal,
    );
  },
};
