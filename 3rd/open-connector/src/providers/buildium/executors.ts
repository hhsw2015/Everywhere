import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";
import type { BuildiumActionContext } from "./runtime.ts";

import { requiredString } from "../../core/cast.ts";
import { defineProviderExecutors, ProviderRequestError, requireApiKeyCredential } from "../provider-runtime.ts";
import { buildiumActionHandlers, validateBuildiumCredential } from "./runtime.ts";

const service = "buildium";

export const executors: ProviderExecutors = defineProviderExecutors<BuildiumActionContext>({
  service,
  handlers: buildiumActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch): Promise<BuildiumActionContext> {
    const credential = await requireApiKeyCredential(context, service);
    return {
      clientId: readClientId(credential.values),
      clientSecret: credential.apiKey,
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateBuildiumCredential({
      clientId: readClientId(input.values),
      clientSecret: input.apiKey,
      fetcher,
      signal,
    });
  },
};

function readClientId(values: Record<string, string>): string {
  return requiredString(values.clientId, "clientId", (message) => new ProviderRequestError(400, message));
}
