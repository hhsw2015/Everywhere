import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";

import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import { mattermostActionHandlers, normalizeMattermostUrls, validateMattermostCredential } from "./runtime.ts";

const service = "mattermost";

export const executors: ProviderExecutors = defineProviderExecutors({
  service,
  handlers: mattermostActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch) {
    const credential = await requireApiKeyCredential(context, service);
    const urls = normalizeMattermostUrls(credential.metadata.apiBaseUrl ?? credential.values.instanceUrl);
    return {
      apiKey: credential.apiKey,
      apiBaseUrl: urls.apiBaseUrl,
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateMattermostCredential(
      { apiKey: input.apiKey, instanceUrl: input.values.instanceUrl },
      fetcher,
      signal,
    );
  },
};
