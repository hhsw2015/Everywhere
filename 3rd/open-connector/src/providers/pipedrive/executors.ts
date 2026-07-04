import type { CredentialValidators } from "../../core/types.ts";

import { executors, validatePipedriveCredential } from "./runtime.ts";

export { executors };

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher }) {
    return validatePipedriveCredential({ apiKey: input.apiKey, ...input.values }, fetcher);
  },
};
