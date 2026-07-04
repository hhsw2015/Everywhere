import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineProviderExecutors, requireCustomCredential } from "../provider-runtime.ts";
import { twilioActionHandlers, validateTwilioCredential } from "./runtime.ts";

const service = "twilio";

export const executors: ProviderExecutors = defineProviderExecutors({
  service,
  handlers: twilioActionHandlers,
  async createContext(context, fetcher) {
    const credential = await requireCustomCredential(context, service);
    return {
      accountSid: credential.values.accountSid,
      authToken: credential.values.authToken,
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  customCredential(input, { fetcher, signal }) {
    return validateTwilioCredential(input.values, fetcher, signal);
  },
};
