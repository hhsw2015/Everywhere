import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { togglActionHandlers, validateTogglCredential } from "./runtime.ts";

const service = "toggl";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, togglActionHandlers);

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validateTogglCredential(input.apiKey, fetcher, signal);
  },
};
