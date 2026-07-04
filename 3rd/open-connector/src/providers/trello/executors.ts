import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";
import type { TrelloActionContext } from "./runtime.ts";

import { defineProviderExecutors, ProviderRequestError } from "../provider-runtime.ts";
import { trelloActionHandlers, validateTrelloCredential } from "./runtime.ts";

const service = "trello";

export const executors: ProviderExecutors = defineProviderExecutors<TrelloActionContext>({
  service,
  handlers: trelloActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch): Promise<TrelloActionContext> {
    const credential = await context.getCredential(service);
    if (credential?.authType !== "custom_credential") {
      throw new ProviderRequestError(401, "Configure trello custom credentials first.");
    }
    return {
      apiKey: credential.values.apiKey,
      apiToken: credential.values.apiToken,
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  customCredential: validateTrelloCredential,
};
