import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { tomtomActionHandlers, validateTomtomCredential } from "./runtime.ts";

const service = "tomtom";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, tomtomActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey: validateTomtomCredential,
};
