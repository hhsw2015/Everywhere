import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { smugmugActionHandlers, validateSmugmugCredential } from "./runtime.ts";

const service = "smugmug";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, smugmugActionHandlers);

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validateSmugmugCredential({
      apiKey: input.apiKey,
      fetcher,
      signal,
    });
  },
};
