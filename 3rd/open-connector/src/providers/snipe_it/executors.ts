import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";

import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import { resolveSnipeItContext, snipeItActionHandlers, validateSnipeItCredential } from "./runtime.ts";

const service = "snipe_it";

export const executors: ProviderExecutors = defineProviderExecutors({
  service,
  handlers: snipeItActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch) {
    const credential = await requireApiKeyCredential(context, service);
    return resolveSnipeItContext({ ...credential.values, apiKey: credential.apiKey }, fetcher, context.signal);
  },
  fallbackMessage: "snipe_it request failed",
});

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateSnipeItCredential({ ...input.values, apiKey: input.apiKey }, fetcher, signal);
  },
};
