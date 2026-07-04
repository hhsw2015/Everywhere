import type { CredentialValidators } from "../../core/types.ts";

import { executors, validateQuadernoCredential } from "./runtime.ts";

export { executors };

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher }) {
    return validateQuadernoCredential({ apiKey: input.apiKey, ...input.values }, fetcher);
  },
};
