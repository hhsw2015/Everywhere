import type { CredentialValidators } from "../../core/types.ts";

import { executors, validatePendoCredential } from "./runtime.ts";

export { executors };

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher }) {
    return validatePendoCredential({ apiKey: input.apiKey, ...input.values }, fetcher);
  },
};
