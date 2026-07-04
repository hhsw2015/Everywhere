import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { manatalActionHandlers, validateManatalCredential } from "./runtime.ts";

const service = "manatal";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, manatalActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateManatalCredential(input.apiKey, fetcher, signal);
  },
};
