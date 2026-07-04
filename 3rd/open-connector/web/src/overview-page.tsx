import type { AppData } from "./model";
import type { ReactNode } from "react";

import { useTranslate } from "@embra/i18n/react";
import { Activity, AppWindow, FileText, KeyRound, PlugZap, RefreshCw, TerminalSquare } from "lucide-react";
import { Link } from "react-router";
import { compactJson, createOverviewSummary, formatDate, formatDuration } from "./model";
import { Badge, EmptyState, InfoBlock, Metric } from "./shared-ui";

interface OverviewPageProps {
  data: AppData;
  onRefresh(): void;
}

export function OverviewPage(props: OverviewPageProps): ReactNode {
  const t = useTranslate();
  const summary = createOverviewSummary(props.data);
  const recentRuns = props.data.runs.slice(0, 6);

  return (
    <div className="page-stack">
      <section className="runtime-strip">
        <div>
          <strong>{t("overview.runtimeReady")}</strong>
          <span>{t("overview.connectedProviders", { count: summary.connectedCount })}</span>
        </div>
        <button className="secondary-button compact" onClick={props.onRefresh}>
          <RefreshCw size={15} />
          {t("common.refresh")}
        </button>
      </section>

      <section className="metrics">
        <Metric label={t("overview.metrics.providers")} value={summary.providerCount} />
        <Metric label={t("overview.metrics.actions")} value={summary.actionCount} />
        <Metric label={t("overview.metrics.connected")} value={summary.connectedCount} />
        <Metric label={t("overview.metrics.tokens")} value={summary.activeTokenCount} />
      </section>

      <section className="content-grid">
        <div className="detail-panel">
          <div className="section-heading-row">
            <h2>{t("overview.connectionHealth")}</h2>
            <Link className="secondary-link" to="/providers">
              <PlugZap size={15} />
              {t("nav.providers")}
            </Link>
          </div>
          <div className="section-grid">
            <InfoBlock
              icon={<AppWindow size={18} />}
              label={t("overview.metrics.providers")}
              value={t("overview.catalogValue", { count: summary.providerCount })}
            />
            <InfoBlock
              icon={<PlugZap size={18} />}
              label={t("overview.metrics.connected")}
              value={t("overview.connectedValue", { count: summary.connectedCount })}
            />
            <InfoBlock
              icon={<TerminalSquare size={18} />}
              label={t("overview.executable")}
              value={t("overview.executableValue", {
                count: props.data.providers
                  .flatMap((provider) => provider.actions)
                  .filter((action) => action.execution.locallyExecutable).length,
              })}
            />
          </div>
        </div>

        <div className="detail-panel">
          <div className="section-heading-row">
            <h2>{t("overview.commonEntries")}</h2>
          </div>
          <div className="quick-link-grid">
            <Link className="quick-link" to="/providers">
              <PlugZap size={16} />
              {t("overview.connectProvider")}
            </Link>
            <Link className="quick-link" to="/actions">
              <TerminalSquare size={16} />
              {t("overview.searchActions")}
            </Link>
            <Link className="quick-link" to="/access">
              <KeyRound size={16} />
              {t("overview.createToken")}
            </Link>
            <Link className="quick-link" to="/resources">
              <FileText size={16} />
              {t("overview.openDocs")}
            </Link>
          </div>
        </div>
      </section>

      <section className="content-grid">
        <div className="table-panel">
          <div className="table-panel-heading">
            <h2>{t("overview.recentFailures")}</h2>
            <Badge tone={summary.failedRuns.length ? "error" : "success"}>{summary.failedRuns.length}</Badge>
          </div>
          {summary.failedRuns.length === 0 ? (
            <EmptyState title={t("overview.noFailedRunsTitle")} description={t("overview.noFailedRunsDescription")} />
          ) : (
            <RunSummaryTable runs={summary.failedRuns} />
          )}
        </div>

        <div className="table-panel">
          <div className="table-panel-heading">
            <h2>{t("overview.recentRuns")}</h2>
            <Link className="secondary-link" to="/runs">
              <Activity size={15} />
              {t("nav.runs")}
            </Link>
          </div>
          {recentRuns.length === 0 ? (
            <EmptyState title={t("overview.noRunsTitle")} description={t("overview.noRunsDescription")} />
          ) : (
            <RunSummaryTable runs={recentRuns} />
          )}
        </div>
      </section>
    </div>
  );
}

function RunSummaryTable(props: { runs: AppData["runs"] }): ReactNode {
  const t = useTranslate();
  return (
    <table>
      <thead>
        <tr>
          <th>{t("overview.table.action")}</th>
          <th>{t("overview.table.status")}</th>
          <th>{t("overview.table.started")}</th>
          <th>{t("overview.table.duration")}</th>
          <th>{t("overview.table.input")}</th>
        </tr>
      </thead>
      <tbody>
        {props.runs.map((run) => (
          <tr key={run.id}>
            <td className="mono">{run.actionId}</td>
            <td>
              {run.ok ? (
                <Badge tone="success">{t("common.success")}</Badge>
              ) : (
                <Badge tone="error">{t("common.failed")}</Badge>
              )}
            </td>
            <td>{formatDate(run.startedAt)}</td>
            <td>{formatDuration(run)}</td>
            <td className="mono">{compactJson(run.inputSummary)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
