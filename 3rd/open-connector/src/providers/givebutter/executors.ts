import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { givebutterActionHandlers, validateGivebutterCredential } from "./runtime.ts";

const service = "givebutter";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, givebutterActionHandlers);

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validateGivebutterCredential(input, fetcher, signal);
  },
};
