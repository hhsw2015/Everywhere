import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineSendsparkExecutors, validateSendsparkCredential } from "./runtime.ts";

export const executors: ProviderExecutors = defineSendsparkExecutors();

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validateSendsparkCredential(input, fetcher, signal);
  },
};
