import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { octaveActionHandlers, validateOctaveCredential } from "./runtime.ts";

const service = "octave";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, octaveActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateOctaveCredential(input.apiKey, fetcher, signal);
  },
};
