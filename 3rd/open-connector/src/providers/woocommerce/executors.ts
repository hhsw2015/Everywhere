import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";

import { defineProviderExecutors, requireCustomCredential } from "../provider-runtime.ts";
import {
  resolveWooCommerceCredentialContext,
  validateWooCommerceCredential,
  woocommerceActionHandlers,
} from "./runtime.ts";

const service = "woocommerce";

export const executors: ProviderExecutors = defineProviderExecutors({
  service,
  handlers: woocommerceActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch) {
    const credential = await requireCustomCredential(context, service);
    return resolveWooCommerceCredentialContext(credential.values, fetcher, context.signal, context.transitFiles);
  },
  fallbackMessage: "woocommerce request failed",
});

export const credentialValidators: CredentialValidators = {
  customCredential(input, { fetcher, signal }) {
    return validateWooCommerceCredential(input.values, fetcher, signal);
  },
};
