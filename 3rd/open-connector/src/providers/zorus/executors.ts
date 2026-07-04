import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";
import type { ApiKeyProviderContext, ProviderRuntimeHandler } from "../provider-runtime.ts";

import { arrayPayload, requestJson } from "../http-json-runtime.ts";
import { defineApiKeyProviderExecutors } from "../provider-runtime.ts";

const service = "zorus";
const apiBaseUrl = "https://developer.zorustech.com";
const apiVersion = "1.0";
const validationPath = "/api/customers/search";

type Handler = ProviderRuntimeHandler<ApiKeyProviderContext>;

const paths: Record<string, string> = {
  search_customers: validationPath,
  search_endpoints: "/api/endpoints/search",
  search_groups: "/api/groups/search",
  search_policies: "/api/policies/search",
  search_active_unblock_requests: "/api/unblock-requests/active/search",
};

export const zorusActionHandlers: Record<string, Handler> = {
  search_customers(input, context) {
    return searchZorus(paths.search_customers, input, context);
  },
  search_endpoints(input, context) {
    return searchZorus(paths.search_endpoints, input, context);
  },
  search_groups(input, context) {
    return searchZorus(paths.search_groups, input, context);
  },
  search_policies(input, context) {
    return searchZorus(paths.search_policies, input, context);
  },
  search_active_unblock_requests(input, context) {
    return searchZorus(paths.search_active_unblock_requests, input, context);
  },
};

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, zorusActionHandlers);

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }) {
    await zorusRequest(validationPath, { apiKey: input.apiKey, fetcher, signal }, { page: 1, pageSize: 1 }, "validate");
    return {
      profile: {
        accountId: "zorus-api-token",
        displayName: "Zorus API Token",
      },
      grantedScopes: [],
      metadata: {
        apiBaseUrl,
        apiVersion,
        validationEndpoint: validationPath,
      },
    };
  },
};

function zorusRequest(
  path: string,
  context: Pick<ApiKeyProviderContext, "apiKey" | "fetcher" | "signal">,
  body: unknown,
  phase: "validate" | "execute" = "execute",
): Promise<unknown> {
  return requestJson({
    providerName: "Zorus",
    baseUrl: apiBaseUrl,
    path,
    fetcher: context.fetcher,
    signal: context.signal,
    method: "POST",
    body,
    phase,
    headers: {
      "Zorus-Api-Version": apiVersion,
      authorization: `Impersonation ${context.apiKey}`,
    },
  });
}

async function searchZorus(
  path: string,
  input: Record<string, unknown>,
  context: ApiKeyProviderContext,
): Promise<Record<string, unknown>> {
  return {
    items: arrayPayload(await zorusRequest(path, context, input), "Zorus search results"),
  };
}
