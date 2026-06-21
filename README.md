# Teste Técnico – Engenheiro(a) de Suporte Aplicacional (L3) – .NET/SQL

**Tempo estimado:** até **45 minutos** (núcleo A/B/C)  
**Prazo de envio:** **18/11 - 10hs**  
**Entregáveis:** `A-sql.sql`, `B-worker.cs`
**(Opcional)** extra: `D-agent.md`

**Transparência sobre IA/LLM** (permitido): use o modelo `IA.md` para indicar o que foi gerado por IA e o que é de sua autoria.

---

## Contexto
Você atuará como **L3 (nível de código)** ajudando times internos (Atendimento, Marketing, Operações) a manter o e‑commerce estável. O foco é **resolver incidentes e solicitações com leitura de código, logs e SQL**, sempre com **idempotência**, **observabilidade** e **segurança de dados**.

---

## A) `A-sql.sql` — Stored Procedure (SQL Server)
**Tarefa:** criar a procedure `dbo.prc_CarregarFatoPrazosExpedicao` que carrega a fato `dbo.Fato_PrazosExpedicao` (consumida pelo BI).

**Regras de negócio:**
1. Considerar **últimos 30 dias** de pedidos **expedidos**.  
2. `PrazoDias = DATEDIFF(day, DataPedido, DataExpedicao)`  
3. Se houver **múltiplas expedições**, considerar **a primeira** (`MIN(DataExpedicao)`).  
4. **Evitar duplicidades** na fato (idempotente em reexecuções).  
5. Registrar `DataCarga` (use `SYSUTCDATETIME()` ou similar).

**Critérios de aceite:**
- Usa `MIN(DataExpedicao)` filtrando a janela de **30 dias**.  
- **Upsert** seguro (ex.: `MERGE`, `INSERT ... WHERE NOT EXISTS` ou `UPDATE/INSERT` em transação) que **não duplica** registros.  
- `DataCarga` registrada.  
- Parâmetro opcional `@DataRef DATETIME2 = NULL`.

O arquivo `A-sql.sql` já inclui **setup mínimo** (DDL + dados de exemplo) e um **stub** da procedure para você completar.

---

## B) `B-worker.cs` — Refatoração de Worker (.NET)
**Cenário:** um worker consome mensagens (ex.: de uma fila) e grava no SQL Server. O código base possui problemas de **concorrência**, **cancelamento** e **SQL não parametrizado**.

**O que esperamos na refatoração:**  
- **Concorrência controlada** (sem `Parallel.ForEach` com `async`). Use `Channel<T>`, `SemaphoreSlim` ou *bounded concurrency* com `Task.WhenAll`.  
- **CancellationToken** respeitado e **shutdown limpo**.  
- **`OpenAsync` e SQL parametrizado** (ADO.NET ou Dapper).  
- **Retry/backoff** **apenas** para **erros transitórios** (documente quais).  
- **Idempotência** por chave de negócio (ex.: `MessageId` ou `OrderId` com `UNIQUE` + lógica de upsert).  
- **Logs estruturados** (inclua `orderId`, tentativas, duração).

No topo do arquivo, **explique em 2–4 linhas** como você evita **saturar o SQL** e garante **desligamento limpo**.

O diretório contém:
- `B-worker.cs` → código base a ser refatorado.  
- `B-setup.sql` → tabela de destino.  
- `messages.jsonl` → dados simulando a fila (inclui uma mensagem **duplicada**).

---

## (Opcional) C) `D-agent.md` — Automação com IA/Agente (10–15 min)
Descreva (em 10–15 linhas ou YAML + 1 função) um **agente L1** que:
- Recebe **webhooks** (Sentry/App Insights), **classifica** severidade e correlaciona erros.  
- Executa **runbooks de baixo risco** (ex.: reprocessar mensagem, destravar projeto) com **idempotência**.  
- Consulta SQL **read‑only** via *queries* pré‑aprovadas.  
- Abre/atualiza tíquetes e issues com **trilha de auditoria**.  
- **Guardrails:** confirmação humana para escrita, **redação de PII** e escopo limitado por função/ferramenta.

---

## Como entregar
Crie um `.zip` com `A-sql.sql`, `B-worker.cs` **(e, se desejar, `D-agent.md`)**.  
Envie por e‑mail para **quem enivou o teste**
(Opcional) envie também o link de um repositório Git.

Boa sorte!
