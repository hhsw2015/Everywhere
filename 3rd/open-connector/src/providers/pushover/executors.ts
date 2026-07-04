import type { CredentialValidators } from "../../core/types.ts";

import { executors, validatePushoverCredential } from "./runtime.ts";

export { executors };

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher }) {
    return validatePushoverCredential({ apiKey: input.apiKey, ...input.values }, fetcher);
  },
};
