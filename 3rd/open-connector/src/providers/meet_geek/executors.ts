import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";

import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";
import { meetGeekActionHandlers, validateMeetGeekCredential } from "./runtime.ts";

const service = "meet_geek";

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, meetGeekActionHandlers);

export const credentialValidators: CredentialValidators = {
  apiKey: validateMeetGeekCredential,
};
