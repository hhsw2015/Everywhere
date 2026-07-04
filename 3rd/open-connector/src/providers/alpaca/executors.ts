import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";

import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import { alpacaActionHandlers, readAlpacaCredential, validateAlpacaCredential } from "./runtime.ts";

const service = "alpaca";

export const executors: ProviderExecutors = defineProviderExecutors({
  service,
  handlers: alpacaActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch) {
    const credential = await requireApiKeyCredential(context, service);
    return {
      credential: readAlpacaCredential({
        apiKey: credential.apiKey,
        apiKeyId: credential.values.apiKeyId,
        environment: credential.values.environment,
      }),
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validateAlpacaCredential(
      {
        apiKey: input.apiKey,
        apiKeyId: input.values.apiKeyId,
        environment: input.values.environment,
      },
      fetcher,
      signal,
    );
  },
};
