import type { ActionDefinition, JsonSchema } from "../../core/types.ts";

import { s } from "../../core/json-schema.ts";
import { defineProviderAction } from "../../core/provider-definition.ts";

const service = "longbridge";

export const longbridgeOAuthScopes: string[] = ["4", "6", "10", "11"];

export type LongbridgeActionName = "list_securities" | "list_account_cash" | "list_stock_positions";

const nonEmptyString = (description: string): JsonSchema => s.string({ minLength: 1, pattern: "\\S", description });
const rawObjectSchema = s.looseObject("The raw object returned by Longbridge.");

const securitySchema = s.looseObject("A Longbridge tradable security record.", {
  symbol: s.string("The Longbridge security symbol."),
  name_cn: s.string("The Simplified Chinese security name returned by Longbridge."),
  name_hk: s.string("The Traditional Chinese security name returned by Longbridge."),
  name_en: s.string("The English security name returned by Longbridge."),
});

const cashInfoSchema = s.looseObject("A Longbridge per-currency cash detail record.", {
  currency: s.string("The currency for this cash detail record."),
  withdraw_cash: s.string("The withdrawable cash amount returned by Longbridge."),
  available_cash: s.string("The available cash amount returned by Longbridge."),
  frozen_cash: s.string("The frozen cash amount returned by Longbridge."),
  settling_cash: s.string("The settling cash amount returned by Longbridge."),
  redemption_cash: s.string("The redemption cash amount returned by Longbridge."),
});

const accountCashSchema = s.looseObject("A Longbridge account cash balance record.", {
  currency: s.string("The account currency for this balance record."),
  total_cash: s.string("The total cash amount returned by Longbridge."),
  net_assets: s.string("The net asset amount returned by Longbridge."),
  buy_power: s.string("The buying power returned by Longbridge."),
  cash_infos: s.array("Per-currency Longbridge cash detail records.", cashInfoSchema),
});

const stockPositionSchema = s.looseObject("A Longbridge stock position record.", {
  symbol: s.string("The Longbridge security symbol."),
  symbol_name: s.string("The security name returned by Longbridge."),
  currency: s.string("The position currency."),
  quantity: s.string("The position quantity returned by Longbridge."),
  available_quantity: s.string("The available quantity returned by Longbridge."),
  cost_price: s.string("The cost price returned by Longbridge."),
  market: s.string("The market code returned by Longbridge."),
  init_quantity: s.string("The initial quantity returned by Longbridge."),
});

const stockPositionGroupSchema = s.looseObject("A Longbridge stock position group by account channel.", {
  account_channel: s.string("The Longbridge account channel for this group."),
  stock_info: s.array("The stock positions in this account channel.", stockPositionSchema),
});

export const longbridgeActions: ActionDefinition[] = [
  defineProviderAction(service, {
    name: "list_securities",
    description: "List Longbridge tradable securities for a market and category.",
    requiredScopes: longbridgeOAuthScopes,
    providerPermissions: ["openapi"],
    inputSchema: s.object("Input parameters for listing Longbridge tradable securities.", {
      market: s.stringEnum("The Longbridge market code.", ["US", "HK"]),
      category: nonEmptyString(
        "The Longbridge security category filter, such as Overnight for US overnight-tradable securities.",
      ),
    }),
    outputSchema: s.object("The normalized Longbridge securities response.", {
      securities: s.array("The tradable securities returned by Longbridge.", securitySchema),
      raw: rawObjectSchema,
    }),
  }),
  defineProviderAction(service, {
    name: "list_account_cash",
    description: "List Longbridge account cash balances visible to the connected OAuth user.",
    requiredScopes: longbridgeOAuthScopes,
    providerPermissions: ["openapi"],
    inputSchema: s.object(
      "Input parameters for querying Longbridge account cash balances.",
      {
        currency: nonEmptyString("The currency code to send to Longbridge, such as USD or HKD."),
      },
      { optional: ["currency"] },
    ),
    outputSchema: s.object("The normalized Longbridge account cash response.", {
      balances: s.array("The account cash balance records returned by Longbridge.", accountCashSchema),
      raw: rawObjectSchema,
    }),
  }),
  defineProviderAction(service, {
    name: "list_stock_positions",
    description: "List Longbridge stock positions visible to the connected OAuth user.",
    requiredScopes: longbridgeOAuthScopes,
    providerPermissions: ["openapi"],
    inputSchema: s.object(
      "Input parameters for querying Longbridge stock positions.",
      {
        symbols: s.array(
          "The Longbridge security symbols to filter by.",
          nonEmptyString("One Longbridge security symbol, such as AAPL.US or 700.HK."),
          { minItems: 1, maxItems: 100 },
        ),
      },
      { optional: ["symbols"] },
    ),
    outputSchema: s.object("The normalized Longbridge stock positions response.", {
      positionGroups: s.array("The stock position groups returned by Longbridge.", stockPositionGroupSchema),
      raw: rawObjectSchema,
    }),
  }),
];
