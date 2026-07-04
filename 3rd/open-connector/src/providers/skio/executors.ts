import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { skioActionHandlers, validateSkioCredential } from "./runtime.ts";

const service = "skio";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, skioActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateSkioCredential(input.apiKey, fetcher, signal);
  },
};
