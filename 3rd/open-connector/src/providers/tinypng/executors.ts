import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { tinypngActionHandlers, validateTinypngCredential } from "./runtime.ts";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors("tinypng", tinypngActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher }): ReturnType<typeof validateTinypngCredential> {
    return validateTinypngCredential({ ...input.values, apiKey: input.apiKey }, fetcher);
  },
};
