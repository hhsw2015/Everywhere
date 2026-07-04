import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";
import type { ApiKeyProviderContext, ProviderFetch } from "../provider-runtime.ts";

import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import { simpleAnalyticsActionHandlers, validateSimpleAnalyticsCredential } from "./runtime.ts";

const service = "simple_analytics";

interface SimpleAnalyticsContext extends ApiKeyProviderContext {
  userId?: string;
}

export const executors: ProviderExecutors = defineProviderExecutors<SimpleAnalyticsContext>({
  service,
  handlers: simpleAnalyticsActionHandlers,
  async createContext(context: ExecutionContext, fetcher: ProviderFetch): Promise<SimpleAnalyticsContext> {
    const credential = await requireApiKeyCredential(context, service);
    return {
      apiKey: credential.apiKey,
      userId: credential.values.userId || readMetadataUserId(credential.metadata),
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validateSimpleAnalyticsCredential(input.apiKey, input.values, fetcher, signal);
  },
};

function readMetadataUserId(metadata: Record<string, unknown>): string | undefined {
  return typeof metadata.userId === "string" && metadata.userId ? metadata.userId : undefined;
}
