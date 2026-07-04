import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineBearerProviderExecutors } from "../provider-runtime.ts";
import { calendlyActionHandlers, validateCalendlyCredential } from "./runtime.ts";

const service = "calendly";

export const executors: ProviderExecutors = defineBearerProviderExecutors(service, calendlyActionHandlers);

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validateCalendlyCredential(input.apiKey, fetcher, signal);
  },
  async oauth2(input, { fetcher, signal }) {
    return validateCalendlyCredential(input.accessToken, fetcher, signal);
  },
};
