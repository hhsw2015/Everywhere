import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { timelinkActionHandlers, validateTimelinkCredential } from "./runtime.ts";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors("timelink", timelinkActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher }): ReturnType<typeof validateTimelinkCredential> {
    return validateTimelinkCredential({ ...input.values, apiKey: input.apiKey }, fetcher);
  },
};
