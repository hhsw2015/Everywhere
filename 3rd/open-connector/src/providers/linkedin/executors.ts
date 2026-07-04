import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineOAuthProviderExecutors } from "../provider-runtime.ts";
import { linkedinActionHandlers, validateLinkedinCredential } from "./runtime.ts";

const service = "linkedin";

export const executors: ProviderExecutors = defineOAuthProviderExecutors(service, linkedinActionHandlers);

export const credentialValidators: CredentialValidators = {
  oauth2(input, { fetcher, signal }) {
    return validateLinkedinCredential(input, fetcher, signal);
  },
};
