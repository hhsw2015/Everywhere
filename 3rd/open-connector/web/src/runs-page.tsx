import type { RunLog, RunLogPage } from "./model";
import type { ReactNode } from "react";

import { useTranslate } from "@embra/i18n/react";
import { Loader2 } from "lucide-react";
import { useEffect, useState } from "react";
import { apiGet } from "./api";
import { compactJson, formatDate, formatDuration } from "./model";
import { Badge, EmptyState, InlineError } from "./shared-ui";

interface RunsPageProps {
  initialRuns: RunLog[];
  nextCursor?: string;
}

export function RunsPage(props: RunsPageProps): ReactNode {
  const t = useTranslate();
  const [runs, setRuns] = useState(props.initialRuns);
  const [nextCursor, setNextCursor] = useState(props.nextCursor);
  const [loadingMore, setLoadingMore] = useState(false);
  const [runsError, setRunsError] = useState<string | null>(null);

  useEffect(() => {
    setRuns(props.initialRuns);
    setNextCursor(props.nextCursor);
    setRunsError(null);
  }, [props.initialRuns, props.nextCursor]);

  async function loadMoreRuns(): Promise<void> {
    if (!nextCursor || loadingMore) {
      return;
    }

    setLoadingMore(true);
    setRunsError(null);
    try {
      const query = new URLSearchParams({ limit: "50", cursor: nextCursor });
      const page = await apiGet<RunLogPage>(`/api/runs?${query}`);
      setRuns((current) => [...current, ...page.items]);
      setNextCursor(page.nextCursor);
    } catch (caught) {
      setRunsError(caught instanceof Error ? caught.message : t("runs.loadMoreFailed"));
    } finally {
      setLoadingMore(false);
    }
  }

  if (runs.length === 0) {
    return <EmptyState title={t("runs.noRunsTitle")} description={t("runs.noRunsDescription")} />;
  }

  return (
    <>
      <section className="table-panel">
        <table>
          <thead>
            <tr>
              <th>{t("runs.table.action")}</th>
              <th>{t("runs.table.caller")}</th>
              <th>{t("runs.table.status")}</th>
              <th>{t("runs.table.started")}</th>
              <th>{t("runs.table.duration")}</th>
              <th>{t("runs.table.input")}</th>
              <th>{t("runs.table.error")}</th>
            </tr>
          </thead>
          <tbody>
            {runs.map((run) => (
              <tr key={run.id}>
                <td className="mono">{run.actionId}</td>
                <td className="mono">{run.caller}</td>
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
                <td>{run.errorMessage ?? run.errorCode ?? ""}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
      {runsError ? <InlineError message={runsError} /> : null}
      {nextCursor ? (
        <div className="table-footer">
          <button className="secondary-button compact" onClick={() => void loadMoreRuns()} disabled={loadingMore}>
            {loadingMore ? <Loader2 size={14} className="spin" /> : null}
            {t("runs.loadMore")}
          </button>
        </div>
      ) : null}
    </>
  );
}
