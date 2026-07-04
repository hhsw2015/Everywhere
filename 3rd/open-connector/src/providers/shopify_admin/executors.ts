import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";

import { optionalString } from "../../core/cast.ts";
import { defineProviderExecutors, ProviderRequestError, requireApiKeyCredential } from "../provider-runtime.ts";
import { normalizeShopDomain, shopifyAdminActionHandlers, validateShopifyAdminCredential } from "./runtime.ts";

const service = "shopify_admin";

export const executors: ProviderExecutors = defineProviderExecutors({
  service,
  handlers: shopifyAdminActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch) {
    const credential = await requireApiKeyCredential(context, service);
    const shopDomain = normalizeShopDomain(optionalString(credential.values.shopDomain));
    return {
      apiKey: credential.apiKey,
      shopDomain,
      fetcher,
      signal: context.signal,
    };
  },
  fallbackMessage: "shopify_admin request failed",
});

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    const shopDomain = optionalString(input.values.shopDomain);
    if (!shopDomain) {
      throw new ProviderRequestError(400, "shopDomain is required");
    }
    return validateShopifyAdminCredential(input.apiKey, shopDomain, fetcher, signal);
  },
};
