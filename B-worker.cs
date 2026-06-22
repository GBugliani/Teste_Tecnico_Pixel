/*
 B) Worker .NET – Refatoração
 Objetivo: ler mensagens (ex.: fila) e gravar no SQL Server com:
 - Concorrência controlada (sem Parallel.ForEach + async)
 - CancellationToken e shutdown limpo
 - SQL parametrizado + OpenAsync
 - Retry/backoff somente para erros transitórios
 - Idempotência por chave (ex.: MessageId ou OrderId) e logs estruturados

 Explique em 2–4 linhas (abaixo) como você evita saturar o SQL e garante desligamento limpo.

Uso Channel<T> (bounded, FullMode = Wait) como fila intermediária entre o produtor e um número fixo de consumidores (MaxConcurrency), o que limita o grau 
de paralelismo nativamente pois o produtor bloqueia em WriteAsync quando o canal está cheio, sem precisar de SemaphoreSlim. O CancellationToken é propagado a
toda chamada assíncrona (OpenAsync(ct), ExecuteNonQueryAsync(ct), WaitToReadAsync(ct)) e o IHostedService/BackgroundService trata OperationCanceledException 
na finalização como sinal de shutdown, não como erro
*/

using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;

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
    private const int MaxConcurrency = 4;
    private const int ChannelCapacity = 128;
    private const int MaxRetryAttempts = 3;

    public Worker(ILogger<Worker> logger, IMessageQueue queue, IConfiguration cfg)
    {
        _logger = logger;
        _queue = queue;
        _connString = cfg.GetConnectionString("Sql");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = Channel.CreateBounded<Dictionary<string, object>>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true
        });

        var producer = ProduceAsync(channel.Writer, stoppingToken);
        var consumers = Enumerable.Range(0, MaxConcurrency)
            .Select(workerId => ConsumeAsync(channel.Reader, workerId, stoppingToken))
            .ToArray();

        try
        {
            await Task.WhenAll(consumers.Append(producer));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Worker shutdown requested.");
        }
    }

    private async Task ProduceAsync(ChannelWriter<Dictionary<string, object>> writer, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in _queue.ReadAsync(stoppingToken))
            {
                await writer.WriteAsync(message, stoppingToken);
            }
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
            throw;
        }
    }

    private async Task ConsumeAsync(ChannelReader<Dictionary<string, object>> reader, int workerId, CancellationToken stoppingToken)
    {
        while (await reader.WaitToReadAsync(stoppingToken))
        {
            while (reader.TryRead(out var message))
            {
                await ProcessMessageAsync(message, workerId, stoppingToken);
            }
        }
    }

    private async Task ProcessMessageAsync(Dictionary<string, object> message, int workerId, CancellationToken stoppingToken)
    {
        long orderId;
        string messageId;
        string eventType;
        DateTime occurredAt;
        string payload;

        try
        {
            orderId = GetLong(message, "orderId");
            messageId = GetString(message, "messageId");
            eventType = GetString(message, "eventType", "UNKNOWN");
            occurredAt = GetDateTimeUtc(message, "occurredAt", DateTime.UtcNow);
            payload = JsonSerializer.Serialize(message);
        }
        catch (Exception ex)
        {

            _logger.LogError(
                ex,
                "Malformed message discarded WorkerId={WorkerId} RawPayload={RawPayload}",
                workerId,
                JsonSerializer.Serialize(message));
            return;
        }

        var attempt = 0;
        var startedAt = Stopwatch.StartNew();

        while (true)
        {
            attempt++;

            try
            {
                await SaveMessageAsync(orderId, messageId, eventType, occurredAt, payload, stoppingToken);
                startedAt.Stop();

                _logger.LogInformation(
                    "Saved event OrderId={OrderId} MessageId={MessageId} EventType={EventType} WorkerId={WorkerId} Attempt={Attempt} DurationMs={DurationMs}",
                    orderId,
                    messageId,
                    eventType,
                    workerId,
                    attempt,
                    startedAt.ElapsedMilliseconds);

                return;
            }
            catch (SqlException ex) when (IsDuplicateKey(ex))
            {
                startedAt.Stop();
                _logger.LogInformation(
                    "Duplicate message ignored OrderId={OrderId} MessageId={MessageId} WorkerId={WorkerId} DurationMs={DurationMs}",
                    orderId,
                    messageId,
                    workerId,
                    startedAt.ElapsedMilliseconds);
                return;
            }
            catch (Exception ex) when (IsTransientSqlError(ex) && attempt < MaxRetryAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1));
                _logger.LogWarning(
                    ex,
                    "Transient SQL error for OrderId={OrderId} MessageId={MessageId} WorkerId={WorkerId} Attempt={Attempt}. Retrying in {DelayMs}ms.",
                    orderId,
                    messageId,
                    workerId,
                    attempt,
                    delay.TotalMilliseconds);

                await Task.Delay(delay, stoppingToken);
            }
            catch (Exception ex)
            {
                startedAt.Stop();
                _logger.LogError(
                    ex,
                    "Failed to save event OrderId={OrderId} MessageId={MessageId} WorkerId={WorkerId} Attempt={Attempt} DurationMs={DurationMs}",
                    orderId,
                    messageId,
                    workerId,
                    attempt,
                    startedAt.ElapsedMilliseconds);
                return;
            }
        }
    }

    private async Task SaveMessageAsync(long orderId, string messageId, string eventType, DateTime occurredAt, string payload, CancellationToken stoppingToken)
    {
        using var connection = new SqlConnection(_connString);
        await connection.OpenAsync(stoppingToken);

        using var command = connection.CreateCommand();
        command.CommandType = CommandType.Text;
        command.CommandText = @"
        INSERT INTO dbo.EventosPedido (MessageId, OrderId, EventType, OccurredAt, Payload)
        SELECT @MessageId, @OrderId, @EventType, @OccurredAt, @Payload
        WHERE NOT EXISTS (
            SELECT 1
            FROM dbo.EventosPedido WITH (UPDLOCK, HOLDLOCK)
            WHERE MessageId = @MessageId
        );";

        command.Parameters.Add(new SqlParameter("@MessageId", SqlDbType.VarChar, 50) { Value = messageId });
        command.Parameters.Add(new SqlParameter("@OrderId", SqlDbType.BigInt) { Value = orderId });
        command.Parameters.Add(new SqlParameter("@EventType", SqlDbType.VarChar, 50) { Value = eventType });
        command.Parameters.Add(new SqlParameter("@OccurredAt", SqlDbType.DateTime2) { Value = occurredAt });
        command.Parameters.Add(new SqlParameter("@Payload", SqlDbType.NVarChar, -1) { Value = payload });

        await command.ExecuteNonQueryAsync(stoppingToken);
    }

    private static bool IsDuplicateKey(SqlException ex)
    {
        foreach (SqlError error in ex.Errors)
        {
            if (error.Number == 2601 || error.Number == 2627)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTransientSqlError(Exception ex)
    {
        if (ex is not SqlException sqlException)
        {
            return false;
        }

        foreach (SqlError error in sqlException.Errors)
        {
            if (error.Number is -2 or 1205 or 40501 or 40613 or 40197 or 10928 or 10929 or 49918 or 49919 or 49920)
            {
                return true;
            }
        }

        return false;
    }

    private static string GetString(Dictionary<string, object> message, string key, string defaultValue = "")
    {
        if (!message.TryGetValue(key, out var value) || value is null)
        {
            return defaultValue;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? defaultValue;
    }

    private static long GetLong(Dictionary<string, object> message, string key)
    {
        if (!message.TryGetValue(key, out var value) || value is null)
        {
            throw new InvalidOperationException($"Required key '{key}' was not found.");
        }

        return value switch
        {
            long longValue => longValue,
            int intValue => intValue,
            string stringValue when long.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture)
        };
    }

    private static DateTime GetDateTimeUtc(Dictionary<string, object> message, string key, DateTime defaultValue)
    {
        if (!message.TryGetValue(key, out var value) || value is null)
        {
            return DateTime.SpecifyKind(defaultValue, DateTimeKind.Utc);
        }

        return value switch
        {
            DateTime dateTime => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
            string stringValue when DateTime.TryParse(stringValue, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) => DateTime.SpecifyKind(parsed, DateTimeKind.Utc),
            _ => DateTime.SpecifyKind(Convert.ToDateTime(value, CultureInfo.InvariantCulture), DateTimeKind.Utc)
        };
    }

}
