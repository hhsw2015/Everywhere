import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";
import type { HiggsfieldAiContext } from "./runtime.ts";

import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import { higgsfieldAiActionHandlers, readHiggsfieldAiApiSecret, validateHiggsfieldAiCredential } from "./runtime.ts";

const service = "higgsfield_ai";

export const executors: ProviderExecutors = defineProviderExecutors<HiggsfieldAiContext>({
  service,
  handlers: higgsfieldAiActionHandlers,
  async createContext(context, fetcher): Promise<HiggsfieldAiContext> {
    const credential = await requireApiKeyCredential(context, service);
    return {
      apiKey: credential.apiKey,
      apiSecret: readHiggsfieldAiApiSecret(credential.values.apiSecret),
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateHiggsfieldAiCredential(
      input.apiKey,
      readHiggsfieldAiApiSecret(input.values.apiSecret),
      fetcher,
      signal,
    );
  },
};
