using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.MercadoPago;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Payments.Chat
{
    /// <summary>
    /// Recuperacao e efeitos fora da transacao: tarefas depois do pagamento (avisar o cliente, avancar o pedido, cancelar a reemitida),
    /// cobrancas presas em 'criando' (MP sem resposta) e aprovacoes esquecidas. Cada passo e idempotente; pode rodar em varias instancias
    /// (o worker usa advisory lock) e de novo depois de uma queda.
    /// </summary>
    public sealed class CobrancaChatManutencao
    {
        public const int MaxTentativas = 6;
        public static readonly TimeSpan RetomarCriandoDepois = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan DesistirCriandoDepois = TimeSpan.FromMinutes(30);
        public static readonly TimeSpan AprovacaoValidaPor = TimeSpan.FromHours(24);

        private readonly NpgsqlDataSource _db;
        private readonly CobrancaChatService _service;
        private readonly ICobrancaChatMensageiro _mensageiro;
        private readonly ICobrancaPedidoAvancador _avancador;
        private readonly ILogger<CobrancaChatManutencao> _logger;

        public CobrancaChatManutencao(NpgsqlDataSource db, CobrancaChatService service, ICobrancaChatMensageiro mensageiro, ICobrancaPedidoAvancador avancador,
            ILogger<CobrancaChatManutencao> logger)
        {
            _db = db;
            _service = service;
            _mensageiro = mensageiro;
            _avancador = avancador;
            _logger = logger;
        }

        public async Task RodarAsync(CancellationToken ct)
        {
            await TarefasAsync(ct);
            await RetomarCriandoAsync(ct);
            await ExpirarAprovacoesAsync(ct);
        }

        // ---- tarefas ---------------------------------------------------------------------------------------------------------

        private sealed class Tarefa
        {
            public long Id { get; set; }
            public Guid IdCobranca { get; set; }
            public Guid IdEstabelecimento { get; set; }
            public string Tipo { get; set; } = string.Empty;
            public int Tentativas { get; set; }
        }

        public async Task TarefasAsync(CancellationToken ct)
        {
            List<Tarefa> lote;
            await using (var cx = await _db.OpenConnectionAsync(ct))
            {
                // Reserva: empurra proxima_em para frente; se a instancia cair, a tarefa volta sozinha depois de 5 min.
                lote = (await cx.QueryAsync<Tarefa>(new CommandDefinition(@"
UPDATE cobranca_chat_tarefa t SET proxima_em = NOW() + INTERVAL '5 minutes', tentativas = t.tentativas + 1
 WHERE t.id IN (SELECT id FROM cobranca_chat_tarefa WHERE status = 'pendente' AND proxima_em <= NOW() ORDER BY id LIMIT 20 FOR UPDATE SKIP LOCKED)
RETURNING t.id, t.id_cobranca, t.id_estabelecimento, t.tipo, t.tentativas", cancellationToken: ct))).ToList();
            }

            foreach (var t in lote)
            {
                string status, resultado;
                string? erro = null;
                try
                {
                    (status, resultado) = await ExecutarAsync(t, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    erro = ex is PagamentoException p ? p.Codigo : ex.GetType().Name;
                    status = t.Tentativas >= MaxTentativas ? "falhou" : "pendente";
                    resultado = "erro";
                    _logger.LogWarning(ex, "[cobranca.chat] ev=tarefa_erro tarefa={Tarefa} tipo={Tipo} cobranca={Cobranca} tentativa={N}", t.Id, t.Tipo, t.IdCobranca, t.Tentativas);
                }

                await using var cx = await _db.OpenConnectionAsync(ct);
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE cobranca_chat_tarefa SET status = @S, resultado = @R, ultimo_erro = @E,
       proxima_em = CASE WHEN @S = 'pendente' THEN NOW() + (30 * power(2, LEAST(tentativas, 8)) * INTERVAL '1 second') ELSE proxima_em END,
       concluida_em = CASE WHEN @S = 'pendente' THEN NULL ELSE NOW() END
 WHERE id = @Id", new { t.Id, S = status, R = resultado, E = erro }, cancellationToken: ct));
            }
        }

        private async Task<(string Status, string Resultado)> ExecutarAsync(Tarefa t, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var c = await CobrancaChatRepositorio.ObterAsync(cx, null, t.IdCobranca, t.IdEstabelecimento, false, ct);
            if (c == null) return ("ignorada", "cobranca_inexistente");

            switch (t.Tipo)
            {
                case TiposTarefa.ConfirmarCliente:
                {
                    if (c.Status is not (StatusCobranca.Pago or StatusCobranca.ReembolsoSolicitado)) return ("ignorada", "nao_pago");
                    var config = await CobrancaChatRepositorio.ConfigAsync(cx, c.IdEstabelecimento, ct);
                    if (!config.ConfirmarPagamentoCliente) return ("ignorada", "desligado_na_loja");
                    var conversa = await CobrancaChatRepositorio.ConversaAsync(cx, null, c.ConversaId, c.IdEstabelecimento, ct);
                    if (conversa == null || !CobrancaChatRegras.JanelaAberta(conversa.Janela24hFim, conversa.Estado, DateTime.UtcNow))
                        return ("ignorada", "janela_fechada");
                    await _mensageiro.EnviarTextoAsync(conversa.Id, c.IdEstabelecimento, CobrancaChatRegras.TextoConfirmacao(c.Descricao, c.Valor), "sistema", ct);
                    return ("feita", "cliente_avisado");
                }
                case TiposTarefa.AvancarPedido:
                {
                    if (c.PedidoId == null) return ("ignorada", "sem_pedido");
                    var r = await _avancador.AvancarAsync(c.IdEstabelecimento, c.PedidoId.Value, c.AutorUsuarioId, ct);
                    return (r == "confirmado" ? "feita" : "ignorada", r);
                }
                case TiposTarefa.CancelarSucessora:
                {
                    var abertas = (await cx.QueryAsync<Guid>(new CommandDefinition(@"
WITH RECURSIVE cadeia AS (
    SELECT id, status FROM cobranca_chat WHERE reemitida_de = @Id
    UNION ALL
    SELECT c.id, c.status FROM cobranca_chat c JOIN cadeia ON c.reemitida_de = cadeia.id)
SELECT id FROM cadeia WHERE status IN ('aguardando_aprovacao', 'criando', 'aguardando', 'enviado')", new { c.Id }, cancellationToken: ct))).ToList();
                    foreach (var s in abertas)
                    {
                        try
                        {
                            await _service.CancelarInternoAsync(c.IdEstabelecimento, s, "anterior_paga", null, ct);
                        }
                        catch (PagamentoException ex) when (ex.Codigo == PagamentoCodigos.JaPago)
                        {
                            // As duas foram pagas: fica registrado para o gerente reembolsar uma delas (com aprovacao).
                            await PagamentoAuditoria.RegistrarAsync(cx, null, c.IdEstabelecimento, "cobranca_chat", s.ToString(), "pagamento_duplicado", "sistema", null,
                                new { anterior = c.Id, c.PedidoId }, ct);
                        }
                    }
                    return ("feita", $"canceladas:{abertas.Count}");
                }
                default:
                    return ("ignorada", "tipo_desconhecido");
            }
        }

        // ---- recuperacao -----------------------------------------------------------------------------------------------------

        public async Task RetomarCriandoAsync(CancellationToken ct)
        {
            List<(Guid Id, Guid Loja, DateTime Criada)> presas;
            await using (var cx = await _db.OpenConnectionAsync(ct))
            {
                presas = (await cx.QueryAsync<(Guid, Guid, DateTime)>(new CommandDefinition(@"
SELECT id, id_estabelecimento, created_at FROM cobranca_chat
 WHERE status = 'criando' AND id_intent IS NULL AND created_at <= NOW() - @Depois ORDER BY created_at LIMIT 20",
                    new { Depois = RetomarCriandoDepois }, cancellationToken: ct))).ToList();
            }

            foreach (var (id, loja, criada) in presas)
            {
                await using var cx = await _db.OpenConnectionAsync(ct);
                var c = await CobrancaChatRepositorio.ObterAsync(cx, null, id, loja, false, ct);
                if (c is not { Status: StatusCobranca.Criando, IdIntent: null }) continue;
                try
                {
                    await _service.GerarPixAsync(c, null, ct);
                    _logger.LogInformation("[cobranca.chat] ev=pix_retomado cobranca={Id}", id);
                }
                catch (PagamentoException ex)
                {
                    _logger.LogWarning("[cobranca.chat] ev=pix_retomada_falhou cobranca={Id} codigo={Codigo}", id, ex.Codigo);
                    if (DateTime.SpecifyKind(criada, DateTimeKind.Utc) > DateTime.UtcNow - DesistirCriandoDepois) continue;
                    await using var tx = await cx.BeginTransactionAsync(ct);
                    var atual = (await CobrancaChatRepositorio.ObterAsync(cx, tx, id, loja, true, ct))!;
                    if (atual is { Status: StatusCobranca.Criando, IdIntent: null })
                        await CobrancaChatRepositorio.MudarStatusAsync(cx, tx, atual, StatusCobranca.Recusado, "conciliador", null, new { codigo = "pix_nao_gerado", ex.Codigo }, ct);
                    await tx.CommitAsync(ct);
                }
            }
        }

        public async Task ExpirarAprovacoesAsync(CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var velhas = (await cx.QueryAsync<(Guid Id, Guid Loja)>(new CommandDefinition(@"
SELECT id, id_estabelecimento FROM cobranca_chat
 WHERE status = 'aguardando_aprovacao' AND created_at <= NOW() - @Validade LIMIT 50", new { Validade = AprovacaoValidaPor }, cancellationToken: ct))).ToList();
            foreach (var (id, loja) in velhas)
            {
                await using var tx = await cx.BeginTransactionAsync(ct);
                var c = await CobrancaChatRepositorio.ObterAsync(cx, tx, id, loja, true, ct);
                if (c is { Status: StatusCobranca.AguardandoAprovacao })
                {
                    await CobrancaChatRepositorio.MudarStatusAsync(cx, tx, c, StatusCobranca.Cancelado, "sistema", null, new { motivo = "aprovacao_expirada" }, ct);
                    await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE cobranca_chat SET motivo = COALESCE(motivo, 'aprovacao_expirada') WHERE id = @Id;
UPDATE cobranca_chat_aprovacao SET status = 'cancelada', motivo_decisao = 'expirada', updated_at = NOW() WHERE id_cobranca = @Id AND status = 'pendente';",
                        new { Id = id }, tx, cancellationToken: ct));
                }
                await tx.CommitAsync(ct);
            }
        }
    }

    /// <summary>Roda a manutencao a cada 20 s, uma instancia por vez (advisory lock).</summary>
    public sealed class CobrancaChatWorker : BackgroundService
    {
        private const long ChaveTrava = 75442350;
        private readonly IServiceScopeFactory _scopes;
        private readonly NpgsqlDataSource _db;
        private readonly ILogger<CobrancaChatWorker> _logger;

        public CobrancaChatWorker(IServiceScopeFactory scopes, NpgsqlDataSource db, ILogger<CobrancaChatWorker> logger)
        {
            _scopes = scopes;
            _db = db;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var cx = await _db.OpenConnectionAsync(stoppingToken);
                    if (await cx.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_lock(@K)", new { K = ChaveTrava }))
                    {
                        try
                        {
                            using var scope = _scopes.CreateScope();
                            await scope.ServiceProvider.GetRequiredService<CobrancaChatManutencao>().RodarAsync(stoppingToken);
                        }
                        finally
                        {
                            await cx.ExecuteAsync("SELECT pg_advisory_unlock(@K)", new { K = ChaveTrava });
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "[cobranca.chat] ev=manutencao_erro");
                }
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
            }
        }
    }
}
