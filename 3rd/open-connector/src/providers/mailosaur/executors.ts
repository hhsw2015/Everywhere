import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { mailosaurActionHandlers, validateMailosaurCredential } from "./runtime.ts";

const service = "mailosaur";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, mailosaurActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateMailosaurCredential(input.apiKey, fetcher, signal);
  },
};
