import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineMailgunExecutors, validateMailgunCredential } from "./runtime.ts";

const service = "mailgun";

export const executors: ProviderExecutors = defineMailgunExecutors(service);

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateMailgunCredential(input.apiKey, input.values, fetcher, signal);
  },
};
