import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";

import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import { createDockerHubActionContext, dockerHubActionHandlers, validateDockerHubApiKey } from "./runtime.ts";

const service = "docker_hub";

export const executors: ProviderExecutors = defineProviderExecutors({
  service,
  handlers: dockerHubActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch) {
    const credential = await requireApiKeyCredential(context, service);
    return createDockerHubActionContext(credential.apiKey, fetcher, context.signal);
  },
});

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    return validateDockerHubApiKey(input.apiKey, fetcher, signal);
  },
};
