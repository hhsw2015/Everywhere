import type { CredentialValidators, ExecutionContext, ProviderExecutors } from "../../core/types.ts";
import type { HtmlCssToImageActionContext } from "./runtime.ts";

import { defineProviderExecutors, requireApiKeyCredential } from "../provider-runtime.ts";
import {
  htmlCssToImageActionHandlers,
  resolveHtmlCssToImageUserId,
  validateHtmlCssToImageCredential,
} from "./runtime.ts";

const service = "htmlcsstoimage";

export const executors: ProviderExecutors = defineProviderExecutors<HtmlCssToImageActionContext>({
  service,
  handlers: htmlCssToImageActionHandlers,
  async createContext(context: ExecutionContext, fetcher: typeof fetch): Promise<HtmlCssToImageActionContext> {
    const credential = await requireApiKeyCredential(context, service);
    return {
      credential: {
        apiKey: credential.apiKey,
        userId: resolveHtmlCssToImageUserId({
          values: credential.values,
          metadata: credential.metadata,
        }),
      },
      fetcher,
      signal: context.signal,
    };
  },
});

export const credentialValidators: CredentialValidators = {
  async apiKey(input, options) {
    return validateHtmlCssToImageCredential(input, options);
  },
};
