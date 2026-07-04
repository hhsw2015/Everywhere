import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { imagekitActionHandlers, validateImagekitCredential } from "./runtime.ts";

const service = "imagekit";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, imagekitActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey: validateImagekitCredential,
};
