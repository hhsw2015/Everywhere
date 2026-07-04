import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";
import type { NorthbeamContext } from "./runtime.ts";

import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import { northbeamActionHandlers, resolveNorthbeamCredentialContext, validateNorthbeamCredential } from "./runtime.ts";

const service = "northbeam";

export const executors: ProviderExecutors = defineProviderExecutors<NorthbeamContext>({
  service,
  handlers: northbeamActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch): Promise<NorthbeamContext> {
    const credential = await requireApiKeyCredential(context, service);
    return resolveNorthbeamCredentialContext(
      credential.apiKey,
      credential.values,
      credential.metadata,
      fetcher,
      context.signal,
    );
  },
  fallbackMessage: "northbeam request failed",
});

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateNorthbeamCredential(input.apiKey, input.values, fetcher, signal);
  },
};
