import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { tisaneActionHandlers, validateTisaneCredential } from "./runtime.ts";

const service = "tisane";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, tisaneActionHandlers);

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validateTisaneCredential(input.apiKey, fetcher, signal);
  },
};
