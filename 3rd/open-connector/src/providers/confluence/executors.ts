import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";

import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import { confluenceActionHandlers, validateConfluenceCredential } from "./runtime.ts";

const service = "confluence";

export const executors: ProviderExecutors = defineProviderExecutors({
  service,
  handlers: confluenceActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch) {
    const credential = await requireApiKeyCredential(context, service);
    return {
      apiToken: credential.apiKey,
      email: credential.metadata.email,
      baseUrl: credential.metadata.baseUrl,
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validateConfluenceCredential(
      {
        apiKey: input.apiKey,
        ...input.values,
      },
      fetcher,
      signal,
    );
  },
};
