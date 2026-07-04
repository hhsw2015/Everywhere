import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { expofpActionHandlers, validateExpofpCredential } from "./runtime.ts";

const service = "expofp";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, expofpActionHandlers);

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validateExpofpCredential(input.apiKey, fetcher, signal);
  },
};
