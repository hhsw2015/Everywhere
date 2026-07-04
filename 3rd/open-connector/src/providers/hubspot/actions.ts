import type { ActionDefinition, JsonSchema } from "../../core/types.ts";

import { s } from "../../core/json-schema.ts";
import { defineProviderAction } from "../../core/provider-definition.ts";
import {
  hubspotCompaniesReadScope,
  hubspotCompaniesWriteScope,
  hubspotContactsReadScope,
  hubspotContactsWriteScope,
  hubspotDealsReadScope,
  hubspotDealsWriteScope,
  hubspotSchemaReadScopes,
} from "./scopes.ts";

const service = "hubspot";

interface HubspotActionSource {
  name: HubspotActionName;
  description: string;
  requiredScopes: string[];
  inputSchema: JsonSchema;
  outputSchema: JsonSchema;
}

const unknownRecord = s.record(true, { description: "HubSpot object keyed by provider field name." });
const propertiesInput = s.record(true, { description: "HubSpot properties keyed by property name." });
const propertiesOutput = s.record(s.nullable(s.string()), {
  description: "HubSpot properties keyed by property name.",
});
const propertiesWithHistoryOutput = s.record(true, {
  description: "HubSpot property histories keyed by property name.",
});
const hubspotObjectType = s.stringEnum(["contacts", "companies", "deals"], {
  description: "HubSpot CRM object type.",
});
const requestedProperties = s.array(s.string({ minLength: 1 }), {
  description: "Property names to include in the returned record payload.",
});
const requestedPropertiesWithHistory = s.array(s.string({ minLength: 1 }), {
  description: "Property names to include with their value history.",
});
const requestedAssociations = s.array(s.string({ minLength: 1 }), {
  description: "Associated object types to include in the response.",
});
const recordId = s.string({ minLength: 1, description: "HubSpot record identifier." });
const idProperty = s.string({
  minLength: 1,
  description: "Alternate unique property name that should be used to resolve recordId.",
});

const hubspotRecord = s.object(
  {
    id: s.string({ description: "HubSpot record identifier." }),
    archived: s.boolean({ description: "Whether the record is archived." }),
    createdAt: s.string({ description: "Timestamp when the record was created." }),
    updatedAt: s.string({ description: "Timestamp when the record was last updated." }),
    properties: propertiesOutput,
    propertiesWithHistory: propertiesWithHistoryOutput,
    associations: unknownRecord,
  },
  { required: ["id", "archived", "properties"], description: "HubSpot CRM record." },
);

const hubspotPropertyOption = s.object(
  {
    label: s.string({ description: "Display label for the property option." }),
    value: s.string({ description: "Stored value for the property option." }),
    description: s.nullable(s.string({ description: "Description for the option, when present." })),
    displayOrder: s.nullable(s.integer({ description: "Display order for the option, when present." })),
    hidden: s.boolean({ description: "Whether the option is hidden." }),
  },
  { required: ["label", "value", "description", "displayOrder", "hidden"], description: "HubSpot property option." },
);

const hubspotPropertyDefinition = s.object(
  {
    name: s.string({ description: "Internal HubSpot property name." }),
    label: s.string({ description: "Display label for the property." }),
    type: s.string({ description: "HubSpot property type." }),
    fieldType: s.string({ description: "HubSpot field presentation type." }),
    description: s.nullable(s.string({ description: "Description configured for the property." })),
    groupName: s.nullable(s.string({ description: "HubSpot property group name." })),
    hasUniqueValue: s.boolean({ description: "Whether the property enforces unique values." }),
    hidden: s.boolean({ description: "Whether the property is hidden." }),
    options: s.array(hubspotPropertyOption, { description: "Enumerated options for the property." }),
  },
  {
    required: ["name", "label", "type", "fieldType", "description", "groupName", "hasUniqueValue", "hidden", "options"],
    description: "HubSpot property definition.",
  },
);

const searchInput = input({
  query: s.string({ description: "Full-text query applied to the HubSpot CRM search." }),
  filterGroups: s.array(unknownRecord, { description: "HubSpot filter groups used to narrow the search." }),
  sorts: s.array(s.string({ minLength: 1 }), { description: "HubSpot sort expressions." }),
  properties: requestedProperties,
  propertiesWithHistory: requestedPropertiesWithHistory,
  associations: requestedAssociations,
  limit: s.integer({ minimum: 1, maximum: 200, description: "Maximum number of records to return." }),
  after: s.string({
    minLength: 1,
    description: "Paging cursor returned by a previous search request.",
  }),
});

const getInput = input(
  {
    recordId,
    idProperty,
    properties: requestedProperties,
    propertiesWithHistory: requestedPropertiesWithHistory,
    associations: requestedAssociations,
  },
  ["recordId"],
);

const createInput = input(
  {
    properties: propertiesInput,
    associations: s.array(unknownRecord, { description: "Association definitions to create alongside the record." }),
  },
  ["properties"],
);

const updateInput = input(
  {
    recordId,
    idProperty,
    properties: propertiesInput,
  },
  ["recordId", "properties"],
);

const actions: HubspotActionSource[] = [
  crmAction(
    "search_contacts",
    "Search HubSpot contacts with optional filters, sorting, and selected properties.",
    [hubspotContactsReadScope],
    searchInput,
    searchOutput("Contacts"),
  ),
  crmAction(
    "get_contact",
    "Get a HubSpot contact by record ID or by a custom idProperty value.",
    [hubspotContactsReadScope],
    getInput,
    recordOutput("The requested HubSpot contact."),
  ),
  crmAction(
    "create_contact",
    "Create a HubSpot contact with the provided properties and optional associations.",
    [hubspotContactsWriteScope],
    createInput,
    recordOutput("The created HubSpot contact."),
  ),
  crmAction(
    "update_contact",
    "Update a HubSpot contact by record ID or by a custom idProperty value.",
    [hubspotContactsWriteScope],
    updateInput,
    recordOutput("The updated HubSpot contact."),
  ),
  crmAction(
    "search_companies",
    "Search HubSpot companies with optional filters, sorting, and selected properties.",
    [hubspotCompaniesReadScope],
    searchInput,
    searchOutput("Companies"),
  ),
  crmAction(
    "get_company",
    "Get a HubSpot company by record ID or by a custom idProperty value.",
    [hubspotCompaniesReadScope],
    getInput,
    recordOutput("The requested HubSpot company."),
  ),
  crmAction(
    "create_company",
    "Create a HubSpot company with the provided properties and optional associations.",
    [hubspotCompaniesWriteScope],
    createInput,
    recordOutput("The created HubSpot company."),
  ),
  crmAction(
    "update_company",
    "Update a HubSpot company by record ID or by a custom idProperty value.",
    [hubspotCompaniesWriteScope],
    updateInput,
    recordOutput("The updated HubSpot company."),
  ),
  crmAction(
    "search_deals",
    "Search HubSpot deals with optional filters, sorting, and selected properties.",
    [hubspotDealsReadScope],
    searchInput,
    searchOutput("Deals"),
  ),
  crmAction(
    "get_deal",
    "Get a HubSpot deal by record ID or by a custom idProperty value.",
    [hubspotDealsReadScope],
    getInput,
    recordOutput("The requested HubSpot deal."),
  ),
  crmAction(
    "create_deal",
    "Create a HubSpot deal with the provided properties and optional associations.",
    [hubspotDealsWriteScope],
    createInput,
    recordOutput("The created HubSpot deal."),
  ),
  crmAction(
    "update_deal",
    "Update a HubSpot deal by record ID or by a custom idProperty value.",
    [hubspotDealsWriteScope],
    updateInput,
    recordOutput("The updated HubSpot deal."),
  ),
  crmAction(
    "list_properties",
    "List HubSpot property definitions for contacts, companies, or deals.",
    hubspotSchemaReadScopes,
    input({ objectType: hubspotObjectType }, ["objectType"]),
    output({
      properties: s.array(hubspotPropertyDefinition, {
        description: "Property definitions returned for the requested object type.",
      }),
    }),
  ),
  crmAction(
    "get_property",
    "Get a single HubSpot property definition for contacts, companies, or deals.",
    hubspotSchemaReadScopes,
    input(
      {
        objectType: hubspotObjectType,
        propertyName: s.string({ minLength: 1, description: "HubSpot property name." }),
      },
      ["objectType", "propertyName"],
    ),
    output({
      property: hubspotPropertyDefinition,
    }),
  ),
];

export type HubspotActionName =
  | "search_contacts"
  | "get_contact"
  | "create_contact"
  | "update_contact"
  | "search_companies"
  | "get_company"
  | "create_company"
  | "update_company"
  | "search_deals"
  | "get_deal"
  | "create_deal"
  | "update_deal"
  | "list_properties"
  | "get_property";

export const hubspotActions: ActionDefinition[] = actions.map((source) =>
  defineProviderAction(service, {
    name: source.name,
    description: source.description,
    requiredScopes: source.requiredScopes,
    providerPermissions: source.requiredScopes,
    inputSchema: source.inputSchema,
    outputSchema: source.outputSchema,
  }),
);

function crmAction(
  name: HubspotActionName,
  description: string,
  requiredScopes: string[],
  inputSchema: JsonSchema,
  outputSchema: JsonSchema,
): HubspotActionSource {
  return { name, description, requiredScopes, inputSchema, outputSchema };
}

function input(properties: Record<string, JsonSchema>, required: string[] = []): JsonSchema {
  return s.actionInput(properties, required, "The input payload for this action.");
}

function output(properties: Record<string, JsonSchema>): JsonSchema {
  return s.actionOutput(properties, "HubSpot action output.");
}

function recordOutput(description: string): JsonSchema {
  return output({ record: withDescription(hubspotRecord, description) });
}

function searchOutput(label: string): JsonSchema {
  return output({
    results: s.array(hubspotRecord, { description: `${label} returned by the search request.` }),
    paging: s.object(
      {
        nextAfter: s.string({ description: "Cursor for the next search request when another page is available." }),
      },
      { description: "Paging information for the next page when HubSpot returns it." },
    ),
  });
}

function withDescription(schema: JsonSchema, description: string): JsonSchema {
  return {
    ...schema,
    description,
  };
}
