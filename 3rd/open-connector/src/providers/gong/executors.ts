import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";
import type { GongContext } from "./runtime.ts";

import { defineProviderExecutors, requireCustomCredential } from "../provider-runtime.ts";
import { gongActionHandlers, resolveGongCredentialContext, validateGongCredential } from "./runtime.ts";

const service = "gong";

export const executors: ProviderExecutors = defineProviderExecutors<GongContext>({
  service,
  handlers: gongActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch): Promise<GongContext> {
    const credential = await requireCustomCredential(context, service);
    return resolveGongCredentialContext(credential.values, fetcher, context.signal);
  },
  fallbackMessage: "gong request failed",
});

export const credentialValidators: CredentialValidators = {
  customCredential(input, { fetcher, signal }) {
    return validateGongCredential(input.values, fetcher, signal);
  },
};
