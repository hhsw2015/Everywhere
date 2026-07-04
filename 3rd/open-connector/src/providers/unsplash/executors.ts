import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { unsplashActionHandlers, validateUnsplashCredential } from "./runtime.ts";

const service = "unsplash";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, unsplashActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateUnsplashCredential(input.apiKey, fetcher, signal);
  },
};
