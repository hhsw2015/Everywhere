import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { enchargeActionHandlers, validateEnchargeCredential } from "./runtime.ts";

const service = "encharge";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, enchargeActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateEnchargeCredential(input.apiKey, fetcher, signal);
  },
};
