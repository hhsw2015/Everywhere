import type { CredentialValidators } from "../../core/types.ts";

import { executors, validatePayhipCredential } from "./runtime.ts";

export { executors };

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher }) {
    return validatePayhipCredential({ apiKey: input.apiKey, ...input.values }, fetcher);
  },
};
