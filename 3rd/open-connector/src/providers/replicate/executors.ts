import type { CredentialValidators } from "../../core/types.ts";

import { executors, validateReplicateCredential } from "./runtime.ts";

export { executors };

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateReplicateCredential(input.apiKey, fetcher, signal);
  },
};
