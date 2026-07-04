import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";
import type { DocmosisActionContext } from "./runtime.ts";

import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import { docmosisActionHandlers, resolveDocmosisApiBaseUrl, validateDocmosisCredential } from "./runtime.ts";

const service = "docmosis";

export const executors: ProviderExecutors = defineProviderExecutors<DocmosisActionContext>({
  service,
  handlers: docmosisActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch): Promise<DocmosisActionContext> {
    const credential = await requireApiKeyCredential(context, service);
    return {
      apiKey: credential.apiKey,
      apiBaseUrl: resolveDocmosisApiBaseUrl(credential.values.apiBaseUrl),
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validateDocmosisCredential(input, fetcher, signal);
  },
};
