import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { textrazorActionHandlers, validateTextrazorApiKey } from "./runtime.ts";

const service = "textrazor";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, textrazorActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateTextrazorApiKey({
      apiKey: input.apiKey,
      fetcher,
      signal,
    });
  },
};
