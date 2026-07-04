import type {
  CredentialValidators,
  CredentialValidationResult,
  ExecutionContext,
  ProviderExecutors,
  ResolvedCredential,
} from "../../core/types.ts";
import type { TwitterActionContext } from "./runtime.ts";

import { optionalRecord, optionalString } from "../../core/cast.ts";
import { defineProviderExecutors, ProviderRequestError } from "../provider-runtime.ts";
import { fetchTwitterCurrentAccount, twitterActionHandlers } from "./runtime.ts";

const service = "twitter";

export const executors: ProviderExecutors = defineProviderExecutors<TwitterActionContext>({
  service,
  handlers: twitterActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch): Promise<TwitterActionContext> {
    const credential = await context.getCredential(service);
    if (credential?.authType === "oauth2") {
      return {
        userAccessToken: credential.accessToken,
        appBearerToken: readOAuthAppBearerToken(credential),
        fetcher,
        signal: context.signal,
      };
    }

    if (credential?.authType === "custom_credential") {
      return {
        userAccessToken: credential.values.userAccessToken,
        appBearerToken: credential.values.appBearerToken,
        fetcher,
        signal: context.signal,
      };
    }

    throw new ProviderRequestError(401, "Connect twitter with OAuth or configure twitter custom credentials first.");
  },
});

export const credentialValidators: CredentialValidators = {
  async oauth2(input, { fetcher, signal }): Promise<CredentialValidationResult> {
    const validation = await fetchTwitterCurrentAccount(input.accessToken, fetcher, signal);
    return {
      ...validation,
      metadata: {
        ...input.metadata,
        ...validation.metadata,
      },
    };
  },

  async customCredential(input, { fetcher, signal }): Promise<CredentialValidationResult> {
    const userAccessToken = input.values.userAccessToken?.trim();
    if (userAccessToken) {
      return fetchTwitterCurrentAccount(userAccessToken, fetcher, signal);
    }

    if (input.values.appBearerToken?.trim()) {
      return {
        profile: {
          accountId: "twitter:app_bearer_token",
          displayName: "X App Bearer Token",
        },
        grantedScopes: [],
        metadata: {
          credentialMode: "app_bearer_token",
        },
      };
    }

    throw new ProviderRequestError(401, "Configure a twitter user access token or app bearer token first.");
  },
};

function readOAuthAppBearerToken(credential: Extract<ResolvedCredential, { authType: "oauth2" }>): string | undefined {
  return optionalString(optionalRecord(credential.metadata.oauthClientSecretExtra)?.appBearerToken);
}
