import type { CredentialValidators } from "../../core/types.ts";

import { executors, validateProductiveCredential } from "./runtime.ts";

export { executors };

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher }) {
    return validateProductiveCredential({ apiKey: input.apiKey, ...input.values }, fetcher);
  },
};
