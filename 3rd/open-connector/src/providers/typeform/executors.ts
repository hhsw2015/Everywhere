import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineBearerProviderExecutors } from "../provider-runtime.ts";
import { fetchTypeformCurrentAccount, typeformActionHandlers, validateTypeformCredential } from "./runtime.ts";

const service = "typeform";

export const executors: ProviderExecutors = defineBearerProviderExecutors(service, typeformActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateTypeformCredential(input.apiKey, fetcher, signal);
  },
  oauth2(input, { fetcher, signal }) {
    return fetchTypeformCurrentAccount(input.accessToken, fetcher, signal);
  },
};
