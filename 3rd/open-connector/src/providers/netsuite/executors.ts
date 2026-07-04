import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";
import type { NetsuiteContext } from "./runtime.ts";

import { defineProviderExecutors, requireCustomCredential } from "../provider-runtime.ts";
import { netsuiteActionHandlers, resolveNetsuiteCredentialContext, validateNetsuiteCredential } from "./runtime.ts";

const service = "netsuite";

export const executors: ProviderExecutors = defineProviderExecutors<NetsuiteContext>({
  service,
  handlers: netsuiteActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch): Promise<NetsuiteContext> {
    const credential = await requireCustomCredential(context, service);
    return resolveNetsuiteCredentialContext(credential.values, credential.metadata, fetcher, context.signal);
  },
  fallbackMessage: "netsuite request failed",
});

export const credentialValidators: CredentialValidators = {
  customCredential(input, { fetcher, signal }) {
    return validateNetsuiteCredential(input.values, fetcher, signal);
  },
};
