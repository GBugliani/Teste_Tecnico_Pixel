/*
 B) Worker .NET – Refatoração
 Objetivo: ler mensagens (ex.: fila) e gravar no SQL Server com:
 - Concorrência controlada (sem Parallel.ForEach + async)
 - CancellationToken e shutdown limpo
 - SQL parametrizado + OpenAsync
 - Retry/backoff somente para erros transitórios
 - Idempotência por chave (ex.: MessageId ou OrderId) e logs estruturados

 Explique em 2–4 linhas (abaixo) como você evita saturar o SQL e garante desligamento limpo.
 [Sua explicação aqui]
*/

using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public interface IMessageQueue
{
    // Simule como quiser; a pasta contém messages.jsonl
    IAsyncEnumerable<Dictionary<string, object>> ReadAsync(CancellationToken ct);
}

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IMessageQueue _queue;
    private readonly string _connString;

    public Worker(ILogger<Worker> logger, IMessageQueue queue, IConfiguration cfg)
    {
        _logger = logger;
        _queue = queue;
        _connString = cfg.GetConnectionString("Sql");
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PROPOSITALMENTE PROBLEMÁTICO PARA VOCÊ REFAZER:
        // - Usa sync I/O
        // - SQL não parametrizado
        // - Não respeita corretamente o CancellationToken
        // - Usa paralelismo inadequado ao cenário

        var messages = _queue.ReadAsync(stoppingToken);

        // Não faça isso: exemplo ruim para servir de base da refatoração
        Parallel.ForEach(AsyncToBlocking(messages), msg =>
        {
            var orderId = msg["orderId"].ToString();
            var status  = msg.ContainsKey("status") ? msg["status"]?.ToString() : "UNKNOWN";

            using (var con = new SqlConnection(_connString))
            {
                con.Open(); // Sync
                var sql = $"INSERT INTO dbo.EventosPedido(OrderId, EventType, MessageId, OccurredAt, Payload) " +
                          $"VALUES({orderId}, '{status}', '{msg["messageId"]}', GETUTCDATE(), '{System.Text.Json.JsonSerializer.Serialize(msg)}')";
                using (var cmd = new SqlCommand(sql, con))
                {
                    cmd.ExecuteNonQuery(); // Sync e sem parâmetros
                }
            }

            _logger.LogInformation("Saved {orderId}", orderId);
        });

        return Task.CompletedTask;
    }

    // Helper para converter IAsyncEnumerable em consumo bloqueante (apenas para demonstrar o antipadrão)
    private static IEnumerable<T> AsyncToBlocking<T>(IAsyncEnumerable<T> source)
    {
        var enumerator = source.GetAsyncEnumerator();
        while (enumerator.MoveNextAsync().AsTask().Result)
            yield return enumerator.Current;
    }
}
