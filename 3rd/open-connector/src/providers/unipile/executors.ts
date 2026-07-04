import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";
import type { ProviderFetch } from "../provider-runtime.ts";

import { defineProviderExecutors, ProviderRequestError } from "../provider-runtime.ts";
import { unipileActionHandlers, validateUnipileCredential } from "./runtime.ts";

const service = "unipile";

interface UnipileExecutorContext {
  dsn: string;
  apiKey: string;
  fetcher: ProviderFetch;
  signal?: AbortSignal;
}

export const executors: ProviderExecutors = defineProviderExecutors<UnipileExecutorContext>({
  service,
  handlers: unipileActionHandlers,
  async createContext(context: ExecutionContext, fetcher: ProviderFetch): Promise<UnipileExecutorContext> {
    const credential = await context.getCredential(service);
    if (!credential || credential.authType !== "api_key") {
      throw new ProviderRequestError(401, "Configure unipile API key credentials first.");
    }
    return {
      dsn: credential.values.dsn,
      apiKey: credential.apiKey,
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateUnipileCredential({ apiKey: input.apiKey, dsn: input.values.dsn }, fetcher, signal);
  },
};
