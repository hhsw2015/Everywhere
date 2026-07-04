import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";

import { optionalNumber, optionalRecord, optionalString, requiredString } from "../../core/cast.ts";
import { defineProviderExecutors, ProviderRequestError, requireCustomCredential } from "../provider-runtime.ts";
import { dataForSeoActionHandlers, requestDataForSeoUserData } from "./runtime.ts";

const service = "dataforseo";

interface DataForSeoContext {
  login: string;
  password: string;
  fetcher: typeof fetch;
}

export const executors: ProviderExecutors = defineProviderExecutors<DataForSeoContext>({
  service,
  handlers: dataForSeoActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch): Promise<DataForSeoContext> {
    const credential = await requireCustomCredential(context, service);
    return {
      login: requiredString(credential.values.login, "login", (message) => new ProviderRequestError(400, message)),
      password: requiredString(
        credential.values.password,
        "password",
        (message) => new ProviderRequestError(400, message),
      ),
      fetcher,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  async customCredential(input, { fetcher }) {
    const login = requiredString(input.values.login, "login", (message) => new ProviderRequestError(400, message));
    const password = requiredString(
      input.values.password,
      "password",
      (message) => new ProviderRequestError(400, message),
    );
    const userData = await requestDataForSeoUserData({ login, password, fetcher }, "validate");
    const money = optionalRecord(userData.money);
    const account = optionalString(userData.login) ?? login;

    return {
      profile: {
        accountId: account,
        displayName: account,
      },
      grantedScopes: [],
      metadata: {
        apiBaseUrl: "https://api.dataforseo.com/v3",
        login: account,
        timezone: optionalString(userData.timezone),
        balance: optionalNumber(money?.balance),
        totalDeposited: optionalNumber(money?.total),
        validationEndpoint: "/appendix/user_data",
      },
    };
  },
};
