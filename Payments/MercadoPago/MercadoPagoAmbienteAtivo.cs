using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Payments.MercadoPago
{
    /// <summary>
    /// Ambiente (sandbox | producao) que o servidor usa agora, escolhido na Gestao e guardado em mp_ambiente_ativo. Leitura sincrona de memoria
    /// (renovada pelo worker); sem linha no banco, vale Payments:MercadoPago:Ambiente. As credenciais dos dois ambientes ficam sempre gravadas.
    /// </summary>
    public interface IMercadoPagoAmbienteAtivo
    {
        string Atual { get; }
        Task RecarregarAsync(CancellationToken ct = default);
        /// <summary>Grava o ambiente e ja passa a valer nesta instancia (as outras pegam no proximo ciclo do worker).</summary>
        Task DefinirAsync(string ambiente, int usuario, CancellationToken ct = default);
    }

    public sealed class MercadoPagoAmbienteAtivo : IMercadoPagoAmbienteAtivo
    {
        private readonly NpgsqlDataSource _db;
        private readonly MercadoPagoOptions _options;
        private volatile string? _gravado;

        public MercadoPagoAmbienteAtivo(NpgsqlDataSource db, IOptions<MercadoPagoOptions> options)
        {
            _db = db;
            _options = options.Value;
        }

        public string Atual => _gravado ?? _options.AmbienteAtivo;

        public async Task RecarregarAsync(CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var amb = await cx.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT ambiente FROM mp_ambiente_ativo WHERE id = 1", cancellationToken: ct));
            _gravado = Ambientes.Valido(amb) ? amb : null;
        }

        public async Task DefinirAsync(string ambiente, int usuario, CancellationToken ct = default)
        {
            if (!Ambientes.Valido(ambiente)) throw PagamentoException.Invalida("Ambiente deve ser sandbox ou producao.", "ambiente");
            await using var cx = await _db.OpenConnectionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO mp_ambiente_ativo (id, ambiente, atualizado_por, updated_at) VALUES (1, @Amb, @Usuario, NOW())
ON CONFLICT (id) DO UPDATE SET ambiente = EXCLUDED.ambiente, atualizado_por = EXCLUDED.atualizado_por, updated_at = NOW()",
                new { Amb = ambiente, Usuario = usuario }, cancellationToken: ct));
            _gravado = ambiente;
        }
    }

    /// <summary>Carrega o ambiente ativo antes de o servidor atender e o renova a cada 10 s (varias instancias enxergam a troca).</summary>
    public sealed class MercadoPagoAmbienteAtivoWorker : BackgroundService
    {
        private static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(10);
        private readonly IMercadoPagoAmbienteAtivo _ambiente;
        private readonly ILogger<MercadoPagoAmbienteAtivoWorker> _logger;

        public MercadoPagoAmbienteAtivoWorker(IMercadoPagoAmbienteAtivo ambiente, ILogger<MercadoPagoAmbienteAtivoWorker> logger)
        {
            _ambiente = ambiente;
            _logger = logger;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            await TentarAsync(cancellationToken);
            await base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await Task.Delay(Intervalo, stoppingToken); } catch (OperationCanceledException) { break; }
                await TentarAsync(stoppingToken);
            }
        }

        // Tabela ainda nao criada (migration pendente) ou banco fora: mantem o ultimo valor (ou a configuracao).
        private async Task TentarAsync(CancellationToken ct)
        {
            try { await _ambiente.RecarregarAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { _logger.LogWarning("[mp.ambiente] ev=recarga_falhou erro={Erro}", ex.GetType().Name); }
        }
    }
}
