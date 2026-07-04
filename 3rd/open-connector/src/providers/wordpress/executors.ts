import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";

import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import { createWordpressContext, validateWordpressCredential, wordpressActionHandlers } from "./runtime.ts";

const service = "wordpress";

export const executors: ProviderExecutors = defineProviderExecutors({
  service,
  handlers: wordpressActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch) {
    const credential = await requireApiKeyCredential(context, service);
    return createWordpressContext(credential.apiKey, credential.values, fetcher, context.signal);
  },
});

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateWordpressCredential(input, fetcher, signal);
  },
};
