import type { CredentialValidators } from "../../core/types.ts";

import { executors, validatePartnerstackCredential } from "./runtime.ts";

export { executors };

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher }) {
    return validatePartnerstackCredential({ apiKey: input.apiKey, ...input.values }, fetcher);
  },
};
