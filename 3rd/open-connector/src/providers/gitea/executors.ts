import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";
import type { GiteaActionContext } from "./runtime.ts";

import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import { giteaActionHandlers, resolveGiteaBaseUrl, validateGiteaCredential } from "./runtime.ts";

const service = "gitea";

export const executors: ProviderExecutors = defineProviderExecutors<GiteaActionContext>({
  service,
  handlers: giteaActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch): Promise<GiteaActionContext> {
    const credential = await requireApiKeyCredential(context, service);
    return {
      apiKey: credential.apiKey,
      baseUrl: resolveGiteaBaseUrl({
        values: credential.values,
        metadata: credential.metadata,
      }),
      fetcher,
      signal: context.signal,
    };
  },
  fallbackMessage: "Gitea request failed.",
});

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validateGiteaCredential(input, fetcher, signal);
  },
};
