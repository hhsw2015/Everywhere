import type { CredentialValidators } from "../../core/types.ts";

import { executors, validatePineconeCredential } from "./runtime.ts";

export { executors };

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher }) {
    return validatePineconeCredential({ apiKey: input.apiKey, ...input.values }, fetcher);
  },
};
