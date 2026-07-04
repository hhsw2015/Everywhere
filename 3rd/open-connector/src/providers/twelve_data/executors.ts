import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { twelveDataActionHandlers, validateTwelveDataCredential } from "./runtime.ts";

const service = "twelve_data";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, twelveDataActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateTwelveDataCredential(input.apiKey, fetcher, signal);
  },
};
