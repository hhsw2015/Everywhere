import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";
import type { OksignActionContext } from "./runtime.ts";

import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import { buildOksignAuthorizationHeader, oksignActionHandlers, validateOksignCredential } from "./runtime.ts";

const service = "oksign";

export const executors: ProviderExecutors = defineProviderExecutors<OksignActionContext>({
  service,
  handlers: oksignActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch): Promise<OksignActionContext> {
    const credential = await requireApiKeyCredential(context, service);
    return {
      authorizationHeader: buildOksignAuthorizationHeader({
        accountNumber: credential.apiKey,
        authorizationToken: credential.values.authorizationToken,
        organizationalToken: credential.values.organizationalToken,
      }),
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateOksignCredential(
      {
        accountNumber: input.apiKey,
        authorizationToken: input.values.authorizationToken,
        organizationalToken: input.values.organizationalToken,
      },
      fetcher,
      signal,
    );
  },
};
