import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";

import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import {
  contentstackContentManagementActionHandlers,
  validateContentstackContentManagementCredential,
} from "./runtime.ts";

const service = "contentstack_content_management";

export const executors: ProviderExecutors = defineProviderExecutors({
  service,
  handlers: contentstackContentManagementActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch) {
    const credential = await requireApiKeyCredential(context, service);
    return {
      managementToken: credential.apiKey,
      stackApiKey: credential.values.stackApiKey,
      branch: credential.values.branch || credential.metadata.branch,
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validateContentstackContentManagementCredential(
      {
        apiKey: input.apiKey,
        ...input.values,
      },
      fetcher,
      signal,
    );
  },
};
