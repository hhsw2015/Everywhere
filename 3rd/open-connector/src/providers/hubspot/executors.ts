import type { CredentialValidators, ProviderExecutors } from "../../core/types.ts";
import type { OAuthProviderContext } from "../provider-runtime.ts";
import type { HubspotActionName } from "./actions.ts";

import { compactObject, nullableInteger, optionalRecord, optionalString, requiredRecord } from "../../core/cast.ts";
import { ProviderRequestError, defineOAuthProviderExecutors } from "../provider-runtime.ts";

type HubspotAccessTokenMetadata = {
  hubId: number;
  userId: number;
  userEmail?: string;
  hubDomain?: string;
  appId?: number;
  tokenType?: string;
  scopes: string[];
};

type HubspotActionContext = OAuthProviderContext;

type HubspotActionHandler = (input: Record<string, unknown>, context: HubspotActionContext) => Promise<unknown>;

const service = "hubspot";
const hubspotAccessTokenMetadataBaseUrl = "https://api.hubapi.com/oauth/v1/access-tokens";
export const hubspotApiBaseUrl = "https://api.hubapi.com";

export const hubspotActionHandlers: Record<HubspotActionName, HubspotActionHandler> = {
  search_contacts(input, context) {
    return searchHubspotRecords("contacts", input, context);
  },
  search_companies(input, context) {
    return searchHubspotRecords("companies", input, context);
  },
  search_deals(input, context) {
    return searchHubspotRecords("deals", input, context);
  },
  get_contact(input, context) {
    return getHubspotRecord("contacts", input, context);
  },
  get_company(input, context) {
    return getHubspotRecord("companies", input, context);
  },
  get_deal(input, context) {
    return getHubspotRecord("deals", input, context);
  },
  create_contact(input, context) {
    return createHubspotRecord("contacts", input, context);
  },
  create_company(input, context) {
    return createHubspotRecord("companies", input, context);
  },
  create_deal(input, context) {
    return createHubspotRecord("deals", input, context);
  },
  update_contact(input, context) {
    return updateHubspotRecord("contacts", input, context);
  },
  update_company(input, context) {
    return updateHubspotRecord("companies", input, context);
  },
  update_deal(input, context) {
    return updateHubspotRecord("deals", input, context);
  },
  list_properties(input, context) {
    return listHubspotProperties(input, context);
  },
  get_property(input, context) {
    return getHubspotProperty(input, context);
  },
};

export const executors: ProviderExecutors = defineOAuthProviderExecutors(service, hubspotActionHandlers);

export const credentialValidators: CredentialValidators = {
  async oauth2(input, { fetcher }) {
    return fetchHubspotCurrentAccount(input.accessToken, fetcher);
  },
};

async function fetchHubspotCurrentAccount(
  accessToken: string,
  fetcher: typeof fetch,
): Promise<{
  profile: {
    accountId: string;
    displayName: string;
  };
  grantedScopes: string[];
  metadata: Record<string, unknown>;
}> {
  const response = await fetcher(`${hubspotAccessTokenMetadataBaseUrl}/${encodeURIComponent(accessToken)}`, {
    headers: {
      accept: "application/json",
    },
  });

  const payload = parseHubspotAccessTokenMetadata(await readHubspotJson(response), response.status);
  const accountId = `${payload.hubId}:${payload.userId}`;

  return {
    profile: {
      accountId,
      displayName: buildHubspotAccountLabel(payload, accountId),
    },
    grantedScopes: payload.scopes,
    metadata: compactObject({
      hubId: payload.hubId,
      hubDomain: payload.hubDomain,
      userId: payload.userId,
      userEmail: payload.userEmail,
      appId: payload.appId,
      tokenType: payload.tokenType,
    }),
  };
}

async function searchHubspotRecords(objectType: string, input: Record<string, unknown>, context: HubspotActionContext) {
  const payload = await hubspotJsonRequest<Record<string, unknown>>(`/crm/v3/objects/${objectType}/search`, {
    accessToken: context.accessToken,
    fetcher: context.fetcher,
    method: "POST",
    body: compactObject({
      query: optionalString(input.query),
      filterGroups: Array.isArray(input.filterGroups) ? input.filterGroups : undefined,
      sorts: Array.isArray(input.sorts) ? input.sorts : undefined,
      properties: asStringArray(input.properties),
      propertiesWithHistory: asStringArray(input.propertiesWithHistory),
      associations: asStringArray(input.associations),
      limit: typeof input.limit === "number" ? input.limit : undefined,
      after: optionalString(input.after),
    }),
  });

  const results = Array.isArray(payload.results) ? payload.results.map((item) => normalizeHubspotRecord(item)) : [];
  const nextAfter = readHubspotNextAfter(payload);

  return {
    results,
    ...(nextAfter ? { paging: { nextAfter } } : {}),
  };
}

async function getHubspotRecord(objectType: string, input: Record<string, unknown>, context: HubspotActionContext) {
  const recordId = requireNonEmptyString(input.recordId, "recordId");
  const payload = await hubspotJsonRequest<unknown>(`/crm/v3/objects/${objectType}/${encodeURIComponent(recordId)}`, {
    accessToken: context.accessToken,
    fetcher: context.fetcher,
    query: compactObject({
      idProperty: optionalString(input.idProperty),
      properties: asStringArray(input.properties),
      propertiesWithHistory: asStringArray(input.propertiesWithHistory),
      associations: asStringArray(input.associations),
    }),
  });

  return {
    record: normalizeHubspotRecord(payload),
  };
}

async function createHubspotRecord(objectType: string, input: Record<string, unknown>, context: HubspotActionContext) {
  const payload = await hubspotJsonRequest<unknown>(`/crm/v3/objects/${objectType}`, {
    accessToken: context.accessToken,
    fetcher: context.fetcher,
    method: "POST",
    body: compactObject({
      properties: requireRecord(input.properties, "properties"),
      associations: Array.isArray(input.associations) ? input.associations : undefined,
    }),
  });

  return {
    record: normalizeHubspotRecord(payload),
  };
}

async function updateHubspotRecord(objectType: string, input: Record<string, unknown>, context: HubspotActionContext) {
  const recordId = requireNonEmptyString(input.recordId, "recordId");
  const payload = await hubspotJsonRequest<unknown>(`/crm/v3/objects/${objectType}/${encodeURIComponent(recordId)}`, {
    accessToken: context.accessToken,
    fetcher: context.fetcher,
    method: "PATCH",
    query: compactObject({
      idProperty: optionalString(input.idProperty),
    }),
    body: {
      properties: requireRecord(input.properties, "properties"),
    },
  });

  return {
    record: normalizeHubspotRecord(payload),
  };
}

async function listHubspotProperties(input: Record<string, unknown>, context: HubspotActionContext) {
  const objectType = requireNonEmptyString(input.objectType, "objectType");
  const payload = await hubspotJsonRequest<unknown>(`/crm/v3/properties/${encodeURIComponent(objectType)}`, {
    accessToken: context.accessToken,
    fetcher: context.fetcher,
  });

  const record = optionalRecord(payload);
  const rawProperties = Array.isArray(payload) ? payload : Array.isArray(record?.results) ? record.results : [];

  return {
    properties: rawProperties.map((item) => normalizeHubspotPropertyDefinition(item)),
  };
}

async function getHubspotProperty(input: Record<string, unknown>, context: HubspotActionContext) {
  const objectType = requireNonEmptyString(input.objectType, "objectType");
  const propertyName = requireNonEmptyString(input.propertyName, "propertyName");
  const payload = await hubspotJsonRequest<unknown>(
    `/crm/v3/properties/${encodeURIComponent(objectType)}/${encodeURIComponent(propertyName)}`,
    {
      accessToken: context.accessToken,
      fetcher: context.fetcher,
    },
  );

  return {
    property: normalizeHubspotPropertyDefinition(payload),
  };
}

async function hubspotJsonRequest<T>(
  path: string,
  input: {
    accessToken: string;
    fetcher: typeof fetch;
    method?: string;
    query?: Record<string, string | string[] | undefined>;
    body?: Record<string, unknown>;
  },
): Promise<T> {
  const url = new URL(`${hubspotApiBaseUrl}${path}`);
  for (const [key, value] of Object.entries(input.query ?? {})) {
    if (Array.isArray(value)) {
      url.searchParams.append(key, value.join(","));
      continue;
    }
    if (value) {
      url.searchParams.append(key, value);
    }
  }

  const response = await input.fetcher(url.toString(), {
    method: input.method ?? "GET",
    headers: compactObject({
      authorization: `Bearer ${input.accessToken}`,
      accept: "application/json",
      ...(input.body ? { "content-type": "application/json" } : {}),
    }),
    body: input.body ? JSON.stringify(input.body) : undefined,
  });

  return (await readHubspotJson(response)) as T;
}

async function readHubspotJson(response: Response): Promise<unknown> {
  let payload: unknown = null;
  try {
    payload = await response.json();
  } catch {
    payload = null;
  }

  if (!response.ok) {
    throw toHubspotError(response.status, payload);
  }

  return payload;
}

function parseHubspotAccessTokenMetadata(payload: unknown, status: number): HubspotAccessTokenMetadata {
  const body = optionalRecord(payload);
  if (!body) {
    throw new ProviderRequestError(502, `malformed hubspot access token metadata (${status})`);
  }

  const hubId = asPositiveFiniteNumber(body.hub_id);
  if (hubId == null) {
    throw new ProviderRequestError(502, "malformed hubspot access token metadata: hub_id");
  }

  const userId = asPositiveFiniteNumber(body.user_id);
  if (userId == null) {
    throw new ProviderRequestError(502, "malformed hubspot access token metadata: user_id");
  }

  return {
    hubId,
    userId,
    userEmail: optionalString(body.user),
    hubDomain: optionalString(body.hub_domain),
    appId: asPositiveFiniteNumber(body.app_id) ?? undefined,
    tokenType: optionalString(body.token_type),
    scopes: parseHubspotProviderScopes(body),
  };
}

function parseHubspotProviderScopes(body: Record<string, unknown>): string[] {
  const scope = body.scope;
  if (typeof scope === "string") {
    return scope
      .split(" ")
      .map((item) => item.trim())
      .filter(Boolean)
      .sort();
  }

  const scopes = body.scopes;
  if (Array.isArray(scopes)) {
    return scopes
      .map((item) => optionalString(item))
      .filter((item): item is string => Boolean(item))
      .sort();
  }

  return [];
}

function buildHubspotAccountLabel(payload: HubspotAccessTokenMetadata, accountId: string): string {
  if (payload.userEmail && payload.hubDomain) {
    return `${payload.userEmail} (${payload.hubDomain})`;
  }

  if (payload.userEmail) {
    return `${payload.userEmail} (${payload.hubId})`;
  }

  return accountId;
}

function toHubspotError(status: number, payload: unknown): ProviderRequestError {
  const message = extractHubspotErrorMessage(payload) ?? `hubspot request failed with status ${status}`;

  if (status === 401) {
    return new ProviderRequestError(401, message);
  }
  if (status === 400 || status === 404) {
    return new ProviderRequestError(400, message);
  }
  if (status === 429) {
    return new ProviderRequestError(429, message);
  }

  return new ProviderRequestError(502, message, status || 500);
}

function extractHubspotErrorMessage(payload: unknown): string | undefined {
  const body = optionalRecord(payload);
  if (!body) {
    return undefined;
  }

  return optionalString(body.error_description) ?? optionalString(body.message) ?? optionalString(body.error);
}

function normalizeHubspotRecord(payload: unknown): Record<string, unknown> {
  const body = optionalRecord(payload);
  if (!body) {
    throw new ProviderRequestError(502, "malformed hubspot record payload");
  }

  return compactObject({
    id: requireStringField(body.id, "id"),
    archived: body.archived === true,
    createdAt: optionalString(body.createdAt),
    updatedAt: optionalString(body.updatedAt),
    properties: normalizeHubspotProperties(body.properties),
    propertiesWithHistory: optionalRecord(body.propertiesWithHistory),
    associations: optionalRecord(body.associations),
  });
}

function normalizeHubspotProperties(payload: unknown): Record<string, string | null> {
  const body = optionalRecord(payload);
  if (!body) {
    return {};
  }

  return Object.fromEntries(Object.entries(body).map(([key, value]) => [key, normalizeHubspotPropertyValue(value)]));
}

function normalizeHubspotPropertyValue(value: unknown): string | null {
  if (value == null) {
    return null;
  }

  if (typeof value === "string") {
    return value;
  }

  if (typeof value === "number" || typeof value === "boolean") {
    return String(value);
  }

  return JSON.stringify(value);
}

function readHubspotNextAfter(payload: Record<string, unknown>): string | undefined {
  const paging = optionalRecord(payload.paging);
  const next = optionalRecord(paging?.next);
  return optionalString(next?.after);
}

function normalizeHubspotPropertyDefinition(payload: unknown): Record<string, unknown> {
  const body = optionalRecord(payload);
  if (!body) {
    throw new ProviderRequestError(502, "malformed hubspot property definition payload");
  }

  return {
    name: requireStringField(body.name, "name"),
    label: optionalString(body.label) ?? requireStringField(body.name, "name"),
    type: optionalString(body.type) ?? "",
    fieldType: optionalString(body.fieldType) ?? "",
    description: optionalString(body.description) ?? null,
    groupName: optionalString(body.groupName) ?? null,
    hasUniqueValue: body.hasUniqueValue === true,
    hidden: body.hidden === true,
    options: Array.isArray(body.options) ? body.options.map((item) => normalizeHubspotPropertyOption(item)) : [],
  };
}

function normalizeHubspotPropertyOption(payload: unknown): Record<string, unknown> {
  const body = optionalRecord(payload);
  if (!body) {
    throw new ProviderRequestError(502, "malformed hubspot property option payload");
  }

  return {
    label: optionalString(body.label) ?? "",
    value: optionalString(body.value) ?? "",
    description: optionalString(body.description) ?? null,
    displayOrder: nullableInteger(body.displayOrder) ?? null,
    hidden: body.hidden === true,
  };
}

function asPositiveFiniteNumber(value: unknown): number | undefined {
  if (typeof value === "number" && Number.isFinite(value) && value > 0) {
    return value;
  }

  if (typeof value === "string" && value.trim()) {
    const parsed = Number(value);
    if (Number.isFinite(parsed) && parsed > 0) {
      return parsed;
    }
  }

  return undefined;
}

function asStringArray(value: unknown): string[] | undefined {
  if (!Array.isArray(value)) {
    return undefined;
  }

  const items = value.map((item) => optionalString(item)).filter((item): item is string => Boolean(item));
  return items.length > 0 ? items : undefined;
}

function requireRecord(value: unknown, fieldName: string): Record<string, unknown> {
  return requiredRecord(value, fieldName, (message) => new ProviderRequestError(400, message));
}

function requireStringField(value: unknown, fieldName: string): string {
  const parsed = optionalString(value);
  if (!parsed) {
    throw new ProviderRequestError(502, `malformed hubspot record payload: ${fieldName}`);
  }
  return parsed;
}

function requireNonEmptyString(value: unknown, fieldName: string): string {
  const parsed = optionalString(value);
  if (!parsed) {
    throw new ProviderRequestError(400, `${fieldName} is required`);
  }
  return parsed;
}
