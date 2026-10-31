using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Service.Interface;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Service
{
    /// <summary>
    /// Contador mensal no banco (tabela google_uso_mensal). Se o banco ou a tabela falharem, cai para um contador
    /// diario em memoria com limite = limite mensal / 31, para o processo sozinho nunca passar do mes.
    /// </summary>
    public sealed class GoogleUsoContador : IGoogleUsoContador
    {
        private const string SqlReservar = @"
INSERT INTO google_uso_mensal (servico, mes, total) VALUES (@servico, @mes, 1)
ON CONFLICT (servico, mes) DO UPDATE SET total = google_uso_mensal.total + 1, atualizado_em = now()
WHERE google_uso_mensal.total < @limite
RETURNING total;";

        private const string SqlLer = "SELECT total FROM google_uso_mensal WHERE servico = @servico AND mes = @mes;";
        private static readonly TimeSpan IntervaloEntreAvisos = TimeSpan.FromMinutes(10);

        private readonly NpgsqlDataSource? _db;
        private readonly TimeProvider _relogio;
        private readonly ILogger<GoogleUsoContador> _logger;
        private readonly ConcurrentDictionary<string, int[]> _memoria = new();
        private long _ultimoAvisoEm;

        public GoogleUsoContador(NpgsqlDataSource? db, TimeProvider relogio, ILogger<GoogleUsoContador> logger)
        {
            _db = db;
            _relogio = relogio;
            _logger = logger;
        }

        internal static string Mes(DateTimeOffset agoraUtc) => agoraUtc.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        internal static string Dia(DateTimeOffset agoraUtc) => agoraUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        public async Task<bool> TentarReservarAsync(string servico, int limiteMensal, CancellationToken cancellationToken = default)
        {
            if (limiteMensal <= 0) return false;

            var agora = _relogio.GetUtcNow();
            if (_db != null)
            {
                try
                {
                    await using var conexao = await _db.OpenConnectionAsync(cancellationToken);
                    var total = await conexao.ExecuteScalarAsync<int?>(new CommandDefinition(
                        SqlReservar, new { servico, mes = Mes(agora), limite = limiteMensal }, cancellationToken: cancellationToken));
                    return total.HasValue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AvisarQueOBancoFalhou(ex);
                }
            }

            return ReservarEmMemoria(servico, Dia(agora), Math.Max(1, limiteMensal / 31));
        }

        public async Task<GoogleUsoResumo> ObterAsync(string servico, int limiteMensal, CancellationToken cancellationToken = default)
        {
            var agora = _relogio.GetUtcNow();
            var mes = Mes(agora);
            if (_db != null)
            {
                try
                {
                    await using var conexao = await _db.OpenConnectionAsync(cancellationToken);
                    var total = await conexao.ExecuteScalarAsync<int?>(new CommandDefinition(
                        SqlLer, new { servico, mes }, cancellationToken: cancellationToken));
                    return new GoogleUsoResumo(servico, mes, total ?? 0, limiteMensal);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AvisarQueOBancoFalhou(ex);
                }
            }

            var usadoHoje = _memoria.TryGetValue(ChaveMemoria(servico, Dia(agora)), out var contagem) ? Volatile.Read(ref contagem[0]) : 0;
            return new GoogleUsoResumo(servico, mes, usadoHoje, limiteMensal);
        }

        private static string ChaveMemoria(string servico, string dia) => servico + "|" + dia;

        private bool ReservarEmMemoria(string servico, string dia, int limiteDiario)
        {
            if (_memoria.Count > 100)
            {
                foreach (var chave in _memoria.Keys)
                {
                    if (!chave.EndsWith("|" + dia, StringComparison.Ordinal)) _memoria.TryRemove(chave, out _);
                }
            }

            var contagem = _memoria.GetOrAdd(ChaveMemoria(servico, dia), _ => new int[1]);
            return Interlocked.Increment(ref contagem[0]) <= limiteDiario;
        }

        private void AvisarQueOBancoFalhou(Exception ex)
        {
            var agora = _relogio.GetUtcNow().UtcTicks;
            var ultimo = Interlocked.Read(ref _ultimoAvisoEm);
            if (ultimo != 0 && agora - ultimo < IntervaloEntreAvisos.Ticks) return;
            Interlocked.Exchange(ref _ultimoAvisoEm, agora);
            _logger.LogWarning(ex, "Contador do Google: banco indisponivel ou tabela google_uso_mensal ausente; usando contador diario em memoria.");
        }
    }
}
