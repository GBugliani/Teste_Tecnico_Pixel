/*
 A) Stored Procedure – Fato_PrazosExpedicao (SQL Server)
 Objetivo: carregar dbo.Fato_PrazosExpedicao para consumo de BI.

 Regras:
 - Últimos 30 dias de pedidos expedidos
 - PrazoDias = DATEDIFF(day, DataPedido, DataExpedicao)
 - Se houver múltiplas expedições, considerar a primeira
 - Evitar duplicidades na fato (idempotente)
 - Registrar DataCarga (data/hora atual)

 Critérios de aceite:
 - Upsert seguro (sem duplicar), DataCarga registrada
 - Parâmetro opcional @DataRef DATETIME2 = NULL (assumir SYSUTCDATETIME quando NULL)
*/

/* ---------- SETUP MÍNIMO (ajuste livremente) ---------- */
IF OBJECT_ID('dbo.Expedicao', 'U') IS NOT NULL DROP TABLE dbo.Expedicao;
IF OBJECT_ID('dbo.Pedido',    'U') IS NOT NULL DROP TABLE dbo.Pedido;
IF OBJECT_ID('dbo.Fato_PrazosExpedicao', 'U') IS NOT NULL DROP TABLE dbo.Fato_PrazosExpedicao;

CREATE TABLE dbo.Pedido (
    PedidoId    BIGINT        NOT NULL PRIMARY KEY,
    ClienteId   BIGINT        NOT NULL,
    DataPedido  DATETIME2(0)  NOT NULL,
    Status      VARCHAR(20)   NOT NULL
);

CREATE TABLE dbo.Expedicao (
    ExpedicaoId    BIGINT IDENTITY(1,1) PRIMARY KEY,
    PedidoId       BIGINT        NOT NULL,
    DataExpedicao  DATETIME2(0)  NOT NULL,
    CarrierCode    VARCHAR(20)   NULL,
    CONSTRAINT FK_Expedicao_Pedido FOREIGN KEY (PedidoId) REFERENCES dbo.Pedido(PedidoId)
);

CREATE TABLE dbo.Fato_PrazosExpedicao (
    PedidoId               BIGINT        NOT NULL PRIMARY KEY,
    DataPedido             DATETIME2(0)  NOT NULL,
    DataPrimeiraExpedicao  DATETIME2(0)  NOT NULL,
    PrazoDias              INT           NOT NULL,
    DataCarga              DATETIME2(0)  NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_Expedicao_Pedido_Data ON dbo.Expedicao(PedidoId, DataExpedicao);

/* ---------- DADOS DE EXEMPLO ---------- */
DECLARE @hoje DATETIME2(0) = SYSUTCDATETIME();

INSERT INTO dbo.Pedido (PedidoId, ClienteId, DataPedido, Status) VALUES
 (101, 1, DATEADD(DAY, -31, @hoje), 'Pago'),    -- fora da janela (31 dias)
 (102, 2, DATEADD(DAY, -15, @hoje), 'Pago'),
 (103, 3, DATEADD(DAY, -10, @hoje), 'Pago'),
 (104, 4, DATEADD(DAY,  -5, @hoje), 'Pago'),
 (105, 5, DATEADD(DAY,  -3, @hoje), 'Pago');

INSERT INTO dbo.Expedicao (PedidoId, DataExpedicao, CarrierCode) VALUES
 (101, DATEADD(DAY, -30, @hoje), 'AA'), -- expedição há 30 dias
 (102, DATEADD(DAY, -10, @hoje), 'BB'),
 (102, DATEADD(DAY,  -9, @hoje), 'BB'), -- múltiplas: considerar a primeira
 (103, DATEADD(DAY,  -8, @hoje), 'CC'),
 (104, DATEADD(DAY,  -4, @hoje), 'DD'),
 (105, DATEADD(DAY,  -1, @hoje), 'EE');

GO

/* ---------- TODO: IMPLEMENTE A PROCEDURE ---------- */
CREATE OR ALTER PROCEDURE dbo.prc_CarregarFatoPrazosExpedicao
    @DataRef DATETIME2(0) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    /*
      TODO:
      - Calcular a janela de 30 dias a partir de COALESCE(@DataRef, SYSUTCDATETIME())
      - Selecionar a primeira DataExpedicao por Pedido (MIN)
      - Calcular PrazoDias
      - Upsert idempotente em dbo.Fato_PrazosExpedicao (INSERT/UPDATE sem duplicar)
      - Preencher DataCarga
    */

END
GO

/* DICA DE TESTE
EXEC dbo.prc_CarregarFatoPrazosExpedicao @DataRef = SYSUTCDATETIME();
SELECT * FROM dbo.Fato_PrazosExpedicao ORDER BY PedidoId;
*/
