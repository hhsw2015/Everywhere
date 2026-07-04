import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { nylasActionHandlers, validateNylasCredential } from "./runtime.ts";

const service = "nylas";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, nylasActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateNylasCredential(input.apiKey, fetcher, signal);
  },
};
