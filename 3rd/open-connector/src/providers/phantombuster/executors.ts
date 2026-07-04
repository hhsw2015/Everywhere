import type { CredentialValidators } from "../../core/types.ts";

import { executors, validatePhantombusterCredential } from "./runtime.ts";

export { executors };

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher }) {
    return validatePhantombusterCredential({ apiKey: input.apiKey, ...input.values }, fetcher);
  },
};
