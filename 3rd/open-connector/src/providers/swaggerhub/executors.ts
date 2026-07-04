import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { swaggerhubActionHandlers, validateSwaggerhubCredential } from "./runtime.ts";

const service = "swaggerhub";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, swaggerhubActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateSwaggerhubCredential(input, fetcher, signal);
  },
};
