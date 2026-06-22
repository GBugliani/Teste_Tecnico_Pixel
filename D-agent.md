# D) (Opcional) Agente de Triagem L1 – Desenho rápido

Objetivo: projetar um agente LLM para triagem de incidentes N1 que reduza MTTA/MTTR sem riscos operacionais.

## Entradas/Fontes
- Webhooks (Sentry/AppInsights) com stacktrace e tags
- Logs estruturados do worker (JSON) e métricas (Grafana)
- Query Store/SQL (consultas read-only pré‑aprovadas)

## Ferramentas (exemplos)
- `RunDiagnostics(queryKey, args)` (somente SELECTs aprovados)
- `Reprocess(orderId)` (runbook de baixo risco, idempotente, com confirmação humana)
- `OpenTicket(summary, severity)` (Service Desk) / `CreateIssue(repo, title)` (Azure DevOps)

## Guardrails
- **Sem escrita direta** em produção sem confirmação; trilha de auditoria para todas as ações
- **Redação de PII** em logs e respostas
- Limites de escopo por função (RBAC) e limites de tokens/rate

## Fluxo (YAML esboço)
agent:
  name: l1-incident-triage
  on_event: incident_webhook

  classify:
    inputs: [stacktrace, tags, service, environment]
    outputs:
      severity: [P1, P2, P3, P4]
      category: [transient_sql, timeout, null_ref, auth, unknown]
    fallback: category = unknown, severity = P2
  correlate:
    window_minutes: 10
    key: [fingerprint, service]

  diagnose:
    - tool: RunDiagnostics
      with: { queryKey: "topLocksLast5m", service: "{{service}}" }
    - tool: RunDiagnostics
      with: { queryKey: "slowQueriesWindow", service: "{{service}}" }
    - tool: RunDiagnostics
      with: { queryKey: "recentDeploysLast30m", service: "{{service}}" }

  propose:
    - mitigation_steps

  runbooks:
    - name: Reprocess
      condition: category == "transient_sql" and severity in [P3, P4]
      idempotent: true
      require_human_confirm: true

  guardrails:
    write_actions_require_human: true
    pii_redaction: true
    query_allowlist_only: true
    audit_trail: every_action
    rbac_scope: read_only_default

  create_ticket: true
  postmortem_draft: true

## Implementação de referência — `RunDiagnostics`
import re
import logging
from dataclasses import dataclass

logger = logging.getLogger("l1_agent.audit")

APPROVED_QUERIES = {
    "topLocksLast5m": (
        "SELECT TOP 20 session_id, wait_type, wait_time_ms, blocking_session_id "
        "FROM sys.dm_exec_requests WHERE blocking_session_id <> 0 "
        "AND start_time >= DATEADD(MINUTE, -5, SYSUTCDATETIME())"
    ),
    "slowQueriesWindow": (
        "SELECT TOP 20 query_text, avg_duration_ms, execution_count "
        "FROM dbo.vw_QueryStoreSummary WHERE service = @service "
        "AND last_execution_time >= DATEADD(MINUTE, -15, SYSUTCDATETIME())"
    ),
    "recentDeploysLast30m": (
        "SELECT deploy_id, service, deployed_at, commit_sha "
        "FROM dbo.DeployHistory WHERE service = @service "
        "AND deployed_at >= DATEADD(MINUTE, -30, SYSUTCDATETIME())"
    ),
}

PII_PATTERNS = [
    re.compile(r"\b\d{3}\.\d{3}\.\d{3}-\d{2}\b"),
    re.compile(r"[\w\.-]+@[\w\.-]+\.\w+"),
    re.compile(r"\b(?:\d[ -]*?){13,16}\b"),
]


@dataclass
class DiagnosticResult:
    query_key: str
    rows: list[dict]
    redacted: bool


class UnapprovedQueryError(Exception):
    pass


def redact_pii(rows: list[dict]) -> list[dict]:
    redacted_rows = []
    for row in rows:
        clean_row = {}
        for key, value in row.items():
            text = str(value)
            for pattern in PII_PATTERNS:
                text = pattern.sub("[REDACTED]", text)
            clean_row[key] = text
        redacted_rows.append(clean_row)
    return redacted_rows


def run_diagnostics(query_key: str, params: dict, db_connection, actor: str) -> DiagnosticResult:
    """
    Executa uma query read-only pré-aprovada e devolve o resultado já com
    PII redigida. Nunca aceita SQL fora da allowlist — isso é o guardrail
    central desta função, não um detalhe de implementação.
    """
    if query_key not in APPROVED_QUERIES:

        logger.warning(
            "audit action=run_diagnostics actor=%s query_key=%s result=REJECTED reason=not_in_allowlist",
            actor, query_key,
        )
        raise UnapprovedQueryError(f"Query '{query_key}' não está na allowlist aprovada.")

    sql = APPROVED_QUERIES[query_key]

    logger.info(
        "audit action=run_diagnostics actor=%s query_key=%s result=EXECUTING",
        actor, query_key,
    )

    cursor = db_connection.cursor()
    cursor.execute(sql, params)
    columns = [col[0] for col in cursor.description]
    raw_rows = [dict(zip(columns, row)) for row in cursor.fetchall()]

    safe_rows = redact_pii(raw_rows)

    logger.info(
        "audit action=run_diagnostics actor=%s query_key=%s result=SUCCESS row_count=%d",
        actor, query_key, len(safe_rows),
    )

    return DiagnosticResult(query_key=query_key, rows=safe_rows, redacted=True)