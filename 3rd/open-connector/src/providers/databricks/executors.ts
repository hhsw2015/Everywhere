import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";

import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import { databricksActionHandlers, normalizeDatabricksHost, validateDatabricksCredential } from "./runtime.ts";

const service = "databricks";

interface DatabricksContext {
  apiKey: string;
  host: string;
  fetcher: typeof fetch;
  signal?: AbortSignal;
}

export const executors: ProviderExecutors = defineProviderExecutors<DatabricksContext>({
  service,
  handlers: databricksActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch): Promise<DatabricksContext> {
    const credential = await requireApiKeyCredential(context, service);
    return {
      apiKey: credential.apiKey,
      host: normalizeDatabricksHost(credential.values.host || String(credential.metadata.host ?? "")),
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validateDatabricksCredential(input.apiKey, input.values, fetcher, signal);
  },
};
