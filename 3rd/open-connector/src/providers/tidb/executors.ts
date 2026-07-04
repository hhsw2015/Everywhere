import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineProviderExecutors, requireCustomCredential } from "../provider-runtime.ts";
import { tidbActionHandlers, validateTiDBCredential } from "./runtime.ts";

export const executors: ProviderExecutors = defineProviderExecutors({
  service: "tidb",
  handlers: tidbActionHandlers,
  async createContext(context, fetcher) {
    const credential = await requireCustomCredential(context, "tidb");
    return {
      publicKey: credential.values.publicKey,
      privateKey: credential.values.privateKey,
      fetcher,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  customCredential(input, { fetcher }): ReturnType<typeof validateTiDBCredential> {
    return validateTiDBCredential(input.values, fetcher);
  },
};
