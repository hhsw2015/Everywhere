import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";

import { optionalString } from "../../core/cast.ts";
import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import { normalizeShopDomain, shopifyActionHandlers, validateShopifyCredential } from "./runtime.ts";

const service = "shopify";

export const executors: ProviderExecutors = defineProviderExecutors({
  service,
  handlers: shopifyActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch) {
    const credential = await requireApiKeyCredential(context, service);
    return {
      apiKey: credential.apiKey,
      shopDomain: normalizeShopDomain(optionalString(credential.values.shopDomain)),
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateShopifyCredential(input, fetcher, signal);
  },
};
