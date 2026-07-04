import type { ProviderActionDefinition } from "../../core/provider-definition.ts";
import type { JsonSchema } from "../../core/types.ts";

import { s } from "../../core/json-schema.ts";
import { defineProviderAction } from "../../core/provider-definition.ts";

const service = "telegram";

const chatIdSchema: JsonSchema = s.union(
  [
    s.integer("A numeric Telegram chat identifier."),
    s.stringPattern("^-?\\d+$", { description: "A numeric Telegram chat identifier encoded as a string." }),
    s.stringPattern("^@[A-Za-z0-9_]+$", { description: "A Telegram @username for a public chat or channel." }),
  ],
  { description: "The target Telegram chat ID or channel username." },
);
const parseModeSchema = s.stringEnum("The parse mode used for message entities.", ["Markdown", "MarkdownV2", "HTML"]);
const looseObject = (description: string) => s.looseObject(description);
const replyMarkupSchema = s.union(
  [s.nonEmptyString("A JSON-serialized object string."), looseObject("A Telegram reply markup object.")],
  { description: "A Telegram reply markup object or JSON object string." },
);

const telegramUserSchema = looseObject("A Telegram user or bot record.");
const telegramChatSchema = looseObject("A Telegram chat record.");
const telegramMessageSchema = looseObject("A normalized Telegram message record.");
const telegramUpdateSchema = looseObject("A Telegram update payload.");
const telegramChatMemberSchema = looseObject("A Telegram chat member record.");
const successSchema = s.actionOutput(
  {
    success: s.literal(true, { description: "Whether the Telegram Bot API request succeeded." }),
  },
  "A success response payload.",
);

export type TelegramActionName =
  | "get_me"
  | "get_webhook_info"
  | "get_updates"
  | "get_chat_history"
  | "send_message"
  | "edit_message_text"
  | "send_photo"
  | "send_document"
  | "send_poll"
  | "get_chat"
  | "get_chat_member"
  | "get_chat_administrators"
  | "get_chat_members_count"
  | "delete_message"
  | "forward_message"
  | "send_location"
  | "create_chat_invite_link"
  | "answer_callback_query"
  | "set_my_commands"
  | "set_webhook"
  | "delete_webhook";

export const telegramActions: ProviderActionDefinition<TelegramActionName>[] = [
  defineProviderAction(service, {
    name: "get_me",
    description: "Validate the bot token and return the bot profile from Telegram Bot API.",
    inputSchema: s.actionInput({}),
    outputSchema: telegramUserSchema,
  }),
  defineProviderAction(service, {
    name: "get_webhook_info",
    description: "Return the webhook status configured for the bot.",
    inputSchema: s.actionInput({}),
    outputSchema: looseObject("Telegram webhook status information."),
  }),
  defineProviderAction(service, {
    name: "get_updates",
    description: "Poll pending updates for the bot. Use this only when webhook delivery is disabled or for debugging.",
    inputSchema: s.actionInput({
      offset: s.integer("The update ID offset to start polling from."),
      limit: s.integer("The maximum number of updates to return.", { minimum: 1, maximum: 100 }),
      timeout: s.integer("The long-polling timeout in seconds.", { minimum: 0, maximum: 50 }),
      allowedUpdates: s.stringArray("The update types to receive."),
    }),
    outputSchema: s.actionOutput({
      updates: s.array("The updates returned by Telegram.", telegramUpdateSchema),
    }),
  }),
  defineProviderAction(service, {
    name: "get_chat_history",
    description:
      "Get chat history by polling getUpdates, filtering to one chat, and returning message-bearing updates only.",
    inputSchema: s.actionInput(
      {
        chatId: chatIdSchema,
        limit: s.integer("The maximum number of history entries to return.", { minimum: 1, maximum: 100 }),
        offset: s.integer("The Telegram update offset to start reading from."),
        messageId: s.positiveInteger("Only include messages at or after this message ID."),
      },
      ["chatId"],
    ),
    outputSchema: s.actionOutput({
      messages: s.array("The message-bearing updates for the requested chat.", looseObject("A history message.")),
      nextUpdateOffset: s.nullableInteger("The next update offset to continue reading from."),
    }),
  }),
  defineProviderAction(service, {
    name: "send_message",
    description: "Send a text message to a chat, group, supergroup, channel, or forum topic.",
    inputSchema: s.actionInput(
      {
        chatId: chatIdSchema,
        text: s.string("The text of the message to send.", { minLength: 1, maxLength: 4096 }),
        parseMode: parseModeSchema,
        disableNotification: s.boolean("Whether to send the message silently."),
        protectContent: s.boolean("Whether to protect the sent message from forwarding and saving."),
        disableWebPagePreview: s.boolean("Whether to disable link previews in the message."),
        messageThreadId: s.positiveInteger("The forum topic ID for the target message thread."),
        replyToMessageId: s.positiveInteger("The message ID to reply to."),
      },
      ["chatId", "text"],
    ),
    outputSchema: telegramMessageSchema,
  }),
  defineProviderAction(service, {
    name: "edit_message_text",
    description: "Edit the text of a previously sent message or an inline message.",
    inputSchema: s.actionInput(
      {
        chatId: chatIdSchema,
        messageId: s.positiveInteger("The message ID to edit."),
        inlineMessageId: s.nonEmptyString("The inline message ID to edit."),
        text: s.string("The new message text.", { minLength: 1, maxLength: 4096 }),
        parseMode: parseModeSchema,
        disableWebPagePreview: s.boolean("Whether to disable link previews in the edited message."),
      },
      ["text"],
    ),
    outputSchema: s.actionOutput({
      edited: s.literal(true, { description: "Whether the message edit succeeded." }),
      message: s.nullable(telegramMessageSchema),
      inlineMessageId: s.nullableString("The inline message ID when editing an inline message."),
    }),
  }),
  defineProviderAction(service, {
    name: "send_photo",
    description: "Send a photo by public URL or existing Telegram file_id.",
    inputSchema: s.actionInput(
      {
        chatId: chatIdSchema,
        photo: s.nonEmptyString("The photo URL or existing Telegram file_id to send."),
        caption: s.string("The caption for the photo.", { maxLength: 1024 }),
        parseMode: parseModeSchema,
        disableNotification: s.boolean("Whether to send the photo silently."),
        protectContent: s.boolean("Whether to protect the photo from forwarding and saving."),
        messageThreadId: s.positiveInteger("The forum topic ID for the target message thread."),
        replyToMessageId: s.positiveInteger("The message ID to reply to."),
      },
      ["chatId", "photo"],
    ),
    outputSchema: telegramMessageSchema,
  }),
  defineProviderAction(service, {
    name: "send_document",
    description: "Send a document by public URL or existing Telegram file_id.",
    inputSchema: s.actionInput(
      {
        chatId: chatIdSchema,
        document: s.nonEmptyString("The document URL or existing Telegram file_id to send."),
        caption: s.string("The caption for the document.", { maxLength: 1024 }),
        parseMode: parseModeSchema,
        thumbnail: s.nonEmptyString("An optional thumbnail URL or file identifier."),
        replyMarkup: replyMarkupSchema,
        replyToMessageId: s.positiveInteger("The message ID to reply to."),
        disableNotification: s.boolean("Whether to send the document silently."),
        disableContentTypeDetection: s.boolean("Whether to disable server-side content type detection."),
      },
      ["chatId", "document"],
    ),
    outputSchema: telegramMessageSchema,
  }),
  defineProviderAction(service, {
    name: "send_poll",
    description: "Send a native Telegram poll to a chat.",
    inputSchema: s.actionInput(
      {
        chatId: chatIdSchema,
        question: s.string("The question shown at the top of the poll.", { minLength: 1, maxLength: 300 }),
        options: s.array("The answer options available in the poll.", s.string({ minLength: 1, maxLength: 100 }), {
          minItems: 2,
          maxItems: 10,
        }),
        type: s.stringEnum("The type of poll to send.", ["regular", "quiz"]),
        isAnonymous: s.boolean("Whether the poll should be anonymous."),
        allowsMultipleAnswers: s.boolean("Whether users can choose multiple answers."),
        correctOptionId: s.integer("The zero-based index of the correct option for quiz polls.", { minimum: 0 }),
        explanation: s.string("The explanation shown for quiz polls.", { maxLength: 200 }),
        explanationParseMode: parseModeSchema,
        openPeriod: s.integer("The number of seconds the poll should stay open.", { minimum: 5, maximum: 600 }),
        closeDate: s.integer("The Unix timestamp when the poll should close."),
        isClosed: s.boolean("Whether the poll should be sent already closed."),
        disableNotification: s.boolean("Whether to send the poll silently."),
        replyToMessageId: s.positiveInteger("The message ID to reply to."),
        replyMarkup: replyMarkupSchema,
      },
      ["chatId", "question", "options"],
    ),
    outputSchema: telegramMessageSchema,
  }),
  defineProviderAction(service, {
    name: "get_chat",
    description: "Return metadata for a chat the bot can access.",
    inputSchema: s.actionInput({ chatId: chatIdSchema }, ["chatId"]),
    outputSchema: telegramChatSchema,
  }),
  defineProviderAction(service, {
    name: "get_chat_member",
    description: "Return information about one chat member.",
    inputSchema: s.actionInput(
      {
        chatId: chatIdSchema,
        userId: s.integer("The user ID of the chat member to fetch."),
      },
      ["chatId", "userId"],
    ),
    outputSchema: telegramChatMemberSchema,
  }),
  defineProviderAction(service, {
    name: "get_chat_administrators",
    description: "Return the chat administrators visible to the bot.",
    inputSchema: s.actionInput({ chatId: chatIdSchema }, ["chatId"]),
    outputSchema: s.actionOutput({
      administrators: s.array("The administrators visible to the bot in the chat.", telegramChatMemberSchema),
    }),
  }),
  defineProviderAction(service, {
    name: "get_chat_members_count",
    description: "Return the number of members in a chat.",
    inputSchema: s.actionInput({ chatId: chatIdSchema }, ["chatId"]),
    outputSchema: s.actionOutput({
      memberCount: s.integer("The number of members currently in the chat."),
    }),
  }),
  defineProviderAction(service, {
    name: "delete_message",
    description: "Delete a message from a chat.",
    inputSchema: s.actionInput(
      {
        chatId: chatIdSchema,
        messageId: s.positiveInteger("The message ID to delete."),
      },
      ["chatId", "messageId"],
    ),
    outputSchema: successSchema,
  }),
  defineProviderAction(service, {
    name: "forward_message",
    description: "Forward a message from one chat to another.",
    inputSchema: s.actionInput(
      {
        chatId: chatIdSchema,
        fromChatId: chatIdSchema,
        messageId: s.positiveInteger("The source message ID to forward."),
        disableNotification: s.boolean("Whether to forward the message silently."),
      },
      ["chatId", "fromChatId", "messageId"],
    ),
    outputSchema: telegramMessageSchema,
  }),
  defineProviderAction(service, {
    name: "send_location",
    description: "Send a map location to a chat.",
    inputSchema: s.actionInput(
      {
        chatId: chatIdSchema,
        latitude: s.number("The latitude of the location.", { minimum: -90, maximum: 90 }),
        longitude: s.number("The longitude of the location.", { minimum: -180, maximum: 180 }),
        horizontalAccuracy: s.number("The radius of uncertainty for the location, in meters.", {
          minimum: 0,
          maximum: 1500,
        }),
        livePeriod: s.integer("The live location update period in seconds.", { minimum: 60, maximum: 86400 }),
        heading: s.integer("The direction in which the user is moving, in degrees.", { minimum: 1, maximum: 360 }),
        proximityAlertRadius: s.integer("The distance in meters for proximity alerts.", {
          minimum: 1,
          maximum: 100000,
        }),
        disableNotification: s.boolean("Whether to send the location silently."),
        replyToMessageId: s.positiveInteger("The message ID to reply to."),
        replyMarkup: replyMarkupSchema,
      },
      ["chatId", "latitude", "longitude"],
    ),
    outputSchema: telegramMessageSchema,
  }),
  defineProviderAction(service, {
    name: "create_chat_invite_link",
    description: "Export a new primary invite link for a chat.",
    inputSchema: s.actionInput({ chatId: chatIdSchema }, ["chatId"]),
    outputSchema: s.actionOutput({
      inviteLink: s.string("The exported invite link for the chat."),
    }),
  }),
  defineProviderAction(service, {
    name: "answer_callback_query",
    description: "Answer an inline keyboard callback query.",
    inputSchema: s.actionInput(
      {
        callbackQueryId: s.nonEmptyString("The callback query ID to answer."),
        text: s.string("The notification text to show to the user.", { maxLength: 200 }),
        showAlert: s.boolean("Whether to show an alert instead of a notification."),
        url: s.url("The URL to open for the callback query."),
        cacheTime: s.integer("The maximum time in seconds that the result may be cached client-side.", { minimum: 0 }),
      },
      ["callbackQueryId"],
    ),
    outputSchema: successSchema,
  }),
  defineProviderAction(service, {
    name: "set_my_commands",
    description: "Set the bot command list exposed in Telegram clients.",
    inputSchema: s.actionInput(
      {
        commands: s.array(
          "The bot commands to register.",
          s.object({
            command: s.stringPattern("^[a-z0-9_]+$", {
              description: "The command text without the leading slash.",
            }),
            description: s.string("The description shown for the bot command.", { minLength: 1, maxLength: 256 }),
          }),
          { minItems: 1, maxItems: 100 },
        ),
        scope: replyMarkupSchema,
        languageCode: s.string("The language code for localized commands.", { minLength: 2, maxLength: 35 }),
      },
      ["commands"],
    ),
    outputSchema: successSchema,
  }),
  defineProviderAction(service, {
    name: "set_webhook",
    description: "Configure a webhook endpoint for update delivery.",
    inputSchema: s.actionInput(
      {
        url: s.url("The HTTPS webhook URL that Telegram should deliver updates to."),
        secretToken: s.string("The secret token Telegram should include in webhook requests.", {
          minLength: 1,
          maxLength: 256,
        }),
        maxConnections: s.integer("The maximum number of concurrent webhook connections.", {
          minimum: 1,
          maximum: 100,
        }),
        allowedUpdates: s.stringArray("The update types that should be delivered to the webhook."),
        dropPendingUpdates: s.boolean("Whether to drop all pending updates before setting the webhook."),
      },
      ["url"],
    ),
    outputSchema: successSchema,
  }),
  defineProviderAction(service, {
    name: "delete_webhook",
    description: "Delete the configured webhook and optionally drop pending updates.",
    inputSchema: s.actionInput({
      dropPendingUpdates: s.boolean("Whether to drop all pending updates when deleting the webhook."),
    }),
    outputSchema: successSchema,
  }),
];
