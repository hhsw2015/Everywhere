import type { CredentialValidators } from "../../core/types.ts";

import { executors, validateQianfanCredential } from "./runtime.ts";

export { executors };

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher }) {
    return validateQianfanCredential({ apiKey: input.apiKey, ...input.values }, fetcher);
  },
};
