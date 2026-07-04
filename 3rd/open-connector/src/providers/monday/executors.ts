import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";
import type { ProviderFetch } from "../provider-runtime.ts";
import type { MondayActionHandler } from "./runtime-common.ts";

import { defineProviderExecutors, requireBearerCredential } from "../provider-runtime.ts";
import { mondayAutomationActionHandlers } from "./runtime-automation.ts";
import { mondayCollaborationActionHandlers } from "./runtime-collaboration.ts";
import { validateMondayCredential } from "./runtime-common.ts";
import { mondayDiscoveryActionHandlers } from "./runtime-discovery.ts";
import { mondayEnterpriseActionHandlers } from "./runtime-enterprise.ts";
import { mondayFormsActionHandlers } from "./runtime-forms.ts";
import { mondayItemActionHandlers } from "./runtime-items.ts";
import { mondayStructureActionHandlers } from "./runtime-structure.ts";
import { getMondayAuthorizationScopes, parseMondayScopeString } from "./scopes.ts";

const service = "monday";

interface MondayActionContext {
  apiKey: string;
  fetcher: ProviderFetch;
}

const runtimeActionHandlers: Record<string, MondayActionHandler> = {
  ...mondayAutomationActionHandlers,
  ...mondayCollaborationActionHandlers,
  ...mondayDiscoveryActionHandlers,
  ...mondayEnterpriseActionHandlers,
  ...mondayFormsActionHandlers,
  ...mondayStructureActionHandlers,
  ...mondayItemActionHandlers,
};

const actionHandlers = Object.fromEntries(
  Object.entries(runtimeActionHandlers).map(([actionName, handler]) => [
    actionName,
    (input: Record<string, unknown>, context: MondayActionContext) =>
      handler(
        {
          apiKey: context.apiKey,
          actionName,
          input,
        },
        context.fetcher,
      ),
  ]),
);

export const executors: ProviderExecutors = defineProviderExecutors<MondayActionContext>({
  service,
  handlers: actionHandlers,
  async createContext(context: ExecutionContext, fetcher: ProviderFetch): Promise<MondayActionContext> {
    const credential = await requireBearerCredential(context, service);
    return {
      apiKey: credential.accessToken,
      fetcher,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher }) {
    return validateMondayCredential({ apiKey: input.apiKey }, fetcher);
  },
  async oauth2(input, { fetcher }) {
    const grantedScopes = parseMondayScopeString(input.metadata.scope);
    return validateMondayCredential({ apiKey: input.accessToken }, fetcher, {
      grantedScopes: grantedScopes.length > 0 ? grantedScopes : getMondayAuthorizationScopes(),
    });
  },
};
