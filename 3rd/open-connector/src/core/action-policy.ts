import type { ActionDefinition } from "./types.ts";

export type ActionPolicyDecision =
  | { allowed: true }
  | {
      allowed: false;
      code: "action_not_allowed" | "action_blocked";
      message: string;
    };

export type ActionPolicyConfig = {
  allowedActions?: string[];
  blockedActions?: string[];
};

/**
 * Local action policy used before invoking provider executors.
 */
export class ActionPolicyService {
  private readonly allowed: ActionMatcher[];
  private readonly blocked: ActionMatcher[];

  constructor(config: ActionPolicyConfig = {}) {
    this.allowed = (config.allowedActions ?? []).map(createMatcher);
    this.blocked = (config.blockedActions ?? []).map(createMatcher);
  }

  evaluate(action: ActionDefinition): ActionPolicyDecision {
    if (this.blocked.some((matcher) => matcher(action.id))) {
      return {
        allowed: false,
        code: "action_blocked",
        message: `${action.id} is blocked by the local action policy.`,
      };
    }

    if (this.allowed.length > 0 && !this.allowed.some((matcher) => matcher(action.id))) {
      return {
        allowed: false,
        code: "action_not_allowed",
        message: `${action.id} is not included in the local action allowlist.`,
      };
    }

    return { allowed: true };
  }
}

type ActionMatcher = (actionId: string) => boolean;

export function parseActionPolicyList(value: string | undefined): string[] {
  return (value ?? "")
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean);
}

function createMatcher(pattern: string): ActionMatcher {
  if (pattern.endsWith(".*")) {
    const prefix = pattern.slice(0, -1);
    return (actionId) => actionId.startsWith(prefix);
  }

  return (actionId) => actionId === pattern;
}
