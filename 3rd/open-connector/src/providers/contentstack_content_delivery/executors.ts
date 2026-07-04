import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";

import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import { contentstackContentDeliveryActionHandlers, validateContentstackContentDeliveryCredential } from "./runtime.ts";

const service = "contentstack_content_delivery";

export const executors: ProviderExecutors = defineProviderExecutors({
  service,
  handlers: contentstackContentDeliveryActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch) {
    const credential = await requireApiKeyCredential(context, service);
    return {
      stackApiKey: credential.apiKey,
      deliveryToken: credential.values.deliveryToken,
      branch: credential.values.branch || credential.metadata.branch,
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validateContentstackContentDeliveryCredential(
      {
        apiKey: input.apiKey,
        ...input.values,
      },
      fetcher,
      signal,
    );
  },
};
