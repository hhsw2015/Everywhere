import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { algoDocsActionHandlers, validateAlgoDocsCredential } from "./runtime.ts";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors("algo_docs", algoDocsActionHandlers);

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validateAlgoDocsCredential(input.apiKey, fetcher, signal);
  },
};
