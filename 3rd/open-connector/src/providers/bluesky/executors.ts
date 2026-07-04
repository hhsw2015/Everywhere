import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";
import type { BlueskyContext } from "./runtime.ts";

import { optionalString } from "../../core/cast.ts";
import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import { blueskyActionHandlers, requireBlueskyHandle, validateBlueskyCredential } from "./runtime.ts";

const service = "bluesky";

export const executors: ProviderExecutors = defineProviderExecutors<BlueskyContext>({
  service,
  handlers: blueskyActionHandlers,
  async createContext(context, fetcher): Promise<BlueskyContext> {
    const credential = await requireApiKeyCredential(context, service);
    return {
      apiKey: credential.apiKey,
      handle: requireBlueskyHandle(optionalString(credential.values.handle)),
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateBlueskyCredential(input, fetcher, signal);
  },
};
