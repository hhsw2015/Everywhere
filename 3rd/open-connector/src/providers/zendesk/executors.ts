import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";

import {
  defineProviderExecutors,
  ProviderRequestError,
  requireApiKeyCredential,
  requireOAuthCredential,
} from "../provider-runtime.ts";
import { validateZendeskCredential, zendeskActionHandlers } from "./runtime.ts";

const service = "zendesk";

export const executors: ProviderExecutors = defineProviderExecutors({
  service,
  handlers: zendeskActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch) {
    const credential = await context.getCredential(service);
    if (credential?.authType === "oauth2") {
      const oauth = await requireOAuthCredential(context, service);
      return {
        authType: "oauth2" as const,
        accessToken: oauth.accessToken,
        baseUrl: resolveBaseUrl(oauth.metadata),
        subdomain: resolveSubdomain(oauth.metadata),
        fetcher,
        signal: context.signal,
      };
    }
    const apiKey = await requireApiKeyCredential(context, service);
    return {
      authType: "api_key" as const,
      apiKey: apiKey.apiKey,
      email: requireValue(apiKey.values.email ?? stringMetadata(apiKey.metadata.email), "Zendesk email is required"),
      baseUrl: resolveBaseUrl(apiKey.metadata, apiKey.values),
      subdomain: resolveSubdomain(apiKey.metadata, apiKey.values),
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  apiKey(input, { fetcher, signal }) {
    return validateZendeskCredential(input.apiKey, input.values, fetcher, signal);
  },
  oauth2(input, { fetcher, signal }) {
    return validateZendeskCredential(input.accessToken, input.metadata, fetcher, signal, "oauth2");
  },
};

function resolveBaseUrl(metadata: Record<string, unknown>, values?: Record<string, unknown>): string {
  const existing = stringMetadata(metadata.baseUrl);
  if (existing) return existing;
  return `https://${resolveSubdomain(metadata, values)}.zendesk.com`;
}

function resolveSubdomain(metadata: Record<string, unknown>, values?: Record<string, unknown>): string {
  return normalizeZendeskSubdomain(
    requireValue(
      stringMetadata(metadata.subdomain) ??
        nestedString(metadata.oauthClientExtra, "subdomain") ??
        stringMetadata(values?.subdomain),
      "Zendesk subdomain is required",
    ),
  );
}

function stringMetadata(value: unknown): string | undefined {
  return typeof value === "string" && value.trim() ? value.trim() : undefined;
}

function nestedString(value: unknown, key: string): string | undefined {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return undefined;
  }
  return stringMetadata((value as Record<string, unknown>)[key]);
}

function requireValue(value: string | undefined, message: string): string {
  if (value) return value;
  throw new ProviderRequestError(400, message);
}

function normalizeZendeskSubdomain(raw: string): string {
  let candidate = raw.trim();
  if (candidate.startsWith("http://") || candidate.startsWith("https://")) {
    const url = new URL(candidate);
    candidate = url.hostname;
  }
  const lower = candidate.toLowerCase();
  const subdomain = lower.endsWith(".zendesk.com") ? lower.slice(0, -".zendesk.com".length) : lower;
  if (!subdomain || subdomain.includes(".") || subdomain.startsWith("-") || subdomain.endsWith("-")) {
    throw new ProviderRequestError(400, "Zendesk subdomain is invalid");
  }
  for (const character of subdomain) {
    const ok = (character >= "a" && character <= "z") || (character >= "0" && character <= "9") || character === "-";
    if (!ok) throw new ProviderRequestError(400, "Zendesk subdomain is invalid");
  }
  return subdomain;
}
