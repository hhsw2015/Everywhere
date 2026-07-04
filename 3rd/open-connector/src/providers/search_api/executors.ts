import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { searchApiActionHandlers, validateSearchApiCredential } from "./runtime.ts";

const service = "search_api";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, searchApiActionHandlers);

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validateSearchApiCredential(input.apiKey, fetcher, signal);
  },
};
