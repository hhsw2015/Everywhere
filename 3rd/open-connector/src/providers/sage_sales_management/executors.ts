import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineProviderExecutors, requireCustomCredential } from "../provider-runtime.ts";
import {
  createSageSalesManagementActionContext,
  sageSalesManagementActionHandlers,
  validateSageSalesManagementCredential,
} from "./runtime.ts";

const service = "sage_sales_management";

export const executors: ProviderExecutors = defineProviderExecutors({
  service,
  handlers: sageSalesManagementActionHandlers,
  async createContext(context, fetcher) {
    const credential = await requireCustomCredential(context, service);
    return createSageSalesManagementActionContext(credential.values, fetcher, context.signal);
  },
});

export const credentialValidators: CredentialValidators = {
  customCredential(input, { fetcher, signal }) {
    return validateSageSalesManagementCredential(input.values, fetcher, signal);
  },
};
