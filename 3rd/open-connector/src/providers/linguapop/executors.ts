import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { linguapopActionHandlers, validateLinguapopCredential } from "./runtime.ts";

const service = "linguapop";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, linguapopActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateLinguapopCredential(input.apiKey, fetcher, signal);
  },
};
