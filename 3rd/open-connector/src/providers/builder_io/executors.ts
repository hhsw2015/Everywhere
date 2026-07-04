import type { CredentialValidationResult, CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { builderIoActionHandlers, builderIoWriteApiBaseUrl } from "./runtime.ts";

const service = "builder_io";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, builderIoActionHandlers);

export const credentialValidators: CredentialValidators = {
  async apiKey(input): Promise<CredentialValidationResult> {
    return {
      profile: {
        accountId: "builder_io_api_key",
        displayName: "Builder.io API Key",
      },
      grantedScopes: [],
      metadata: {
        apiBaseUrl: builderIoWriteApiBaseUrl,
        validationStrategy: "local_required_key_check",
        keyPresent: input.apiKey.length > 0,
      },
    };
  },
};
