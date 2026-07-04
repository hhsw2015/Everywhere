import type { CredentialValidationResult, CredentialValidators, ProviderExecutors } from "../../core/types.ts";
import type { OAuthProviderContext } from "../provider-runtime.ts";
import type { LongbridgeActionName } from "./actions.ts";

import { compactObject, optionalRecord, optionalString, requiredString, stringArray } from "../../core/cast.ts";
import { defineOAuthProviderExecutors, providerUserAgent, ProviderRequestError } from "../provider-runtime.ts";
import { longbridgeOAuthScopes } from "./actions.ts";

const service = "longbridge";
const longbridgeApiBaseUrl = "https://openapi.longbridge.com";

interface LongbridgeRequestInput {
  path: string;
  context: Pick<OAuthProviderContext, "accessToken" | "fetcher" | "signal">;
  phase: "connect" | "execute";
  query?: Record<string, string | string[] | undefined>;
}

type LongbridgeActionHandler = (input: Record<string, unknown>, context: OAuthProviderContext) => Promise<unknown>;

export const longbridgeActionHandlers: Record<LongbridgeActionName, LongbridgeActionHandler> = {
  async list_securities(input, context) {
    const payload = await requestLongbridgeJson({
      path: "/v1/quote/get_security_list",
      context,
      phase: "execute",
      query: {
        market: requiredString(input.market, "market"),
        category: requiredString(input.category, "category"),
      },
    });

    return {
      securities: readLongbridgeDataList(payload),
      raw: payload,
    };
  },
  async list_account_cash(input, context) {
    const payload = await requestLongbridgeJson({
      path: "/v1/asset/account",
      context,
      phase: "execute",
      query: compactObject({
        currency: optionalString(input.currency),
      }),
    });

    return {
      balances: readLongbridgeDataList(payload),
      raw: payload,
    };
  },
  async list_stock_positions(input, context) {
    const payload = await requestLongbridgeJson({
      path: "/v1/asset/stock",
      context,
      phase: "execute",
      query: compactObject({
        symbol: input.symbols === undefined ? undefined : stringArray(input.symbols, "symbols"),
      }),
    });

    return {
      positionGroups: readLongbridgeDataList(payload),
      raw: payload,
    };
  },
};

export const executors: ProviderExecutors = defineOAuthProviderExecutors(service, longbridgeActionHandlers);

export const credentialValidators: CredentialValidators = {
  async oauth2(input, { fetcher, signal }) {
    return validateLongbridgeCredential(input.accessToken, fetcher, signal);
  },
};

async function validateLongbridgeCredential(
  accessToken: string,
  fetcher: typeof fetch,
  signal?: AbortSignal,
): Promise<CredentialValidationResult> {
  const payload = await requestLongbridgeJson({
    path: "/v1/asset/account",
    context: {
      accessToken,
      fetcher,
      signal,
    },
    phase: "connect",
  });
  const records = readLongbridgeDataList(payload);
  const firstRecord = optionalRecord(records[0]);
  const primaryCurrency = optionalString(firstRecord?.currency);

  return {
    profile: {
      accountId: "longbridge:account",
      displayName: "Longbridge account",
      grantedScopes: longbridgeOAuthScopes,
    },
    grantedScopes: longbridgeOAuthScopes,
    metadata: compactObject({
      apiBaseUrl: longbridgeApiBaseUrl,
      validationEndpoint: "/v1/asset/account",
      primaryCurrency,
      balanceCount: records.length,
    }),
  };
}

async function requestLongbridgeJson(input: LongbridgeRequestInput): Promise<unknown> {
  const url = buildLongbridgeUrl(input.path, input.query);
  let response: Response;
  try {
    response = await input.context.fetcher(url, {
      method: "GET",
      headers: {
        accept: "application/json",
        authorization: `Bearer ${input.context.accessToken}`,
        "user-agent": providerUserAgent,
      },
      signal: input.context.signal,
    });
  } catch (error) {
    throw new ProviderRequestError(
      502,
      error instanceof Error ? `Longbridge request failed: ${error.message}` : "Longbridge request failed",
    );
  }

  const payload = await readLongbridgeJson(response);
  if (!response.ok) {
    throw mapLongbridgeHttpError(response.status, payload, input.phase);
  }
  return payload;
}

function buildLongbridgeUrl(path: string, query: Record<string, string | string[] | undefined> = {}): string {
  const normalizedPath = path.startsWith("/") ? path.slice(1) : path;
  const url = new URL(normalizedPath, `${longbridgeApiBaseUrl}/`);
  for (const [key, value] of Object.entries(query)) {
    if (value === undefined) {
      continue;
    }
    if (Array.isArray(value)) {
      for (const item of value) {
        url.searchParams.append(key, item);
      }
      continue;
    }
    url.searchParams.set(key, value);
  }
  return url.toString();
}

async function readLongbridgeJson(response: Response): Promise<unknown> {
  const text = await response.text();
  if (!text) {
    return {};
  }
  try {
    return JSON.parse(text) as unknown;
  } catch {
    return { message: text };
  }
}

function readLongbridgeDataList(payload: unknown): unknown[] {
  const data = optionalRecord(optionalRecord(payload)?.data);
  const list = data?.list;
  if (!Array.isArray(list)) {
    throw new ProviderRequestError(502, "Longbridge response is missing data.list");
  }
  return list;
}

function mapLongbridgeHttpError(status: number, payload: unknown, phase: "connect" | "execute"): ProviderRequestError {
  const message = readLongbridgeErrorMessage(payload) ?? `Longbridge request failed with HTTP ${status}`;
  if (status === 429) {
    return new ProviderRequestError(429, message, payload);
  }
  if (phase === "connect" && (status === 400 || status === 401 || status === 403)) {
    return new ProviderRequestError(400, message, payload);
  }
  if (status === 401 || status === 403) {
    return new ProviderRequestError(status, message, payload);
  }
  if (status >= 400 && status < 500) {
    return new ProviderRequestError(status, message, payload);
  }
  return new ProviderRequestError(502, message, payload);
}

function readLongbridgeErrorMessage(payload: unknown): string | undefined {
  const object = optionalRecord(payload);
  return optionalString(object?.message) ?? optionalString(object?.error_description) ?? optionalString(object?.error);
}
