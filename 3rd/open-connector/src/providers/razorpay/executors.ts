import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";
import type { ProviderFetch } from "../provider-runtime.ts";

import { defineProviderExecutors } from "../provider-runtime.ts";
import { razorpayActionHandlers, validateRazorpayCredential } from "./runtime.ts";

const service = "razorpay";

interface RazorpayExecutorContext {
  keyId: string;
  keySecret: string;
  fetcher: ProviderFetch;
}

export const executors: ProviderExecutors = defineProviderExecutors<RazorpayExecutorContext>({
  service,
  handlers: razorpayActionHandlers,
  async createContext(context: ExecutionContext, fetcher: ProviderFetch): Promise<RazorpayExecutorContext> {
    const credential = await context.getCredential(service);
    if (!credential || credential.authType !== "api_key") {
      throw new Error("razorpay requires api_key credential");
    }
    const keyId = credential.values.keyId?.trim();
    if (!keyId) {
      throw new Error("keyId is required");
    }
    return {
      keyId,
      keySecret: credential.apiKey,
      fetcher,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher }) {
    return validateRazorpayCredential({ apiKey: input.apiKey, ...input.values }, fetcher);
  },
};
