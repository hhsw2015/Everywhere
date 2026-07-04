import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";
import type { CustomerioCredentialContext } from "./runtime.ts";

import { defineProviderExecutors, requireCustomCredential } from "../provider-runtime.ts";
import {
  customerioActionHandlers,
  resolveCustomerioCredentialContext,
  validateCustomerioCredential,
} from "./runtime.ts";

const service = "customerio";

export const executors: ProviderExecutors = defineProviderExecutors<CustomerioCredentialContext>({
  service,
  handlers: customerioActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch): Promise<CustomerioCredentialContext> {
    const credential = await requireCustomCredential(context, service);
    return resolveCustomerioCredentialContext(credential.values, fetcher, context.signal, credential.metadata);
  },
});

export const credentialValidators: CredentialValidators = {
  customCredential(input, { fetcher, signal }) {
    return validateCustomerioCredential(input.values, fetcher, signal);
  },
};
