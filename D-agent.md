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
```
agent:
  on_event: incident_webhook
  classify: severity, service
  diagnose:
    - tool: RunDiagnostics
      with: queryKey: "topLocksLast5m"
    - tool: RunDiagnostics
      with: queryKey: "slowQueriesWindow"
  propose:
    - mitigation_steps
  require_human_confirm_for:
    - Reprocess
  create_ticket: true
  postmortem_draft: true
```
