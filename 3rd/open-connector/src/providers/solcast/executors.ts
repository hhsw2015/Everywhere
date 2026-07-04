import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { solcastActionHandlers, validateSolcastCredential } from "./runtime.ts";

const service = "solcast";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, solcastActionHandlers);

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validateSolcastCredential({
      apiKey: input.apiKey,
      fetcher,
      signal,
    });
  },
};
