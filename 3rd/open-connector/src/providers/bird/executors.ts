import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { birdActionHandlers, validateBirdCredential } from "./runtime.ts";

const service = "bird";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, birdActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateBirdCredential(input.apiKey, fetcher, signal);
  },
};
