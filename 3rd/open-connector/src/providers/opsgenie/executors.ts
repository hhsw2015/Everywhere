import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";

import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import { opsgenieActionHandlers, validateOpsgenieCredential } from "./runtime.ts";

const service = "opsgenie";

export const executors: ProviderExecutors = defineProviderExecutors({
  service,
  handlers: opsgenieActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch) {
    const credential = await requireApiKeyCredential(context, service);
    return {
      apiKey: credential.apiKey,
      environment: credential.metadata.environment ?? credential.values.environment,
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateOpsgenieCredential(
      {
        apiKey: input.apiKey,
        environment: input.values.environment,
      },
      fetcher,
      signal,
    );
  },
};
