import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { tencentMapsActionHandlers, validateTencentMapsCredential } from "./runtime.ts";

const service = "tencent_maps";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, tencentMapsActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateTencentMapsCredential({
      apiKey: input.apiKey,
      fetcher,
      signal,
    });
  },
};
