import type { CredentialValidationResult, CredentialValidators, ProviderExecutors } from "../../core/types.ts";
import type { ApiKeyProviderContext } from "../provider-runtime.ts";
import type { TelegramActionName } from "./actions.ts";

import { optionalBoolean, optionalNumber, optionalRecord, optionalString } from "../../core/cast.ts";
import { assertPublicHttpUrl } from "../../core/request.ts";
import { defineApiKeyProviderExecutors, ProviderRequestError } from "../provider-runtime.ts";

const service = "telegram";
const telegramApiBaseUrl = "https://api.telegram.org";
const telegramDefaultRequestTimeoutMs = 30_000;

interface TelegramApiEnvelope<T> {
  ok?: boolean;
  result?: T;
  description?: unknown;
  error_code?: unknown;
  parameters?: {
    retry_after?: unknown;
  };
}

type TelegramActionHandler = (input: Record<string, unknown>, context: ApiKeyProviderContext) => Promise<unknown>;

export const telegramActionHandlers: Record<TelegramActionName, TelegramActionHandler> = {
  async get_me(_input, context): Promise<unknown> {
    return normalizeTelegramUser(
      await telegramRequest<Record<string, unknown>>({
        botToken: context.apiKey,
        method: "getMe",
        context,
        phase: "execute",
      }),
    );
  },
  async get_webhook_info(_input, context): Promise<unknown> {
    return normalizeWebhookInfo(
      await telegramRequest<Record<string, unknown>>({
        botToken: context.apiKey,
        method: "getWebhookInfo",
        context,
        phase: "execute",
      }),
    );
  },
  async get_updates(input, context): Promise<unknown> {
    const result = await telegramRequest<Array<Record<string, unknown>>>({
      botToken: context.apiKey,
      method: "getUpdates",
      body: compactTelegramBody({
        offset: input.offset,
        limit: input.limit,
        timeout: input.timeout,
        allowed_updates: input.allowedUpdates,
      }),
      context,
      phase: "execute",
    });
    return { updates: result.map((update) => normalizeUpdate(update)) };
  },
  async get_chat_history(input, context): Promise<unknown> {
    const result = await telegramRequest<Array<Record<string, unknown>>>({
      botToken: context.apiKey,
      method: "getUpdates",
      body: compactTelegramBody({
        offset: input.offset,
        limit: input.limit ?? 100,
        allowed_updates: ["message", "edited_message", "channel_post", "edited_channel_post"],
      }),
      context,
      phase: "execute",
    });
    const messages = result.flatMap((update) =>
      extractHistoryEntries(update, {
        chatId: input.chatId,
        messageId: optionalNumber(input.messageId),
      }),
    );
    const updateIds = result
      .map((update) => optionalNumber(update.update_id))
      .filter((value): value is number => value != null);
    return {
      messages,
      nextUpdateOffset: updateIds.length > 0 ? Math.max(...updateIds) + 1 : null,
    };
  },
  async send_message(input, context): Promise<unknown> {
    const result = await telegramRequest<Record<string, unknown>>({
      botToken: context.apiKey,
      method: "sendMessage",
      body: compactTelegramBody({
        chat_id: input.chatId,
        text: input.text,
        parse_mode: input.parseMode,
        disable_notification: input.disableNotification,
        protect_content: input.protectContent,
        message_thread_id: input.messageThreadId,
        reply_parameters: input.replyToMessageId != null ? { message_id: input.replyToMessageId } : undefined,
        link_preview_options: input.disableWebPagePreview === true ? { is_disabled: true } : undefined,
      }),
      context,
      phase: "execute",
    });
    return normalizeMessage(result);
  },
  async edit_message_text(input, context): Promise<unknown> {
    validateEditMessageTarget(input);
    const result = await telegramRequest<Record<string, unknown> | boolean>({
      botToken: context.apiKey,
      method: "editMessageText",
      body: compactTelegramBody({
        chat_id: input.chatId,
        message_id: input.messageId,
        inline_message_id: input.inlineMessageId,
        text: input.text,
        parse_mode: input.parseMode,
        link_preview_options: input.disableWebPagePreview === true ? { is_disabled: true } : undefined,
      }),
      context,
      phase: "execute",
    });
    if (result === true) {
      return {
        edited: true,
        message: null,
        inlineMessageId: optionalString(input.inlineMessageId) ?? null,
      };
    }
    return {
      edited: true,
      message: normalizeMessage(asRecord(result)),
      inlineMessageId: null,
    };
  },
  async send_photo(input, context): Promise<unknown> {
    const result = await telegramRequest<Record<string, unknown>>({
      botToken: context.apiKey,
      method: "sendPhoto",
      body: compactTelegramBody({
        chat_id: input.chatId,
        photo: validateTelegramUrlOrFileId(input.photo, "photo"),
        caption: input.caption,
        parse_mode: input.parseMode,
        disable_notification: input.disableNotification,
        protect_content: input.protectContent,
        message_thread_id: input.messageThreadId,
        reply_parameters: input.replyToMessageId != null ? { message_id: input.replyToMessageId } : undefined,
      }),
      context,
      phase: "execute",
    });
    return normalizeMessage(result);
  },
  async send_document(input, context): Promise<unknown> {
    const result = await telegramRequest<Record<string, unknown>>({
      botToken: context.apiKey,
      method: "sendDocument",
      body: compactTelegramBody({
        chat_id: input.chatId,
        document: validateTelegramUrlOrFileId(input.document, "document"),
        caption: input.caption,
        parse_mode: input.parseMode,
        thumbnail: validateTelegramUrlOrFileId(input.thumbnail, "thumbnail"),
        reply_markup: normalizeJsonLikeInput(input.replyMarkup),
        reply_to_message_id: input.replyToMessageId,
        disable_notification: input.disableNotification,
        disable_content_type_detection: input.disableContentTypeDetection,
      }),
      context,
      phase: "execute",
    });
    return normalizeMessage(result);
  },
  async send_poll(input, context): Promise<unknown> {
    if (input.openPeriod != null && input.closeDate != null) {
      throw new ProviderRequestError(400, "send_poll accepts only one of openPeriod or closeDate");
    }
    if (input.type === "quiz" && input.correctOptionId == null) {
      throw new ProviderRequestError(400, "send_poll quiz polls require correctOptionId");
    }
    const result = await telegramRequest<Record<string, unknown>>({
      botToken: context.apiKey,
      method: "sendPoll",
      body: compactTelegramBody({
        chat_id: input.chatId,
        question: input.question,
        options: input.options,
        type: input.type,
        is_anonymous: input.isAnonymous,
        allows_multiple_answers: input.allowsMultipleAnswers,
        correct_option_id: input.correctOptionId,
        explanation: input.explanation,
        explanation_parse_mode: input.explanationParseMode,
        open_period: input.openPeriod,
        close_date: input.closeDate,
        is_closed: input.isClosed,
        disable_notification: input.disableNotification,
        reply_to_message_id: input.replyToMessageId,
        reply_markup: normalizeJsonLikeInput(input.replyMarkup),
      }),
      context,
      phase: "execute",
    });
    return normalizeMessage(result);
  },
  async get_chat(input, context): Promise<unknown> {
    return normalizeTelegramChat(
      await telegramRequest<Record<string, unknown>>({
        botToken: context.apiKey,
        method: "getChat",
        body: { chat_id: input.chatId },
        context,
        phase: "execute",
      }),
    );
  },
  async get_chat_member(input, context): Promise<unknown> {
    return normalizeChatMember(
      await telegramRequest<Record<string, unknown>>({
        botToken: context.apiKey,
        method: "getChatMember",
        body: {
          chat_id: input.chatId,
          user_id: input.userId,
        },
        context,
        phase: "execute",
      }),
    );
  },
  async get_chat_administrators(input, context): Promise<unknown> {
    const result = await telegramRequest<Array<Record<string, unknown>>>({
      botToken: context.apiKey,
      method: "getChatAdministrators",
      body: { chat_id: input.chatId },
      context,
      phase: "execute",
    });
    return { administrators: result.map((member) => normalizeChatMember(member)) };
  },
  async get_chat_members_count(input, context): Promise<unknown> {
    const result = await telegramRequest<number>({
      botToken: context.apiKey,
      method: "getChatMemberCount",
      body: { chat_id: input.chatId },
      context,
      phase: "execute",
    });
    return { memberCount: result };
  },
  async delete_message(input, context): Promise<unknown> {
    await telegramRequest<boolean>({
      botToken: context.apiKey,
      method: "deleteMessage",
      body: {
        chat_id: input.chatId,
        message_id: input.messageId,
      },
      context,
      phase: "execute",
    });
    return { success: true };
  },
  async forward_message(input, context): Promise<unknown> {
    const result = await telegramRequest<Record<string, unknown>>({
      botToken: context.apiKey,
      method: "forwardMessage",
      body: compactTelegramBody({
        chat_id: input.chatId,
        from_chat_id: input.fromChatId,
        message_id: input.messageId,
        disable_notification: input.disableNotification,
      }),
      context,
      phase: "execute",
    });
    return normalizeMessage(result);
  },
  async send_location(input, context): Promise<unknown> {
    const result = await telegramRequest<Record<string, unknown>>({
      botToken: context.apiKey,
      method: "sendLocation",
      body: compactTelegramBody({
        chat_id: input.chatId,
        latitude: input.latitude,
        longitude: input.longitude,
        horizontal_accuracy: input.horizontalAccuracy,
        live_period: input.livePeriod,
        heading: input.heading,
        proximity_alert_radius: input.proximityAlertRadius,
        disable_notification: input.disableNotification,
        reply_to_message_id: input.replyToMessageId,
        reply_markup: normalizeJsonLikeInput(input.replyMarkup),
      }),
      context,
      phase: "execute",
    });
    return normalizeMessage(result);
  },
  async create_chat_invite_link(input, context): Promise<unknown> {
    const result = await telegramRequest<string>({
      botToken: context.apiKey,
      method: "exportChatInviteLink",
      body: { chat_id: input.chatId },
      context,
      phase: "execute",
    });
    return { inviteLink: result };
  },
  async answer_callback_query(input, context): Promise<unknown> {
    await telegramRequest<boolean>({
      botToken: context.apiKey,
      method: "answerCallbackQuery",
      body: compactTelegramBody({
        callback_query_id: input.callbackQueryId,
        text: input.text,
        show_alert: input.showAlert,
        url: validateTelegramUrl(input.url, "url"),
        cache_time: input.cacheTime,
      }),
      context,
      phase: "execute",
    });
    return { success: true };
  },
  async set_my_commands(input, context): Promise<unknown> {
    await telegramRequest<boolean>({
      botToken: context.apiKey,
      method: "setMyCommands",
      body: compactTelegramBody({
        commands: input.commands,
        scope: normalizeJsonLikeInput(input.scope),
        language_code: input.languageCode,
      }),
      context,
      phase: "execute",
    });
    return { success: true };
  },
  async set_webhook(input, context): Promise<unknown> {
    await telegramRequest<boolean>({
      botToken: context.apiKey,
      method: "setWebhook",
      body: compactTelegramBody({
        url: validateTelegramUrl(input.url, "url"),
        secret_token: input.secretToken,
        max_connections: input.maxConnections,
        allowed_updates: input.allowedUpdates,
        drop_pending_updates: input.dropPendingUpdates,
      }),
      context,
      phase: "execute",
    });
    return { success: true };
  },
  async delete_webhook(input, context): Promise<unknown> {
    await telegramRequest<boolean>({
      botToken: context.apiKey,
      method: "deleteWebhook",
      body: compactTelegramBody({
        drop_pending_updates: input.dropPendingUpdates,
      }),
      context,
      phase: "execute",
    });
    return { success: true };
  },
};

export const executors: ProviderExecutors = defineApiKeyProviderExecutors(service, telegramActionHandlers);

export const credentialValidators: CredentialValidators = {
  async apiKey(input, { fetcher, signal }): Promise<CredentialValidationResult> {
    assertValidTelegramBotToken(input.apiKey);
    const profile = await telegramRequest<Record<string, unknown>>({
      botToken: input.apiKey,
      method: "getMe",
      context: { fetcher, signal },
      phase: "validate",
    });
    const normalizedProfile = normalizeTelegramUser(profile);
    return {
      profile: {
        accountId: String(normalizedProfile.id),
        displayName: normalizedProfile.username ? `@${normalizedProfile.username}` : normalizedProfile.firstName,
      },
      grantedScopes: [],
      metadata: {
        botId: normalizedProfile.id,
        username: normalizedProfile.username,
        firstName: normalizedProfile.firstName,
        validationMethod: "getMe",
      },
    };
  },
};

async function telegramRequest<TResult>(input: {
  botToken: string;
  method: string;
  body?: Record<string, unknown>;
  context: Pick<ApiKeyProviderContext, "fetcher" | "signal">;
  phase: "validate" | "execute";
}): Promise<TResult> {
  const controller = new AbortController();
  let timedOut = false;
  const timeoutHandle = setTimeout(() => {
    timedOut = true;
    controller.abort();
  }, telegramDefaultRequestTimeoutMs);
  const abortFromContext = (): void => controller.abort();
  input.context.signal?.addEventListener("abort", abortFromContext, { once: true });

  let response: Response;
  try {
    response = await input.context.fetcher(buildTelegramMethodUrl(input.botToken, input.method), {
      method: input.body == null ? "GET" : "POST",
      headers: input.body == null ? undefined : { "content-type": "application/json" },
      body: input.body == null ? undefined : JSON.stringify(input.body),
      signal: controller.signal,
    });
  } catch (error) {
    if (timedOut && isAbortLikeError(error)) {
      throw new ProviderRequestError(
        502,
        `telegram ${input.method} request timed out after ${Math.ceil(telegramDefaultRequestTimeoutMs / 1000)} seconds`,
      );
    }
    const message = error instanceof Error && error.message.trim() !== "" ? error.message : "request failed";
    throw new ProviderRequestError(502, `telegram ${input.method} request failed: ${message}`);
  } finally {
    clearTimeout(timeoutHandle);
    input.context.signal?.removeEventListener("abort", abortFromContext);
  }

  const payload = (await parseTelegramPayload(response)) as TelegramApiEnvelope<TResult>;
  if (!response.ok || payload.ok !== true) {
    throw buildTelegramError({
      response,
      payload,
      phase: input.phase,
      method: input.method,
    });
  }
  if (payload.result === undefined) {
    throw new ProviderRequestError(502, `telegram ${input.method} response did not include result`);
  }
  return payload.result;
}

function buildTelegramMethodUrl(botToken: string, method: string): string {
  assertValidTelegramBotToken(botToken);
  return `${telegramApiBaseUrl}/bot${botToken}/${method}`;
}

async function parseTelegramPayload(response: Response): Promise<unknown> {
  const contentType = response.headers.get("content-type") ?? "";
  if (contentType.includes("application/json")) {
    return response.json().catch(() => ({}));
  }
  const text = await response.text().catch(() => "");
  return {
    ok: false,
    description: text || `telegram request failed with ${response.status}`,
    error_code: response.status,
  };
}

function buildTelegramError(input: {
  response: Response;
  payload: TelegramApiEnvelope<unknown>;
  phase: "validate" | "execute";
  method: string;
}): ProviderRequestError {
  const status = Number(input.payload.error_code) || input.response.status;
  const description =
    typeof input.payload.description === "string" && input.payload.description.trim() !== ""
      ? input.payload.description
      : `telegram ${input.method} request failed with ${status}`;
  const retryAfter = optionalNumber(input.payload.parameters?.retry_after);

  if (status === 429) {
    const message = retryAfter != null ? `${description} Retry after ${retryAfter} seconds.` : description;
    return new ProviderRequestError(429, message);
  }
  if (input.phase === "validate" && status === 401) {
    return new ProviderRequestError(400, description);
  }
  if (status === 400 || status === 404 || status === 409) {
    return new ProviderRequestError(400, description);
  }
  return new ProviderRequestError(status || 500, description);
}

function normalizeTelegramUser(value: Record<string, unknown>): Record<string, unknown> & {
  id: number;
  firstName: string;
  username?: string;
} {
  return {
    id: Number(value.id),
    isBot: Boolean(value.is_bot),
    firstName: String(value.first_name ?? ""),
    username: optionalString(value.username),
    languageCode: optionalString(value.language_code),
    canJoinGroups: optionalBoolean(value.can_join_groups),
    canReadAllGroupMessages: optionalBoolean(value.can_read_all_group_messages),
    supportsInlineQueries: optionalBoolean(value.supports_inline_queries),
    canConnectToBusiness: optionalBoolean(value.can_connect_to_business),
    hasMainWebApp: optionalBoolean(value.has_main_web_app),
  };
}

function normalizeTelegramChat(value: Record<string, unknown>): Record<string, unknown> & {
  id: number;
  username?: string;
} {
  return {
    id: Number(value.id),
    type: String(value.type ?? ""),
    title: optionalString(value.title),
    username: optionalString(value.username),
    firstName: optionalString(value.first_name),
    lastName: optionalString(value.last_name),
    isForum: optionalBoolean(value.is_forum),
  };
}

function normalizeMessage(value: Record<string, unknown>): Record<string, unknown> {
  return {
    messageId: Number(value.message_id),
    date: Number(value.date),
    chat: normalizeTelegramChat(asRecord(value.chat)),
    from: value.from ? normalizeTelegramUser(asRecord(value.from)) : undefined,
    senderChat: value.sender_chat ? normalizeTelegramChat(asRecord(value.sender_chat)) : undefined,
    text: optionalString(value.text),
    caption: optionalString(value.caption),
    photo: Array.isArray(value.photo) ? value.photo.map((item) => normalizePhotoSize(asRecord(item))) : undefined,
    document: value.document ? normalizeDocument(asRecord(value.document)) : undefined,
    location: value.location ? normalizeLocation(asRecord(value.location)) : undefined,
    poll: value.poll ? normalizePoll(asRecord(value.poll)) : undefined,
    entities: Array.isArray(value.entities) ? value.entities.map(asRecord) : undefined,
    captionEntities: Array.isArray(value.caption_entities) ? value.caption_entities.map(asRecord) : undefined,
    forwardDate: optionalNumber(value.forward_date),
    forwardFrom: value.forward_from ? normalizeTelegramUser(asRecord(value.forward_from)) : undefined,
    forwardFromChat: value.forward_from_chat ? normalizeTelegramChat(asRecord(value.forward_from_chat)) : undefined,
    forwardFromMessageId: optionalNumber(value.forward_from_message_id),
    forwardSignature: optionalString(value.forward_signature),
    forwardSenderName: optionalString(value.forward_sender_name),
    linkPreviewOptions: value.link_preview_options ? asRecord(value.link_preview_options) : undefined,
  };
}

function normalizePhotoSize(value: Record<string, unknown>): Record<string, unknown> {
  return {
    fileId: String(value.file_id ?? ""),
    fileUniqueId: String(value.file_unique_id ?? ""),
    width: Number(value.width),
    height: Number(value.height),
    fileSize: optionalNumber(value.file_size),
  };
}

function normalizeDocument(value: Record<string, unknown>): Record<string, unknown> {
  return {
    fileId: String(value.file_id ?? ""),
    fileUniqueId: String(value.file_unique_id ?? ""),
    fileName: optionalString(value.file_name),
    mimeType: optionalString(value.mime_type),
    fileSize: optionalNumber(value.file_size),
  };
}

function normalizeLocation(value: Record<string, unknown>): Record<string, unknown> {
  return {
    latitude: Number(value.latitude),
    longitude: Number(value.longitude),
    horizontalAccuracy: optionalNumber(value.horizontal_accuracy),
    livePeriod: optionalNumber(value.live_period),
    heading: optionalNumber(value.heading),
    proximityAlertRadius: optionalNumber(value.proximity_alert_radius),
  };
}

function normalizePoll(value: Record<string, unknown>): Record<string, unknown> {
  return {
    id: String(value.id ?? ""),
    question: String(value.question ?? ""),
    options: Array.isArray(value.options)
      ? value.options.map((option) => ({
          text: String(asRecord(option).text ?? ""),
          voterCount: Number(asRecord(option).voter_count ?? 0),
        }))
      : [],
    totalVoterCount: Number(value.total_voter_count ?? 0),
    isClosed: Boolean(value.is_closed),
    isAnonymous: Boolean(value.is_anonymous),
    type: value.type === "quiz" ? "quiz" : "regular",
    allowsMultipleAnswers: Boolean(value.allows_multiple_answers),
    closeDate: optionalNumber(value.close_date),
    openPeriod: optionalNumber(value.open_period),
    explanation: optionalString(value.explanation),
    correctOptionId: optionalNumber(value.correct_option_id),
  };
}

function normalizeUpdate(value: Record<string, unknown>): Record<string, unknown> {
  return {
    updateId: Number(value.update_id),
    message: value.message ? normalizeMessage(asRecord(value.message)) : undefined,
    editedMessage: value.edited_message ? normalizeMessage(asRecord(value.edited_message)) : undefined,
    channelPost: value.channel_post ? normalizeMessage(asRecord(value.channel_post)) : undefined,
    editedChannelPost: value.edited_channel_post ? normalizeMessage(asRecord(value.edited_channel_post)) : undefined,
    callbackQuery: value.callback_query ? asRecord(value.callback_query) : undefined,
  };
}

function extractHistoryEntries(
  update: Record<string, unknown>,
  input: {
    chatId: unknown;
    messageId?: number;
  },
): Array<Record<string, unknown>> {
  const updateId = optionalNumber(update.update_id);
  if (updateId == null) {
    return [];
  }
  const candidates = [
    ["message", update.message],
    ["editedMessage", update.edited_message],
    ["channelPost", update.channel_post],
    ["editedChannelPost", update.edited_channel_post],
  ] as const;

  return candidates.flatMap(([kind, rawMessage]) => {
    const rawRecord = optionalRecord(rawMessage);
    if (!rawRecord) {
      return [];
    }
    const normalizedMessage = normalizeMessage(rawRecord);
    if (!matchesChatLocator(normalizedMessage.chat as { id: number; username?: string }, input.chatId)) {
      return [];
    }
    if (input.messageId != null && Number(normalizedMessage.messageId) < input.messageId) {
      return [];
    }
    return [{ updateId, kind, message: normalizedMessage }];
  });
}

function normalizeWebhookInfo(value: Record<string, unknown>): Record<string, unknown> {
  return {
    url: String(value.url ?? ""),
    hasCustomCertificate: Boolean(value.has_custom_certificate),
    pendingUpdateCount: Number(value.pending_update_count ?? 0),
    ipAddress: optionalString(value.ip_address),
    lastErrorDate: optionalNumber(value.last_error_date),
    lastErrorMessage: optionalString(value.last_error_message),
    lastSynchronizationErrorDate: optionalNumber(value.last_synchronization_error_date),
    maxConnections: optionalNumber(value.max_connections),
    allowedUpdates: Array.isArray(value.allowed_updates)
      ? value.allowed_updates.map((item) => String(item))
      : undefined,
  };
}

function normalizeChatMember(value: Record<string, unknown>): Record<string, unknown> {
  return {
    status: String(value.status ?? ""),
    user: normalizeTelegramUser(asRecord(value.user)),
    customTitle: optionalString(value.custom_title),
    isAnonymous: optionalBoolean(value.is_anonymous),
    untilDate: optionalNumber(value.until_date),
    canBeEdited: optionalBoolean(value.can_be_edited),
    canChangeInfo: optionalBoolean(value.can_change_info),
    canManageChat: optionalBoolean(value.can_manage_chat),
    canInviteUsers: optionalBoolean(value.can_invite_users),
    canPinMessages: optionalBoolean(value.can_pin_messages),
    canEditMessages: optionalBoolean(value.can_edit_messages),
    canPostMessages: optionalBoolean(value.can_post_messages),
    canDeleteMessages: optionalBoolean(value.can_delete_messages),
    canPromoteMembers: optionalBoolean(value.can_promote_members),
    canRestrictMembers: optionalBoolean(value.can_restrict_members),
    canManageVideoChats: optionalBoolean(value.can_manage_video_chats),
    canManageTopics: optionalBoolean(value.can_manage_topics),
  };
}

function matchesChatLocator(chat: { id: number; username?: string }, locator: unknown): boolean {
  if (typeof locator === "number") {
    return chat.id === locator;
  }
  if (typeof locator !== "string") {
    return false;
  }
  if (locator.startsWith("@")) {
    return chat.username?.toLowerCase() === locator.slice(1).toLowerCase();
  }
  return String(chat.id) === locator;
}

function compactTelegramBody(value: Record<string, unknown>): Record<string, unknown> {
  return Object.fromEntries(Object.entries(value).filter(([, child]) => child !== undefined));
}

function asRecord(value: unknown): Record<string, unknown> {
  const record = optionalRecord(value);
  if (!record) {
    throw new ProviderRequestError(502, "telegram returned an unexpected object payload");
  }
  return record;
}

function normalizeJsonLikeInput(value: unknown): Record<string, unknown> | undefined {
  if (typeof value === "string") {
    try {
      return asRecord(JSON.parse(value) as unknown);
    } catch (error) {
      if (error instanceof ProviderRequestError) {
        throw new ProviderRequestError(400, "JSON string input must decode to an object");
      }
      throw new ProviderRequestError(400, "invalid JSON string input");
    }
  }
  if (value && typeof value === "object" && !Array.isArray(value)) {
    return asRecord(value);
  }
  return undefined;
}

function validateTelegramUrlOrFileId(value: unknown, fieldName: string): unknown {
  const text = optionalString(value);
  if (!text || !/^https?:\/\//iu.test(text)) {
    return value;
  }
  return validateTelegramUrl(text, fieldName);
}

function validateTelegramUrl(value: unknown, fieldName: string): string | undefined {
  const text = optionalString(value);
  if (!text) {
    return undefined;
  }
  return assertPublicHttpUrl(text, {
    fieldName,
    createError: (message) => new ProviderRequestError(400, message),
  }).toString();
}

function validateEditMessageTarget(input: Record<string, unknown>): void {
  const hasInlineTarget = input.inlineMessageId != null;
  const hasChatTarget = input.chatId != null || input.messageId != null;
  if (hasInlineTarget && hasChatTarget) {
    throw new ProviderRequestError(400, "edit_message_text accepts either inlineMessageId or chatId/messageId");
  }
  if (!hasInlineTarget && !(input.chatId != null && input.messageId != null)) {
    throw new ProviderRequestError(400, "edit_message_text requires inlineMessageId or chatId with messageId");
  }
}

function assertValidTelegramBotToken(botToken: string): void {
  if (botToken.length === 0 || /[/?#\s]/u.test(botToken)) {
    throw new ProviderRequestError(400, "telegram bot token is malformed");
  }
}

function isAbortLikeError(error: unknown): boolean {
  return error instanceof Error && error.name === "AbortError";
}
