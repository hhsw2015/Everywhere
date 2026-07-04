import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { certSpotterActionHandlers, validateCertSpotterCredential } from "./runtime.ts";

const service = "sslmate_cert_spotter_api";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, certSpotterActionHandlers);

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validateCertSpotterCredential({
      apiKey: input.apiKey,
      fetcher,
      signal,
    });
  },
};
