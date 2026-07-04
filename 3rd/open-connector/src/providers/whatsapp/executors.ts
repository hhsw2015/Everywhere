import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";
import type { WhatsAppActionContext } from "./runtime.ts";

import { optionalString } from "../../core/cast.ts";
import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import { validateWhatsAppCredential, whatsappActionHandlers } from "./runtime.ts";

const service = "whatsapp";

export const executors: ProviderExecutors = defineProviderExecutors<WhatsAppActionContext>({
  service,
  handlers: whatsappActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch): Promise<WhatsAppActionContext> {
    const credential = await requireApiKeyCredential(context, service);
    const providerContext: WhatsAppActionContext = {
      accessToken: credential.apiKey,
      wabaId: optionalString(credential.values.wabaId) ?? optionalString(credential.metadata.wabaId),
      fetcher,
      signal: context.signal,
    };
    if (context.transitFiles) providerContext.transitFiles = context.transitFiles;
    return providerContext;
  },
});

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateWhatsAppCredential(input, fetcher, signal);
  },
};
