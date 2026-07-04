import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { hookdeckActionHandlers, validateHookdeckCredential } from "./runtime.ts";

const service = "hookdeck";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, hookdeckActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey: validateHookdeckCredential,
};
