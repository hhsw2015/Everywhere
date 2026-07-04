import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { bugsnagActionHandlers, validateBugsnagCredential } from "./runtime.ts";

const service = "bugsnag";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, bugsnagActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateBugsnagCredential({ apiKey: input.apiKey, fetcher, signal });
  },
};
