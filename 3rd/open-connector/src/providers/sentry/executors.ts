import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineOAuthProviderExecutors } from "../provider-runtime.ts";
import { sentryActionHandlers, validateSentryCredential } from "./runtime.ts";

const service = "sentry";

export const executors: ProviderExecutors = defineOAuthProviderExecutors(service, sentryActionHandlers);

export const credentialValidators: CredentialValidators = {
  async oauth2(input, { fetcher }) {
    return validateSentryCredential(input.accessToken, fetcher);
  },
};
