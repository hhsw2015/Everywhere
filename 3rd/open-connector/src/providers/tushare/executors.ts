import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { tushareActionHandlers, validateTushareCredential } from "./runtime.ts";

const service = "tushare";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, tushareActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateTushareCredential(input.apiKey, fetcher, signal);
  },
};
