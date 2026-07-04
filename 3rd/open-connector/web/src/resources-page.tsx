import type { ActionDefinition } from "./model";
import type { ReactNode } from "react";

import { useTranslate } from "@embra/i18n/react";
import { BookOpen, ExternalLink, Link2, TerminalSquare } from "lucide-react";

interface ResourcesPageProps {
  actions: ActionDefinition[];
}

interface DocCardProps {
  icon: ReactNode;
  title: string;
  description: string;
  href: string;
}

export function ResourcesPage(props: ResourcesPageProps): ReactNode {
  const t = useTranslate();
  return (
    <div className="docs-grid">
      <DocCard
        icon={<BookOpen size={20} />}
        title={t("resources.apiReference.title")}
        description={t("resources.apiReference.description")}
        href="/docs"
      />
      <DocCard
        icon={<TerminalSquare size={20} />}
        title={t("resources.mcpTools.title")}
        description={t("resources.mcpTools.description", { count: props.actions.length })}
        href="/mcp/tools"
      />
      <DocCard
        icon={<Link2 size={20} />}
        title={t("resources.openapi.title")}
        description={t("resources.openapi.description")}
        href="/openapi.json"
      />
    </div>
  );
}

function DocCard(props: DocCardProps): ReactNode {
  return (
    <a className="doc-card" href={props.href} target="_blank" rel="noreferrer">
      <span className="doc-icon">{props.icon}</span>
      <strong>{props.title}</strong>
      <p>{props.description}</p>
      <ExternalLink size={16} />
    </a>
  );
}
