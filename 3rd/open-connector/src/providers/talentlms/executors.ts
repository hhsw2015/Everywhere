import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";
import type { TalentlmsActionContext } from "./runtime.ts";

import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import {
  buildTalentlmsApiBaseUrl,
  normalizeTalentlmsApiBaseUrl,
  readTalentlmsDomain,
  talentlmsActionHandlers,
  validateTalentlmsCredential,
} from "./runtime.ts";

const service = "talentlms";

export const executors: ProviderExecutors = defineProviderExecutors<TalentlmsActionContext>({
  service,
  handlers: talentlmsActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch): Promise<TalentlmsActionContext> {
    const credential = await requireApiKeyCredential(context, service);
    const metadataBaseUrl =
      typeof credential.metadata.apiBaseUrl === "string" ? credential.metadata.apiBaseUrl : undefined;

    return {
      apiKey: credential.apiKey,
      apiBaseUrl: metadataBaseUrl
        ? normalizeTalentlmsApiBaseUrl(metadataBaseUrl)
        : buildTalentlmsApiBaseUrl(readTalentlmsDomain({ domain: credential.values.domain })),
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateTalentlmsCredential(input, fetcher, signal);
  },
};
