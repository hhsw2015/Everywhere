import type { CredentialValidators } from "../../core/types.ts";

import { executors, validateProspeoCredential } from "./runtime.ts";

export { executors };

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher }) {
    return validateProspeoCredential({ apiKey: input.apiKey, ...input.values }, fetcher);
  },
};
