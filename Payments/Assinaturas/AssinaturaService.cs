using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.MercadoPago;
using APIBack.Service;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Payments.Assinaturas
{
    public interface IAssinaturaService
    {
        Task<PainelAssinaturaDto> PainelAsync(Guid loja, bool podeGerenciar, CancellationToken ct = default);
        Task<AssinaturaDto> ContratarAsync(Guid loja, ContratarRequest r, int? usuarioId, CancellationToken ct = default);
        Task<AlteracaoDto> AlterarAsync(Guid loja, AlterarRequest r, int? usuarioId, CancellationToken ct = default);
        Task<AssinaturaDto> CancelarAsync(Guid loja, CancelarRequest r, int? usuarioId, CancellationToken ct = default);
        Task<AssinaturaDto> RetomarAsync(Guid loja, int? usuarioId, CancellationToken ct = default);
        Task<IReadOnlyList<CobrancaDto>> CobrancasAsync(Guid loja, CancellationToken ct = default);
        Task<CheckoutCobrancaDto> PagarAsync(Guid loja, Guid cobrancaId, PagarCobrancaRequest r, int? usuarioId, CancellationToken ct = default);
        Task<CompraCriadaDto> ComprarAsync(Guid loja, CompraRequest r, int? usuarioId, Guid? solicitacaoId = null, CancellationToken ct = default);
        Task<SolicitacaoDto> SolicitarAsync(Guid loja, SolicitacaoRequest r, int usuarioId, CancellationToken ct = default);
        Task<IReadOnlyList<SolicitacaoDto>> SolicitacoesAsync(Guid loja, CancellationToken ct = default);
        Task<DecisaoDto> DecidirAsync(Guid loja, Guid solicitacaoId, DecisaoRequest r, int? usuarioId, CancellationToken ct = default);
        Task MarcarAvisosLidosAsync(Guid loja, CancellationToken ct = default);
        /// <summary>Gestao: baixa manual (dinheiro recebido fora do MP). Mesmo caminho do pagamento automatico, entao nada conta duas vezes.</summary>
        Task<CobrancaDto> BaixaManualAsync(Guid cobrancaId, BaixaManualRequest r, int? usuarioId, CancellationToken ct = default);
    }

    /// <summary>
    /// Assinaturas e compras da Zippy (recebedor = conta Zippy, nunca a da loja). Preco sempre do catalogo no servidor e guardado na compra/ciclo.
    /// Pagamento: Pix e cartao avulso pelo nucleo (IPixChargeService/ICardPaymentService, origem assinatura|compra_extra, origem_ref_id = cobranca);
    /// renovacao no cartao so com autorizacao explicita (preapproval do MP). Efeitos do pagamento: AssinaturaSql.ConfirmarPagamentoAsync.
    /// </summary>
    public sealed class AssinaturaService : IAssinaturaService
    {
        private readonly NpgsqlDataSource _db;
        private readonly IPixChargeService _pix;
        private readonly ICardPaymentService _cartao;
        private readonly IMercadoPagoCredencialResolver _credenciais;
        private readonly IMercadoPagoPreapprovalClient _preapproval;
        private readonly IMemoryCache _cache;
        private readonly IConfiguration _config;
        private readonly ILogger<AssinaturaService> _logger;

        public AssinaturaService(NpgsqlDataSource db, IPixChargeService pix, ICardPaymentService cartao, IMercadoPagoCredencialResolver credenciais,
            IMercadoPagoPreapprovalClient preapproval, IMemoryCache cache, IConfiguration config, ILogger<AssinaturaService> logger)
        {
            _db = db;
            _pix = pix;
            _cartao = cartao;
            _credenciais = credenciais;
            _preapproval = preapproval;
            _cache = cache;
            _config = config;
            _logger = logger;
        }

        // ---------- leitura ----------

        public async Task<PainelAssinaturaDto> PainelAsync(Guid loja, bool podeGerenciar, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await GarantirPrimeiroCicloAsync(cx, loja, ct);
            var politica = await AssinaturaSql.PoliticaAsync(cx, ct);
            var a = await AssinaturaSql.AtualAsync(cx, null, loja, ct);
            var planos = (await cx.QueryAsync<PlanoCatalogoRow>(new CommandDefinition(
                $"SELECT {AssinaturaSql.ColunasPlano} FROM pacotes_comerciais ORDER BY ordem, preco", cancellationToken: ct))).ToList();
            var situ = await cx.QueryFirstOrDefaultAsync<(string? Situacao, DateTimeOffset? Fim)>(new CommandDefinition(
                "SELECT situacao_comercial, teste_termina_em FROM estabelecimentos WHERE id = @Loja", new { Loja = loja }, cancellationToken: ct));
            var extras = (await cx.QueryAsync<(long Id, string Nome, decimal Preco, int Motoboys, string[] Modulos, string? Cor)>(new CommandDefinition(
                "SELECT id, nome, preco, motoboys, modulos_ativos::text[], cor FROM produtos_avulsos WHERE NOT arquivado AND preco > 0 ORDER BY preco, id",
                cancellationToken: ct))).Select(x => new ExtraDto(x.Id, x.Nome, x.Preco, x.Motoboys, x.Modulos, x.Cor)).ToList();
            var compras = await ComprasAsync(cx, loja, ct);
            var solicitacoes = (await SolicitacoesInternoAsync(cx, loja, ct)).Where(s => s.Status == "pendente").ToList();
            var avisos = (await cx.QueryAsync<AvisoDto>(new CommandDefinition(@"
SELECT id, tipo, mensagem, criado_em, lido_em IS NOT NULL AS lido FROM comercial_aviso
 WHERE id_estabelecimento = @Loja AND criado_em > NOW() - INTERVAL '45 days' ORDER BY criado_em DESC, id DESC LIMIT 20",
                new { Loja = loja }, cancellationToken: ct))).ToList();
            var publicKey = await cx.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT public_key FROM mp_conta_zippy WHERE ambiente = @Amb AND estado = 'ativa'", new { Amb = _credenciais.AmbienteAtivo }, cancellationToken: ct));
            var dias = situ.Situacao == "teste" ? ProntidaoRegras.DiasRestantes(situ.Fim, DateTimeOffset.UtcNow) : null;

            return new PainelAssinaturaDto(
                a == null ? null : await MontarAsync(cx, a, planos, politica, ct),
                new SituacaoResumoDto(situ.Situacao ?? SituacoesComerciais.Ativo, situ.Fim, dias),
                planos.Where(p => !p.Arquivado || p.Chave == a?.PlanoChave).Select(p => new PlanoDto(p.Chave, p.Nome, p.Preco, p.PrecoAnual, p.Cor, p.Icone, p.Frase,
                    p.ModulosAtivos, p.MotoboysBase, p.Chave == a?.PlanoChave && a.Status != StatusAssinatura.Encerrada)).ToList(),
                extras, compras, solicitacoes, avisos,
                new CheckoutDto(publicKey, true, !string.IsNullOrEmpty(publicKey), _credenciais.AmbienteAtivo),
                podeGerenciar, politica.ToleranciaDias);
        }

        public async Task<IReadOnlyList<CobrancaDto>> CobrancasAsync(Guid loja, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var politica = await AssinaturaSql.PoliticaAsync(cx, ct);
            var linhas = (await cx.QueryAsync<CobrancaRow>(new CommandDefinition(
                $"SELECT {AssinaturaSql.ColunasCobranca} FROM comercial_cobranca WHERE id_estabelecimento = @Loja ORDER BY vencimento DESC, created_at DESC LIMIT 200",
                new { Loja = loja }, cancellationToken: ct))).ToList();
            var comprovantes = await ComprovantesAsync(cx, linhas, ct);
            return linhas.Select(c => Dto(c, comprovantes.GetValueOrDefault(c.Id), politica)).ToList();
        }

        private async Task<AssinaturaDto> MontarAsync(NpgsqlConnection cx, AssinaturaRow a, IReadOnlyList<PlanoCatalogoRow> planos, PoliticaInadimplencia politica,
            CancellationToken ct)
        {
            var plano = planos.FirstOrDefault(p => p.Chave == a.PlanoChave);
            var abertas = (await cx.QueryAsync<CobrancaRow>(new CommandDefinition(
                $"SELECT {AssinaturaSql.ColunasCobranca} FROM comercial_cobranca WHERE id_assinatura = @A AND status IN ('aberta', 'processando') ORDER BY vencimento",
                new { A = a.Id }, cancellationToken: ct))).ToList();
            var proxima = abertas.FirstOrDefault(c => c.Tipo == TiposCobranca.Ciclo) ?? abertas.FirstOrDefault();
            var hoje = FusoComercial.Data(DateTimeOffset.UtcNow);
            var vencida = abertas.Where(c => c.Tipo == TiposCobranca.Ciclo).Select(c => (DateTimeOffset?)c.Vencimento).Min();
            var av = Inadimplencia.Avaliar(vencida.HasValue ? FusoComercial.Data(vencida.Value) : null, hoje, politica.ToleranciaDias);
            var acesso = a.Status == StatusAssinatura.Encerrada || a.Financeiro == EstadoFinanceiro.Suspensa ? "suspenso"
                : a.Financeiro == EstadoFinanceiro.EmAtraso ? "tolerancia" : "liberado";
            var nomeMudanca = a.MudancaPlano == null ? null : planos.FirstOrDefault(p => p.Chave == a.MudancaPlano)?.Nome ?? a.MudancaPlano;
            return new AssinaturaDto(a.Id, new PlanoResumoDto(a.PlanoChave, plano?.Nome ?? a.PlanoChave, plano?.Cor, plano?.Icone), a.Periodicidade, a.Preco, a.Status,
                a.Financeiro, acesso, a.PeriodoInicio, a.PeriodoFim, proxima?.Vencimento, proxima == null ? null : Dto(proxima, null, politica),
                a.Metodo, a.RenovacaoAutomatica, a.CartaoFinal, a.CartaoBandeira, a.MpPreapprovalStatus,
                a.MudancaPlano == null || a.PeriodoFim == null ? null
                    : new MudancaAgendadaDto(a.MudancaPlano, nomeMudanca!, a.MudancaPeriodicidade ?? a.Periodicidade, a.MudancaPreco ?? 0, a.PeriodoFim.Value),
                a.CancelamentoSolicitadoEm == null ? null : new CancelamentoDto(a.CancelamentoSolicitadoEm.Value, a.CancelamentoMotivo, a.PeriodoFim),
                vencida == null || a.Status == StatusAssinatura.Encerrada ? null
                    : new InadimplenciaDto(vencida, av.DiasAtraso, politica.ToleranciaDias,
                        av.SuspendeEm.HasValue ? FusoComercial.InicioDoDia(av.SuspendeEm.Value) : null, a.SuspensaEm,
                        abertas.Where(c => c.Tipo == TiposCobranca.Ciclo && c.Vencimento <= DateTimeOffset.UtcNow).Sum(c => c.Valor)),
                a.Versao, a.CreatedAt);
        }

        private static CobrancaDto Dto(CobrancaRow c, ComprovanteDto? comprovante, PoliticaInadimplencia politica)
        {
            var aberta = StatusCobrancaComercial.EmAberto(c.Status);
            var hoje = FusoComercial.Data(DateTimeOffset.UtcNow);
            var dias = aberta ? Math.Max(0, hoje.DayNumber - FusoComercial.Data(c.Vencimento).DayNumber) : 0;
            object? detalhe = null;
            if (!string.IsNullOrEmpty(c.Detalhe))
            {
                try { detalhe = JsonSerializer.Deserialize<JsonElement>(c.Detalhe); } catch (JsonException) { }
            }
            return new CobrancaDto(c.Id, c.Tipo, c.Descricao, c.Valor, c.Vencimento, c.Status, c.Meio, c.PagoEm, c.PagoVia, c.ValorPago, c.ValorExcedente,
                aberta && c.Vencimento <= DateTimeOffset.UtcNow && dias > 0, dias, aberta, c.IdIntentAtual, comprovante, detalhe, c.CreatedAt);
        }

        /// <summary>Comprovante = o pagamento aprovado no MP (id, meio, cartao final) ou a baixa manual. Nada de dado do pagador.</summary>
        private static async Task<Dictionary<Guid, ComprovanteDto>> ComprovantesAsync(NpgsqlConnection cx, IReadOnlyList<CobrancaRow> linhas, CancellationToken ct)
        {
            var pagas = linhas.Where(c => c.Status == StatusCobrancaComercial.Paga).ToList();
            var r = new Dictionary<Guid, ComprovanteDto>();
            if (pagas.Count == 0) return r;
            var intents = pagas.Where(c => c.IdIntentAtual.HasValue && c.PagoVia == "mercadopago").Select(c => c.IdIntentAtual!.Value).ToArray();
            var pags = (await cx.QueryAsync<(Guid IdIntent, long MpPaymentId, string Metodo, string? CartaoBandeira, string? CartaoFinal, DateTimeOffset? AprovadoEm)>(
                new CommandDefinition(@"
SELECT DISTINCT ON (id_intent) id_intent, mp_payment_id, metodo, cartao_bandeira, cartao_final, aprovado_em FROM pagamento
 WHERE id_intent = ANY(@Ids) AND status IN ('aprovado', 'estornado_parcial', 'estornado', 'em_mediacao', 'contestado')
 ORDER BY id_intent, aprovado_em DESC NULLS LAST", new { Ids = intents }, cancellationToken: ct))).ToDictionary(x => x.IdIntent);
            foreach (var c in pagas)
            {
                if (c.IdIntentAtual is { } i && pags.TryGetValue(i, out var p))
                    r[c.Id] = new ComprovanteDto(i, p.MpPaymentId, p.Metodo, p.CartaoBandeira, p.CartaoFinal, p.AprovadoEm ?? c.PagoEm, c.ValorPago, c.PagoVia ?? "mercadopago");
                else
                    r[c.Id] = new ComprovanteDto(c.IdIntentAtual, c.MpPaymentId, c.PagoVia == "manual" ? "manual" : c.PagoVia == "mp_recorrente" ? "cartao" : null,
                        null, null, c.PagoEm, c.ValorPago, c.PagoVia ?? "manual");
            }
            return r;
        }

        private static async Task<List<CompraDto>> ComprasAsync(NpgsqlConnection cx, Guid loja, CancellationToken ct) =>
            (await cx.QueryAsync<CompraDto>(new CommandDefinition(@"
SELECT ce.id, ce.produto_avulso_id, ce.nome, ce.quantidade, ce.preco_unitario, ce.valor_total, ce.motoboys, ce.status, ce.vigencia_fim,
       (SELECT c.id FROM comercial_cobranca c WHERE c.id_compra = ce.id LIMIT 1) AS cobranca_id, ce.created_at AS criada_em
  FROM compra_extra ce WHERE ce.id_estabelecimento = @Loja AND (ce.status IN ('aguardando_pagamento', 'ativa') OR ce.created_at > NOW() - INTERVAL '90 days')
 ORDER BY ce.created_at DESC LIMIT 30", new { Loja = loja }, cancellationToken: ct))).ToList();

        // ---------- contratar ----------

        public async Task<AssinaturaDto> ContratarAsync(Guid loja, ContratarRequest r, int? usuarioId, CancellationToken ct = default)
        {
            var chavePlano = (r.Plano ?? string.Empty).Trim().ToLowerInvariant();
            var periodicidade = (r.Periodicidade ?? Periodicidades.Mensal).Trim().ToLowerInvariant();
            if (!Periodicidades.Valida(periodicidade)) throw PagamentoException.Invalida("Escolha mensal ou anual.", "periodicidade");
            var chave = string.IsNullOrWhiteSpace(r.IdempotencyKey) ? null : r.IdempotencyKey.Trim();
            if (chave is { Length: > 160 }) throw PagamentoException.Invalida("Chave de idempotencia ate 160 caracteres.", "idempotencyKey");

            await using var cx = await _db.OpenConnectionAsync(ct);
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                await cx.ExecuteAsync(new CommandDefinition("SELECT 1 FROM estabelecimentos WHERE id = @Loja FOR UPDATE", new { Loja = loja }, tx, cancellationToken: ct));
                var viva = await AssinaturaSql.VivaAsync(cx, tx, loja, true, ct);
                if (viva != null)
                {
                    if (chave != null && await cx.ExecuteScalarAsync<bool>(new CommandDefinition(
                            "SELECT EXISTS (SELECT 1 FROM assinatura WHERE id = @Id AND idempotency_key = @K)", new { viva.Id, K = chave }, tx, cancellationToken: ct)))
                    {
                        await tx.CommitAsync(ct);
                        return await AssinaturaDaLojaAsync(loja, ct);
                    }
                    throw new PagamentoException(AssinaturaCodigos.JaExiste, 409, "A loja ja tem uma assinatura. Use alterar plano.");
                }
                var plano = await PlanoValidoAsync(cx, tx, chavePlano, periodicidade, ct);
                var preco = plano.PrecoDe(periodicidade)!.Value;
                var id = Guid.NewGuid();
                await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO assinatura (id, id_estabelecimento, plano_chave, periodicidade, preco, status, contratada_por, idempotency_key)
VALUES (@Id, @Loja, @Plano, @Per, @Preco, 'pendente', @U, @K)", new { Id = id, Loja = loja, Plano = plano.Chave, Per = periodicidade, Preco = preco, U = usuarioId, K = chave },
                    tx, cancellationToken: ct));
                await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "assinatura", id.ToString(), "contratada", usuarioId.HasValue ? "usuario" : "sistema", usuarioId,
                    new { plano = plano.Chave, periodicidade, preco }, ct);
                await tx.CommitAsync(ct);
            }
            await GarantirPrimeiroCicloAsync(cx, loja, ct);
            return await AssinaturaDaLojaAsync(loja, ct);
        }

        /// <summary>
        /// Primeiro ciclo: comeca no fim do teste (sem cobranca automatica: a fatura fica para a loja pagar) ou hoje, se o teste ja acabou ou a loja ja e ativa.
        /// Em implantacao ainda nao ha ciclo: so a escolha do plano (o teste de 15 dias comeca ao "Comecar a operar").
        /// </summary>
        internal static async Task GarantirPrimeiroCicloAsync(NpgsqlConnection cx, Guid loja, CancellationToken ct)
        {
            var a = await AssinaturaSql.VivaAsync(cx, null, loja, false, ct);
            if (a == null || a.Status != StatusAssinatura.Pendente || a.IdCicloAtual != null) return;
            var situ = await cx.QueryFirstOrDefaultAsync<(string? Situacao, DateTimeOffset? Fim)>(new CommandDefinition(
                "SELECT situacao_comercial, teste_termina_em FROM estabelecimentos WHERE id = @Loja", new { Loja = loja }, cancellationToken: ct));
            if (situ.Situacao is null or SituacoesComerciais.Implantacao) return;

            await using var tx = await cx.BeginTransactionAsync(ct);
            a = await AssinaturaSql.PorIdAsync(cx, tx, a.Id, true, ct);
            if (a == null || a.IdCicloAtual != null) return;
            var agora = DateTimeOffset.UtcNow;
            var inicio = situ.Situacao == SituacoesComerciais.Teste && situ.Fim > agora ? situ.Fim.Value : agora;
            var ancora = FusoComercial.Data(inicio);
            var (ini, fim) = CicloDatas.Ciclo(ancora, 1, a.Periodicidade);
            var nome = (await AssinaturaSql.PlanoAsync(cx, tx, a.PlanoChave, ct))?.Nome ?? a.PlanoChave;
            var ciclo = await AssinaturaSql.CriarCicloAsync(cx, tx, a, 1, a.PlanoChave, nome, a.Periodicidade, a.Preco, ini, fim, null, ct);
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET ancora = @Ancora, ancora_ciclo = 1, id_ciclo_atual = @C, periodo_inicio = @Ini, periodo_fim = @Fim, versao = versao + 1, updated_at = NOW()
 WHERE id = @A", new { Ancora = ancora, C = ciclo.Id, Ini = ciclo.Inicio, Fim = ciclo.Fim, A = a.Id }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }

        private static async Task<PlanoCatalogoRow> PlanoValidoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, string chave, string periodicidade, CancellationToken ct)
        {
            var plano = chave.Length == 0 || chave == "personalizado" ? null : await AssinaturaSql.PlanoAsync(cx, tx, chave, ct);
            if (plano == null || plano.Arquivado) throw PagamentoException.Regra(AssinaturaCodigos.PlanoInvalido, "Escolha um plano do catalogo.");
            var preco = plano.PrecoDe(periodicidade);
            if (preco is null or <= 0)
                throw PagamentoException.Regra(AssinaturaCodigos.PeriodicidadeIndisponivel, $"O plano {plano.Nome} nao tem preco {(periodicidade == Periodicidades.Anual ? "anual" : "mensal")}.");
            return plano;
        }

        private async Task<AssinaturaDto> AssinaturaDaLojaAsync(Guid loja, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var a = await AssinaturaSql.AtualAsync(cx, null, loja, ct) ?? throw new PagamentoException(AssinaturaCodigos.SemAssinatura, 404, "A loja nao tem assinatura.");
            var planos = (await cx.QueryAsync<PlanoCatalogoRow>(new CommandDefinition($"SELECT {AssinaturaSql.ColunasPlano} FROM pacotes_comerciais", cancellationToken: ct))).ToList();
            return await MontarAsync(cx, a, planos, await AssinaturaSql.PoliticaAsync(cx, ct), ct);
        }

        // ---------- alterar ----------

        public async Task<AlteracaoDto> AlterarAsync(Guid loja, AlterarRequest r, int? usuarioId, CancellationToken ct = default)
        {
            var chavePlano = (r.Plano ?? string.Empty).Trim().ToLowerInvariant();
            await using var cx = await _db.OpenConnectionAsync(ct);
            var intentsParaCancelar = new List<Guid>();
            PrevisaoMudanca previsao;
            CobrancaDto? cobranca = null;
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                var a = await AssinaturaSql.VivaAsync(cx, tx, loja, true, ct) ?? throw new PagamentoException(AssinaturaCodigos.SemAssinatura, 404, "A loja nao tem assinatura.");
                var periodicidade = (r.Periodicidade ?? a.Periodicidade).Trim().ToLowerInvariant();
                if (!Periodicidades.Valida(periodicidade)) throw PagamentoException.Invalida("Escolha mensal ou anual.", "periodicidade");
                var plano = await PlanoValidoAsync(cx, tx, chavePlano, periodicidade, ct);
                var precoNovo = plano.PrecoDe(periodicidade)!.Value;
                var ciclo = a.IdCicloAtual.HasValue ? await AssinaturaSql.CicloAsync(cx, tx, a.IdCicloAtual.Value, ct) : null;
                var hoje = FusoComercial.Data(DateTimeOffset.UtcNow);

                // Ainda nao pago (pendente): troca simples, sem proporcional. A fatura em aberto passa a ter o preco novo.
                if (a.Status == StatusAssinatura.Pendente)
                {
                    previsao = new PrevisaoMudanca(plano.Chave == a.PlanoChave && periodicidade == a.Periodicidade ? TiposMudanca.Nenhuma : TiposMudanca.Upgrade,
                        a.Preco, precoNovo, 0, 0, 0, 0, "agora", hoje, new[] { "Voce ainda nao pagou o primeiro ciclo: a fatura em aberto passa a ter o preco do plano novo." });
                    if (previsao.Tipo == TiposMudanca.Nenhuma) throw new PagamentoException(AssinaturaCodigos.SemMudanca, 409, "Este ja e o seu plano.");
                    if (!r.Confirmar) { await tx.CommitAsync(ct); return new AlteracaoDto(previsao, false, null, null); }
                    intentsParaCancelar.AddRange(await IntentsAbertasAsync(cx, tx, a.Id, ct));
                    await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE comercial_cobranca SET status = 'cancelada', cancelada_em = NOW(), cancelada_por = @U, cancelamento_motivo = 'plano_trocado', versao = versao + 1, updated_at = NOW()
 WHERE id_assinatura = @A AND status IN ('aberta', 'processando');
UPDATE assinatura_ciclo SET status = 'cancelado', updated_at = NOW() WHERE id_assinatura = @A AND status = 'a_pagar';
UPDATE assinatura SET plano_chave = @Plano, periodicidade = @Per, preco = @Preco, id_ciclo_atual = NULL, periodo_inicio = NULL, periodo_fim = NULL,
       ancora = NULL, versao = versao + 1, updated_at = NOW() WHERE id = @A;",
                        new { A = a.Id, U = usuarioId, Plano = plano.Chave, Per = periodicidade, Preco = precoNovo }, tx, cancellationToken: ct));
                    // Ciclo novo com numero seguinte (o cancelado fica no historico).
                    await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "assinatura", a.Id.ToString(), "plano_alterado", "usuario", usuarioId,
                        new { de = a.PlanoChave, para = plano.Chave, periodicidade, precoNovo, pendente = true }, ct);
                    await tx.CommitAsync(ct);
                    await CancelarIntentsAsync(intentsParaCancelar, loja, usuarioId, ct);
                    await RecriarPrimeiroCicloAsync(cx, loja, ct);
                    return new AlteracaoDto(previsao, true, null, await AssinaturaDaLojaAsync(loja, ct));
                }

                if (ciclo == null) throw PagamentoException.Estado("Assinatura sem ciclo vigente.", a.Status);
                previsao = Proporcional.Calcular(a.Preco, a.Periodicidade, precoNovo, periodicidade, FusoComercial.Data(ciclo.Inicio), FusoComercial.Data(ciclo.Fim),
                    hoje, plano.Chave == a.PlanoChave);
                if (previsao.Tipo == TiposMudanca.Nenhuma)
                {
                    // Voltar ao plano atual desfaz um downgrade agendado.
                    if (a.MudancaPlano != null && r.Confirmar)
                    {
                        await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET mudanca_plano = NULL, mudanca_periodicidade = NULL, mudanca_preco = NULL, mudanca_pedida_em = NULL, mudanca_pedida_por = NULL,
       versao = versao + 1, updated_at = NOW() WHERE id = @A", new { A = a.Id }, tx, cancellationToken: ct));
                        await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "assinatura", a.Id.ToString(), "mudanca_desfeita", "usuario", usuarioId, new { a.MudancaPlano }, ct);
                        await tx.CommitAsync(ct);
                        return new AlteracaoDto(previsao, true, null, await AssinaturaDaLojaAsync(loja, ct));
                    }
                    throw new PagamentoException(AssinaturaCodigos.SemMudanca, 409, "Este ja e o seu plano.");
                }
                if (!r.Confirmar) { await tx.CommitAsync(ct); return new AlteracaoDto(previsao, false, null, null); }
                if (r.ValorPrevisto.HasValue && r.ValorPrevisto.Value != previsao.ValorAgora)
                    throw new PagamentoException(AssinaturaCodigos.PrevisaoDesatualizada, 409, "O valor mudou desde a previsao. Confira a conta nova.",
                        new Dictionary<string, object?> { ["previsao"] = previsao });

                // Uma diferenca em aberto por vez: pedir outra troca substitui a anterior.
                var upgradesAbertos = (await cx.QueryAsync<Guid>(new CommandDefinition(
                    "SELECT id FROM comercial_cobranca WHERE id_assinatura = @A AND tipo = 'upgrade' AND status IN ('aberta', 'processando') FOR UPDATE",
                    new { A = a.Id }, tx, cancellationToken: ct))).ToList();
                foreach (var u in upgradesAbertos)
                {
                    intentsParaCancelar.AddRange(await cx.QueryAsync<Guid>(new CommandDefinition(
                        "SELECT id FROM pagamento_intent WHERE origem_ref_id = @C AND status IN ('criado', 'aguardando_pagamento')", new { C = u }, tx, cancellationToken: ct)));
                    await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE comercial_cobranca SET status = 'cancelada', cancelada_em = NOW(), cancelada_por = @U, cancelamento_motivo = 'substituida', versao = versao + 1, updated_at = NOW()
 WHERE id = @Id", new { Id = u, U = usuarioId }, tx, cancellationToken: ct));
                }

                if (previsao.Tipo == TiposMudanca.Downgrade)
                {
                    await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET mudanca_plano = @Plano, mudanca_periodicidade = @Per, mudanca_preco = @Preco, mudanca_pedida_em = NOW(), mudanca_pedida_por = @U,
       versao = versao + 1, updated_at = NOW() WHERE id = @A", new { Plano = plano.Chave, Per = periodicidade, Preco = precoNovo, U = usuarioId, A = a.Id },
                        tx, cancellationToken: ct));
                    await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "assinatura", a.Id.ToString(), "downgrade_agendado", "usuario", usuarioId,
                        new { de = a.PlanoChave, para = plano.Chave, periodicidade, precoNovo, em = previsao.AplicaEm }, ct);
                }
                else if (previsao.ValorAgora <= 0)
                {
                    await AssinaturaSql.AplicarMudancaAgoraAsync(cx, tx, a, previsao.Tipo, plano.Chave, periodicidade, precoNovo, "usuario", usuarioId, ct);
                }
                else
                {
                    var id = Guid.NewGuid();
                    var descricao = $"Troca para {plano.Nome}{(previsao.Tipo == TiposMudanca.ParaAnual ? " anual" : "")} (diferenca)";
                    await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO comercial_cobranca (id, id_estabelecimento, tipo, id_assinatura, id_ciclo, descricao, valor, vencimento, detalhe, idempotency_key)
VALUES (@Id, @Loja, 'upgrade', @A, @C, @D, @V, NOW(), CAST(@Det AS jsonb), @K)", new
                    {
                        Id = id, Loja = loja, A = a.Id, C = ciclo.Id, D = descricao.Length > 140 ? descricao[..140] : descricao, V = previsao.ValorAgora,
                        Det = JsonSerializer.Serialize(new
                        {
                            tipo = previsao.Tipo, planoAnterior = a.PlanoChave, planoNovo = plano.Chave, periodicidadeNova = periodicidade, precoNovo,
                            precoAtual = a.Preco, previsao.DiasRestantes, previsao.DiasCiclo, previsao.Credito, previsao.Explicacao
                        }), K = $"upgrade:{id:N}"
                    }, tx, cancellationToken: ct));
                    await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "comercial_cobranca", id.ToString(), "upgrade_criado", "usuario", usuarioId,
                        new { de = a.PlanoChave, para = plano.Chave, valor = previsao.ValorAgora }, ct);
                    var c = await AssinaturaSql.CobrancaAsync(cx, tx, id, false, ct);
                    cobranca = Dto(c!, null, PoliticaInadimplencia.Padrao);
                }
                await tx.CommitAsync(ct);
            }
            await CancelarIntentsAsync(intentsParaCancelar, loja, usuarioId, ct);
            _cache.Remove(SituacaoComercialService.CacheKey(loja));
            return new AlteracaoDto(previsao, true, cobranca, await AssinaturaDaLojaAsync(loja, ct));
        }

        private static async Task RecriarPrimeiroCicloAsync(NpgsqlConnection cx, Guid loja, CancellationToken ct)
        {
            var a = await AssinaturaSql.VivaAsync(cx, null, loja, false, ct);
            if (a == null || a.Status != StatusAssinatura.Pendente || a.IdCicloAtual != null) return;
            var situ = await cx.QueryFirstOrDefaultAsync<(string? Situacao, DateTimeOffset? Fim)>(new CommandDefinition(
                "SELECT situacao_comercial, teste_termina_em FROM estabelecimentos WHERE id = @Loja", new { Loja = loja }, cancellationToken: ct));
            if (situ.Situacao is null or SituacoesComerciais.Implantacao) return;
            await using var tx = await cx.BeginTransactionAsync(ct);
            a = await AssinaturaSql.PorIdAsync(cx, tx, a.Id, true, ct);
            if (a == null || a.IdCicloAtual != null) return;
            var numero = await cx.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COALESCE(MAX(numero), 0) + 1 FROM assinatura_ciclo WHERE id_assinatura = @A",
                new { A = a.Id }, tx, cancellationToken: ct));
            var agora = DateTimeOffset.UtcNow;
            var inicio = situ.Situacao == SituacoesComerciais.Teste && situ.Fim > agora ? situ.Fim.Value : agora;
            var ancora = FusoComercial.Data(inicio);
            var (ini, fim) = CicloDatas.Ciclo(ancora, 1, a.Periodicidade);
            var nome = (await AssinaturaSql.PlanoAsync(cx, tx, a.PlanoChave, ct))?.Nome ?? a.PlanoChave;
            var ciclo = await AssinaturaSql.CriarCicloAsync(cx, tx, a, numero, a.PlanoChave, nome, a.Periodicidade, a.Preco, ini, fim, null, ct);
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET ancora = @Ancora, ancora_ciclo = @N, id_ciclo_atual = @C, periodo_inicio = @Ini, periodo_fim = @Fim, versao = versao + 1, updated_at = NOW()
 WHERE id = @A", new { Ancora = ancora, N = numero, C = ciclo.Id, Ini = ciclo.Inicio, Fim = ciclo.Fim, A = a.Id }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }

        private static async Task<IReadOnlyList<Guid>> IntentsAbertasAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid assinaturaId, CancellationToken ct) =>
            (await cx.QueryAsync<Guid>(new CommandDefinition(@"
SELECT i.id FROM pagamento_intent i JOIN comercial_cobranca c ON c.id = i.origem_ref_id
 WHERE c.id_assinatura = @A AND c.status IN ('aberta', 'processando') AND i.status IN ('criado', 'aguardando_pagamento')",
                new { A = assinaturaId }, tx, cancellationToken: ct))).ToList();

        /// <summary>Pix abertos de uma divida cancelada: cancelados no MP. Se ja pagou, o efeito registra como excedente.</summary>
        private async Task CancelarIntentsAsync(IEnumerable<Guid> intents, Guid loja, int? usuarioId, CancellationToken ct)
        {
            foreach (var i in intents.Distinct())
            {
                try { await _pix.CancelarAsync(i, loja, "usuario", usuarioId, ct); }
                catch (PagamentoException ex) { _logger.LogWarning("[assinatura] ev=cancelar_pix_falhou intent={Intent} codigo={Codigo}", i, ex.Codigo); }
            }
        }

        // ---------- cancelar / retomar ----------

        public async Task<AssinaturaDto> CancelarAsync(Guid loja, CancelarRequest r, int? usuarioId, CancellationToken ct = default)
        {
            var motivo = string.IsNullOrWhiteSpace(r.Motivo) ? null : r.Motivo.Trim();
            if (motivo == null) throw PagamentoException.Invalida("Conte o motivo do cancelamento.", "motivo");
            if (motivo.Length > 300) motivo = motivo[..300];
            await using var cx = await _db.OpenConnectionAsync(ct);
            var intents = new List<Guid>();
            string? preapproval = null;
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                var a = await AssinaturaSql.VivaAsync(cx, tx, loja, true, ct) ?? throw new PagamentoException(AssinaturaCodigos.SemAssinatura, 404, "A loja nao tem assinatura.");
                if (a.CancelamentoSolicitadoEm != null) { await tx.CommitAsync(ct); return await AssinaturaDaLojaAsync(loja, ct); }
                preapproval = a.MpPreapprovalStatus is "authorized" or "pending" or "paused" ? a.MpPreapprovalId : null;
                if (a.Status == StatusAssinatura.Pendente)
                {
                    // Nunca paga: encerra ja. Nada a manter.
                    intents.AddRange(await IntentsAbertasAsync(cx, tx, a.Id, ct));
                    await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE comercial_cobranca SET status = 'cancelada', cancelada_em = NOW(), cancelada_por = @U, cancelamento_motivo = 'assinatura_cancelada', versao = versao + 1, updated_at = NOW()
 WHERE id_assinatura = @A AND status IN ('aberta', 'processando');
UPDATE assinatura_ciclo SET status = 'cancelado', updated_at = NOW() WHERE id_assinatura = @A AND status = 'a_pagar';
UPDATE assinatura SET status = 'encerrada', encerrada_em = NOW(), encerramento_motivo = 'cancelada_antes_do_pagamento', financeiro = 'em_dia',
       cancelamento_solicitado_em = NOW(), cancelamento_por = @U, cancelamento_motivo = @M, renovacao_automatica = FALSE, versao = versao + 1, updated_at = NOW()
 WHERE id = @A;", new { A = a.Id, U = usuarioId, M = motivo }, tx, cancellationToken: ct));
                }
                else
                {
                    // Para as renovacoes e mantem o acesso ate o fim do periodo pago. Sem estorno automatico.
                    // Faturas de ciclos que ainda nao comecaram deixam de existir; a do ciclo vigente (se em aberto) continua devida.
                    intents.AddRange(await cx.QueryAsync<Guid>(new CommandDefinition(@"
SELECT i.id FROM pagamento_intent i JOIN comercial_cobranca c ON c.id = i.origem_ref_id JOIN assinatura_ciclo ci ON ci.id = c.id_ciclo
 WHERE c.id_assinatura = @A AND c.status IN ('aberta', 'processando') AND ci.inicio >= @Fim AND i.status IN ('criado', 'aguardando_pagamento')",
                        new { A = a.Id, Fim = a.PeriodoFim }, tx, cancellationToken: ct)));
                    await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE comercial_cobranca c SET status = 'cancelada', cancelada_em = NOW(), cancelada_por = @U, cancelamento_motivo = 'assinatura_cancelada', versao = c.versao + 1, updated_at = NOW()
  FROM assinatura_ciclo ci
 WHERE ci.id = c.id_ciclo AND c.id_assinatura = @A AND c.status IN ('aberta', 'processando') AND ci.inicio >= @Fim;
UPDATE assinatura_ciclo SET status = 'cancelado', updated_at = NOW() WHERE id_assinatura = @A AND status = 'a_pagar' AND inicio >= @Fim;
UPDATE assinatura SET cancelamento_solicitado_em = NOW(), cancelamento_por = @U, cancelamento_motivo = @M, renovacao_automatica = FALSE,
       mudanca_plano = NULL, mudanca_periodicidade = NULL, mudanca_preco = NULL, versao = versao + 1, updated_at = NOW()
 WHERE id = @A;", new { A = a.Id, U = usuarioId, M = motivo, Fim = a.PeriodoFim }, tx, cancellationToken: ct));
                }
                await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "assinatura", a.Id.ToString(), "cancelamento", "usuario", usuarioId,
                    new { motivo, acessoAte = a.PeriodoFim, status = a.Status }, ct);
                await tx.CommitAsync(ct);
            }
            await CancelarIntentsAsync(intents, loja, usuarioId, ct);
            if (preapproval != null) await CancelarPreapprovalAsync(preapproval, ct);
            _cache.Remove(SituacaoComercialService.CacheKey(loja));
            return await AssinaturaDaLojaAsync(loja, ct);
        }

        /// <summary>Cancela no MP; se falhar, o worker tenta de novo (a assinatura ja esta marcada como cancelada aqui).</summary>
        private async Task CancelarPreapprovalAsync(string preapprovalId, CancellationToken ct)
        {
            try
            {
                var cred = await _credenciais.RecebedorZippyAsync(null, ct);
                var pre = await _preapproval.CancelarAsync(cred, preapprovalId, ct);
                await using var cx = await _db.OpenConnectionAsync(ct);
                await cx.ExecuteAsync(new CommandDefinition("UPDATE assinatura SET mp_preapproval_status = @S, updated_at = NOW() WHERE mp_preapproval_id = @P",
                    new { S = pre.Status ?? "cancelled", P = preapprovalId }, cancellationToken: ct));
            }
            catch (Exception ex) when (ex is MercadoPagoApiException or PagamentoException)
            {
                _logger.LogWarning("[assinatura] ev=cancelar_preapproval_falhou preapproval={P}", preapprovalId);
            }
        }

        public async Task<AssinaturaDto> RetomarAsync(Guid loja, int? usuarioId, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            var a = await AssinaturaSql.VivaAsync(cx, tx, loja, true, ct) ?? throw new PagamentoException(AssinaturaCodigos.SemAssinatura, 404, "A loja nao tem assinatura.");
            if (a.CancelamentoSolicitadoEm == null) throw new PagamentoException(AssinaturaCodigos.SemMudanca, 409, "A assinatura nao esta cancelada.");
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET cancelamento_solicitado_em = NULL, cancelamento_por = NULL, cancelamento_motivo = NULL, versao = versao + 1, updated_at = NOW() WHERE id = @A",
                new { A = a.Id }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "assinatura", a.Id.ToString(), "cancelamento_desfeito", "usuario", usuarioId,
                new { a.CancelamentoMotivo }, ct);
            await tx.CommitAsync(ct);
            return await AssinaturaDaLojaAsync(loja, ct);
        }

        // ---------- pagar ----------

        public async Task<CheckoutCobrancaDto> PagarAsync(Guid loja, Guid cobrancaId, PagarCobrancaRequest r, int? usuarioId, CancellationToken ct = default)
        {
            var metodo = (r.Metodo ?? string.Empty).Trim().ToLowerInvariant();
            if (metodo is not (MetodosPagamento.Pix or MetodosPagamento.Cartao)) throw PagamentoException.Invalida("Escolha Pix ou cartao.", "metodo");
            await using var cx = await _db.OpenConnectionAsync(ct);
            var c = await AssinaturaSql.CobrancaAsync(cx, null, cobrancaId, false, ct);
            if (c == null || c.IdEstabelecimento != loja) throw PagamentoException.NaoEncontrado("Cobranca nao encontrada.");
            if (c.Status == StatusCobrancaComercial.Paga) throw new PagamentoException(PagamentoCodigos.JaPago, 409, "Esta cobranca ja foi paga.");
            if (c.Status == StatusCobrancaComercial.Cancelada) throw PagamentoException.Estado("Esta cobranca foi cancelada.", c.Status);
            // O MP esta cobrando o cartao (e repete as tentativas sozinho): nao duplicar. Depois de vencida, Pix fica liberado para regularizar.
            if (c.Status == StatusCobrancaComercial.Processando && c.Meio == "cartao_recorrente" && FusoComercial.Data(DateTimeOffset.UtcNow) <= FusoComercial.Data(c.Vencimento))
                throw new PagamentoException(AssinaturaCodigos.CobrancaNoCartao, 409, "Esta fatura sera cobrada no cartao cadastrado pelo Mercado Pago.");

            var origem = c.Tipo == TiposCobranca.CompraExtra ? OrigensPagamento.CompraExtra : OrigensPagamento.Assinatura;
            var prefixo = $"{origem}:{c.Id:N}";
            if (metodo == MetodosPagamento.Pix) return await PagarPixAsync(cx, c, origem, prefixo, usuarioId, ct);
            if (r.RenovacaoAutomatica) return await AssinarCartaoAsync(cx, c, r, usuarioId, ct);
            return await PagarCartaoAsync(cx, c, origem, prefixo, r, usuarioId, ct);
        }

        /// <summary>Chave da emissao atual: se a intencao dessa chave venceu/foi cancelada, passa para a proxima (nova intencao, sem divida nova).</summary>
        private static async Task<string> ChaveEmissaoAsync(NpgsqlConnection cx, CobrancaRow c, string prefixo, CancellationToken ct)
        {
            for (var n = c.Emissao; n < c.Emissao + 50; n++)
            {
                var chave = n == 0 ? prefixo : $"{prefixo}:{n}";
                var status = await cx.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT status FROM pagamento_intent WHERE id_estabelecimento = @Loja AND idempotency_key = @K", new { Loja = c.IdEstabelecimento, K = chave }, cancellationToken: ct));
                if (status is StatusIntent.Expirado or StatusIntent.Cancelado) continue;
                if (n != c.Emissao)
                    await cx.ExecuteAsync(new CommandDefinition("UPDATE comercial_cobranca SET emissao = GREATEST(emissao, @N), updated_at = NOW() WHERE id = @Id",
                        new { N = n, c.Id }, cancellationToken: ct));
                return chave;
            }
            throw PagamentoException.Estado("Muitas tentativas para esta cobranca. Fale com a Zippy.");
        }

        private async Task<CheckoutCobrancaDto> PagarPixAsync(NpgsqlConnection cx, CobrancaRow c, string origem, string prefixo, int? usuarioId, CancellationToken ct)
        {
            var cartaoEmAnalise = await cx.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM pagamento_intent WHERE origem_ref_id = @C AND status = 'processando')", new { C = c.Id }, cancellationToken: ct));
            if (cartaoEmAnalise) throw PagamentoException.Estado("Ha um pagamento com cartao em analise para esta cobranca.", "processando");

            PixChargeResult pix;
            for (var tentativa = 0; ; tentativa++)
            {
                var chave = await ChaveEmissaoAsync(cx, c, prefixo, ct);
                try
                {
                    pix = await _pix.CriarAsync(new PixChargeRequest
                    {
                        IdEstabelecimento = c.IdEstabelecimento, Origem = origem, Valor = c.Valor, Descricao = c.Descricao, IdempotencyKey = chave,
                        OrigemRefId = c.Id, CriadoPor = usuarioId, ValidadeMinutos = 60
                    }, ct);
                    break;
                }
                catch (PagamentoException ex) when (ex.Codigo == PagamentoCodigos.EstadoInvalido && tentativa == 0)
                {
                    // A intencao venceu entre a leitura e a chamada: a proxima emissao resolve.
                    c.Emissao++;
                }
            }
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE comercial_cobranca SET id_intent_atual = @I, meio = 'pix', versao = versao + 1, updated_at = NOW() WHERE id = @Id AND status IN ('aberta', 'processando')",
                new { I = pix.IntentId, c.Id }, cancellationToken: ct));
            var atual = await AssinaturaSql.CobrancaAsync(cx, null, c.Id, false, ct);
            return new CheckoutCobrancaDto(c.Id, MetodosPagamento.Pix, atual?.Status ?? c.Status, c.Valor,
                new PixCheckoutDto(pix.IntentId, pix.Status, pix.CopiaECola, pix.QrCodeBase64, pix.TicketUrl, pix.ExpiraEm, pix.Reaproveitado), null, null);
        }

        private async Task CancelarPixPendenteAsync(NpgsqlConnection cx, CobrancaRow c, int? usuarioId, CancellationToken ct)
        {
            var abertas = await cx.QueryAsync<Guid>(new CommandDefinition(@"
SELECT i.id FROM pagamento_intent i WHERE i.origem_ref_id = @C AND i.status = 'aguardando_pagamento'
   AND EXISTS (SELECT 1 FROM pagamento p WHERE p.id_intent = i.id AND p.metodo = 'pix' AND p.status = 'pendente')", new { C = c.Id }, cancellationToken: ct));
            foreach (var i in abertas)
                await _pix.CancelarAsync(i, c.IdEstabelecimento, "usuario", usuarioId, ct); // PAG_JA_PAGO sobe: o Pix ja foi pago.
        }

        private async Task<CheckoutCobrancaDto> PagarCartaoAsync(NpgsqlConnection cx, CobrancaRow c, string origem, string prefixo, PagarCobrancaRequest r,
            int? usuarioId, CancellationToken ct)
        {
            var k = r.Cartao ?? throw PagamentoException.Invalida("Dados do cartao obrigatorios.", "cartao");
            await CancelarPixPendenteAsync(cx, c, usuarioId, ct);
            var chave = await ChaveEmissaoAsync(cx, c, prefixo + ":cartao", ct);
            var res = await _cartao.CriarAsync(new CardPaymentRequest
            {
                IdEstabelecimento = c.IdEstabelecimento, Origem = origem, Valor = c.Valor, Descricao = c.Descricao, IdempotencyKey = chave, OrigemRefId = c.Id,
                CriadoPor = usuarioId, Token = k.Token ?? string.Empty, PaymentMethodId = k.PaymentMethodId ?? string.Empty, IssuerId = k.IssuerId,
                Parcelas = Math.Clamp(k.Parcelas, 1, 12), Pagador = k.Pagador
            }, ct);
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE comercial_cobranca SET id_intent_atual = @I, meio = 'cartao', versao = versao + 1, updated_at = NOW() WHERE id = @Id AND status IN ('aberta', 'processando')",
                new { I = res.IntentId, c.Id }, cancellationToken: ct));
            var atual = await AssinaturaSql.CobrancaAsync(cx, null, c.Id, false, ct);
            return new CheckoutCobrancaDto(c.Id, MetodosPagamento.Cartao, atual?.Status ?? c.Status, c.Valor, null,
                new CartaoCheckoutDto(res.IntentId, res.Status, res.StatusPagamento, res.StatusDetail, res.Aprovado), null);
        }

        /// <summary>
        /// Renovacao automatica no cartao: so com autorizacao explicita. Cria o preapproval na conta da Zippy; o MP cobra este ciclo no vencimento
        /// (hoje, se ja venceu) e os proximos sozinho, repetindo as recusas. A Zippy registra pelos webhooks subscription_*.
        /// </summary>
        private async Task<CheckoutCobrancaDto> AssinarCartaoAsync(NpgsqlConnection cx, CobrancaRow c, PagarCobrancaRequest r, int? usuarioId, CancellationToken ct)
        {
            if (c.Tipo != TiposCobranca.Ciclo || c.IdAssinatura == null)
                throw PagamentoException.Invalida("Renovacao automatica so vale para a mensalidade do plano.", "renovacaoAutomatica");
            if (!r.AutorizoRenovacao)
                throw PagamentoException.Regra(AssinaturaCodigos.RenovacaoSemAutorizacao, "Confirme que autoriza a cobranca automatica a cada ciclo.");
            var k = r.Cartao ?? throw PagamentoException.Invalida("Dados do cartao obrigatorios.", "cartao");
            if (string.IsNullOrWhiteSpace(k.Token)) throw PagamentoException.Invalida("Token do cartao obrigatorio.", "cartao.token");
            var email = k.Pagador?.Email?.Trim();
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) throw PagamentoException.Invalida("E-mail do titular obrigatorio.", "cartao.pagador.email");

            var a = await AssinaturaSql.PorIdAsync(cx, null, c.IdAssinatura.Value, false, ct) ?? throw PagamentoException.NaoEncontrado();
            if (a.CancelamentoSolicitadoEm != null) throw PagamentoException.Estado("A assinatura foi cancelada: retome antes de cadastrar o cartao.", "cancelada");
            await CancelarPixPendenteAsync(cx, c, usuarioId, ct);
            if (a.MpPreapprovalId != null && a.MpPreapprovalStatus is "authorized" or "pending" or "paused")
                await CancelarPreapprovalAsync(a.MpPreapprovalId, ct); // troca de cartao

            var cred = await _credenciais.RecebedorZippyAsync(null, ct);
            var nome = (await AssinaturaSql.PlanoAsync(cx, null, a.PlanoChave, ct))?.Nome ?? a.PlanoChave;
            var agora = DateTimeOffset.UtcNow;
            var inicio = c.Vencimento > agora.AddMinutes(10) ? c.Vencimento : (DateTimeOffset?)null;
            var corpo = new MpCriarPreapproval
            {
                Reason = $"Zippy - Plano {nome} ({(a.Periodicidade == Periodicidades.Anual ? "anual" : "mensal")})",
                ExternalReference = PreapprovalReferencia.Gerar(cred.Ambiente, a.Id),
                PayerEmail = email!,
                CardTokenId = k.Token!.Trim(),
                BackUrl = (_config["Payments:MercadoPago:PainelUrl"] ?? "https://zippygo.com.br").TrimEnd('/') + "/apps/configuracoes/plano",
                AutoRecurring = new MpPreapprovalRecorrencia
                {
                    Frequency = Periodicidades.Meses(a.Periodicidade), FrequencyType = "months", TransactionAmount = c.Valor,
                    StartDate = inicio?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
                }
            };
            var chave = $"preapproval:{a.Id:N}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(k.Token)))[..24].ToLowerInvariant()}";
            MpPreapproval pre;
            try
            {
                pre = await _preapproval.CriarAsync(cred, corpo, chave, ct);
            }
            catch (MercadoPagoApiException ex)
            {
                if (!ex.Indisponivel)
                    throw new PagamentoException(PagamentoCodigos.CartaoRecusado, 422, "O Mercado Pago nao aceitou este cartao para a renovacao automatica.",
                        new Dictionary<string, object?> { ["mpErro"] = ex.MpErro });
                throw ex.ParaPagamento();
            }
            if (pre.ExternalReference != corpo.ExternalReference)
                throw new PagamentoException(PagamentoCodigos.MercadoPagoRespostaInconsistente, 502, "O Mercado Pago devolveu uma assinatura que nao confere.");

            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET mp_preapproval_id = @P, mp_preapproval_status = @S, preapproval_valor = @V, renovacao_automatica = TRUE, metodo = 'cartao',
       renovacao_autorizada_em = NOW(), renovacao_autorizada_por = @U, cartao_bandeira = @B, versao = versao + 1, updated_at = NOW()
 WHERE id = @A;
UPDATE comercial_cobranca SET status = 'processando', meio = 'cartao_recorrente', versao = versao + 1, updated_at = NOW()
 WHERE id_assinatura = @A AND tipo = 'ciclo' AND status = 'aberta';", new
                {
                    P = pre.Id, S = pre.Status ?? "pending", V = c.Valor, U = usuarioId, B = k.PaymentMethodId, A = a.Id
                }, tx, cancellationToken: ct));
                await PagamentoAuditoria.RegistrarAsync(cx, tx, a.IdEstabelecimento, "assinatura", a.Id.ToString(), "renovacao_autorizada", "usuario", usuarioId,
                    new { preapproval = pre.Id, status = pre.Status, valor = c.Valor, inicio, frequencia = a.Periodicidade }, ct);
                await tx.CommitAsync(ct);
            }
            return new CheckoutCobrancaDto(c.Id, MetodosPagamento.Cartao, StatusCobrancaComercial.Processando, c.Valor, null, null,
                new RecorrenteCheckoutDto(pre.Status, inicio ?? agora, null));
        }

        // ---------- compras e solicitacoes ----------

        public async Task<CompraCriadaDto> ComprarAsync(Guid loja, CompraRequest r, int? usuarioId, Guid? solicitacaoId = null, CancellationToken ct = default)
        {
            if (r.Quantidade is < 1 or > 20) throw PagamentoException.Invalida("Quantidade entre 1 e 20.", "quantidade");
            var chave = string.IsNullOrWhiteSpace(r.IdempotencyKey) ? null : r.IdempotencyKey.Trim();
            if (chave == null || chave.Length > 160) throw PagamentoException.Invalida("Chave de idempotencia obrigatoria (ate 160 caracteres).", "idempotencyKey");
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition("SELECT 1 FROM estabelecimentos WHERE id = @Loja FOR UPDATE", new { Loja = loja }, tx, cancellationToken: ct));

            var existente = await cx.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM compra_extra WHERE id_estabelecimento = @Loja AND idempotency_key = @K", new { Loja = loja, K = chave }, tx, cancellationToken: ct));
            // Duplo clique com chave nova: a mesma compra ainda esperando pagamento e reaproveitada.
            existente ??= await cx.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(@"
SELECT id FROM compra_extra WHERE id_estabelecimento = @Loja AND produto_avulso_id = @P AND quantidade = @Q AND status = 'aguardando_pagamento'
   AND created_at > NOW() - INTERVAL '30 minutes' ORDER BY created_at DESC LIMIT 1", new { Loja = loja, P = r.ProdutoAvulsoId, Q = r.Quantidade }, tx, cancellationToken: ct));
            if (existente.HasValue)
            {
                await tx.CommitAsync(ct);
                return await CompraCriadaAsync(cx, existente.Value, true, ct);
            }

            var produto = await cx.QueryFirstOrDefaultAsync<(long Id, string Nome, decimal Preco, int Motoboys, string[] Modulos, bool Arquivado)?>(new CommandDefinition(
                "SELECT id, nome, preco, motoboys, modulos_ativos::text[], arquivado FROM produtos_avulsos WHERE id = @Id", new { Id = r.ProdutoAvulsoId }, tx, cancellationToken: ct));
            if (produto is not { Arquivado: false } p || p.Preco <= 0)
                throw PagamentoException.Regra(AssinaturaCodigos.ProdutoInvalido, "Este recurso nao esta a venda.");
            var total = Proporcional.Arredondar(p.Preco * r.Quantidade);
            var compraId = Guid.NewGuid();
            var cobrancaId = Guid.NewGuid();
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO compra_extra (id, id_estabelecimento, produto_avulso_id, nome, preco_unitario, quantidade, valor_total, motoboys, modulos_ativos, id_solicitacao, criado_por, idempotency_key)
VALUES (@Id, @Loja, @P, @Nome, @Preco, @Q, @Total, @Motoboys, @Modulos, @Sol, @U, @K);
INSERT INTO comercial_cobranca (id, id_estabelecimento, tipo, id_compra, descricao, valor, vencimento, idempotency_key)
VALUES (@Cob, @Loja, 'compra_extra', @Id, @Desc, @Total, NOW(), @KC);", new
            {
                Id = compraId, Loja = loja, P = p.Id, Nome = p.Nome, Preco = p.Preco, Q = r.Quantidade, Total = total, Motoboys = p.Motoboys, Modulos = p.Modulos,
                Sol = solicitacaoId, U = usuarioId, K = chave, Cob = cobrancaId, KC = $"compra:{compraId:N}",
                Desc = Cortar(r.Quantidade > 1 ? $"{r.Quantidade}x {p.Nome}" : p.Nome)
            }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "compra_extra", compraId.ToString(), "criada", usuarioId.HasValue ? "usuario" : "sistema", usuarioId,
                new { produto = p.Id, p.Nome, precoUnitario = p.Preco, r.Quantidade, total, solicitacaoId }, ct);
            await tx.CommitAsync(ct);
            return await CompraCriadaAsync(cx, compraId, false, ct);
        }

        private static string Cortar(string texto) => texto.Length > 140 ? texto[..140] : texto;

        private static async Task<CompraCriadaDto> CompraCriadaAsync(NpgsqlConnection cx, Guid compraId, bool reaproveitada, CancellationToken ct)
        {
            var compra = await cx.QueryFirstAsync<CompraDto>(new CommandDefinition(@"
SELECT ce.id, ce.produto_avulso_id, ce.nome, ce.quantidade, ce.preco_unitario, ce.valor_total, ce.motoboys, ce.status, ce.vigencia_fim,
       (SELECT c.id FROM comercial_cobranca c WHERE c.id_compra = ce.id LIMIT 1) AS cobranca_id, ce.created_at AS criada_em
  FROM compra_extra ce WHERE ce.id = @Id", new { Id = compraId }, cancellationToken: ct));
            var c = await AssinaturaSql.CobrancaAsync(cx, null, compra.CobrancaId!.Value, false, ct);
            return new CompraCriadaDto(compra, Dto(c!, null, PoliticaInadimplencia.Padrao), reaproveitada);
        }

        public async Task<SolicitacaoDto> SolicitarAsync(Guid loja, SolicitacaoRequest r, int usuarioId, CancellationToken ct = default)
        {
            if (r.Quantidade is < 1 or > 20) throw PagamentoException.Invalida("Quantidade entre 1 e 20.", "quantidade");
            var motivo = string.IsNullOrWhiteSpace(r.Motivo) ? null : r.Motivo.Trim();
            if (motivo is { Length: > 300 }) motivo = motivo[..300];
            await using var cx = await _db.OpenConnectionAsync(ct);
            var produto = await cx.QueryFirstOrDefaultAsync<(long Id, bool Arquivado, decimal Preco)?>(new CommandDefinition(
                "SELECT id, arquivado, preco FROM produtos_avulsos WHERE id = @Id", new { Id = r.ProdutoAvulsoId }, cancellationToken: ct));
            if (produto is not { Arquivado: false } || produto.Value.Preco <= 0) throw PagamentoException.Regra(AssinaturaCodigos.ProdutoInvalido, "Este recurso nao esta a venda.");
            var id = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(@"
INSERT INTO solicitacao_compra (id, id_estabelecimento, produto_avulso_id, quantidade, motivo, solicitante_id)
VALUES (@Id, @Loja, @P, @Q, @M, @U)
ON CONFLICT (id_estabelecimento, produto_avulso_id, solicitante_id) WHERE status = 'pendente' DO NOTHING RETURNING id",
                new { Id = Guid.NewGuid(), Loja = loja, P = r.ProdutoAvulsoId, Q = r.Quantidade, M = motivo, U = usuarioId }, cancellationToken: ct));
            if (id.HasValue)
                await AssinaturaSql.AvisarAsync(cx, null, loja, null, $"solicitacao:{id:N}", "solicitacao_compra", "Um funcionario pediu a compra de um recurso. Aprove ou recuse em Plano e pagamento.", ct);
            var lista = await SolicitacoesInternoAsync(cx, loja, ct);
            return id.HasValue
                ? lista.First(s => s.Id == id.Value)
                : lista.First(s => s.Status == "pendente" && s.ProdutoAvulsoId == r.ProdutoAvulsoId && s.Solicitante.Id == usuarioId);
        }

        public async Task<IReadOnlyList<SolicitacaoDto>> SolicitacoesAsync(Guid loja, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await SolicitacoesInternoAsync(cx, loja, ct);
        }

        private sealed class SolicitacaoRow
        {
            public Guid Id { get; set; }
            public long ProdutoAvulsoId { get; set; }
            public string ProdutoNome { get; set; } = string.Empty;
            public int Quantidade { get; set; }
            public decimal Preco { get; set; }
            public string? Motivo { get; set; }
            public string Status { get; set; } = string.Empty;
            public int SolicitanteId { get; set; }
            public string? SolicitanteNome { get; set; }
            public int? DecididoPor { get; set; }
            public string? DecididoNome { get; set; }
            public DateTimeOffset? DecididoEm { get; set; }
            public string? DecisaoMotivo { get; set; }
            public Guid? IdCompra { get; set; }
            public DateTimeOffset CreatedAt { get; set; }
        }

        private static async Task<IReadOnlyList<SolicitacaoDto>> SolicitacoesInternoAsync(NpgsqlConnection cx, Guid loja, CancellationToken ct) =>
            (await cx.QueryAsync<SolicitacaoRow>(new CommandDefinition(@"
SELECT s.id, s.produto_avulso_id, p.nome AS produto_nome, s.quantidade, p.preco, s.motivo, s.status, s.solicitante_id, us.nome AS solicitante_nome,
       s.decidido_por, ud.nome AS decidido_nome, s.decidido_em, s.decisao_motivo, s.id_compra, s.created_at
  FROM solicitacao_compra s
  JOIN produtos_avulsos p ON p.id = s.produto_avulso_id
  LEFT JOIN usuario us ON us.id = s.solicitante_id
  LEFT JOIN usuario ud ON ud.id = s.decidido_por
 WHERE s.id_estabelecimento = @Loja ORDER BY (s.status = 'pendente') DESC, s.created_at DESC LIMIT 50", new { Loja = loja }, cancellationToken: ct)))
                .Select(s => new SolicitacaoDto(s.Id, s.ProdutoAvulsoId, s.ProdutoNome, s.Quantidade, Proporcional.Arredondar(s.Preco * s.Quantidade), s.Motivo, s.Status,
                    new PessoaDto(s.SolicitanteId, s.SolicitanteNome), s.DecididoPor.HasValue ? new PessoaDto(s.DecididoPor.Value, s.DecididoNome) : null,
                    s.DecididoEm, s.DecisaoMotivo, s.IdCompra, s.CreatedAt)).ToList();

        public async Task<DecisaoDto> DecidirAsync(Guid loja, Guid solicitacaoId, DecisaoRequest r, int? usuarioId, CancellationToken ct = default)
        {
            var motivo = string.IsNullOrWhiteSpace(r.Motivo) ? null : r.Motivo.Trim();
            if (motivo is { Length: > 300 }) motivo = motivo[..300];
            await using var cx = await _db.OpenConnectionAsync(ct);
            (long Produto, int Quantidade, string Status, Guid? Compra) s;
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                var linha = await cx.QueryFirstOrDefaultAsync<(long, int, string, Guid?)?>(new CommandDefinition(
                    "SELECT produto_avulso_id, quantidade, status, id_compra FROM solicitacao_compra WHERE id = @Id AND id_estabelecimento = @Loja FOR UPDATE",
                    new { Id = solicitacaoId, Loja = loja }, tx, cancellationToken: ct));
                if (linha == null) throw PagamentoException.NaoEncontrado("Solicitacao nao encontrada.");
                s = linha.Value;
                if (s.Status != "pendente")
                {
                    // Repetir a mesma decisao (duplo clique) devolve o resultado; decidir o contrario e conflito.
                    if ((s.Status == "aprovada") == r.Aprovar)
                    {
                        await tx.CommitAsync(ct);
                        var lista = await SolicitacoesInternoAsync(cx, loja, ct);
                        return new DecisaoDto(lista.First(x => x.Id == solicitacaoId), s.Compra.HasValue ? await CompraCriadaAsync(cx, s.Compra.Value, true, ct) : null);
                    }
                    throw new PagamentoException(AssinaturaCodigos.JaDecidida, 409, "Esta solicitacao ja foi decidida.");
                }
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE solicitacao_compra SET status = @S, decidido_por = @U, decidido_em = NOW(), decisao_motivo = @M, updated_at = NOW() WHERE id = @Id",
                    new { S = r.Aprovar ? "aprovada" : "recusada", U = usuarioId, M = motivo, Id = solicitacaoId }, tx, cancellationToken: ct));
                await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "solicitacao_compra", solicitacaoId.ToString(), r.Aprovar ? "aprovada" : "recusada", "usuario", usuarioId,
                    new { motivo }, ct);
                await tx.CommitAsync(ct);
            }
            CompraCriadaDto? compra = null;
            if (r.Aprovar)
            {
                compra = await ComprarAsync(loja, new CompraRequest { ProdutoAvulsoId = s.Produto, Quantidade = s.Quantidade, IdempotencyKey = $"solicitacao:{solicitacaoId:N}" },
                    usuarioId, solicitacaoId, ct);
                await cx.ExecuteAsync(new CommandDefinition("UPDATE solicitacao_compra SET id_compra = @C WHERE id = @Id", new { C = compra.Compra.Id, Id = solicitacaoId },
                    cancellationToken: ct));
            }
            var final = await SolicitacoesInternoAsync(cx, loja, ct);
            return new DecisaoDto(final.First(x => x.Id == solicitacaoId), compra);
        }

        public async Task MarcarAvisosLidosAsync(Guid loja, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition("UPDATE comercial_aviso SET lido_em = NOW() WHERE id_estabelecimento = @Loja AND lido_em IS NULL",
                new { Loja = loja }, cancellationToken: ct));
        }

        public async Task<CobrancaDto> BaixaManualAsync(Guid cobrancaId, BaixaManualRequest r, int? usuarioId, CancellationToken ct = default)
        {
            var motivo = string.IsNullOrWhiteSpace(r.Motivo) ? null : r.Motivo.Trim();
            if (motivo == null) throw PagamentoException.Invalida("Diga como o pagamento foi recebido.", "motivo");
            await using var cx = await _db.OpenConnectionAsync(ct);
            Guid loja;
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                var c = await AssinaturaSql.CobrancaAsync(cx, tx, cobrancaId, true, ct) ?? throw PagamentoException.NaoEncontrado("Cobranca nao encontrada.");
                if (c.Status == StatusCobrancaComercial.Paga) throw new PagamentoException(PagamentoCodigos.JaPago, 409, "Esta cobranca ja foi paga.");
                if (c.Status == StatusCobrancaComercial.Cancelada) throw PagamentoException.Estado("Esta cobranca foi cancelada.", c.Status);
                loja = c.IdEstabelecimento;
                await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "comercial_cobranca", c.Id.ToString(), "baixa_manual", "usuario", usuarioId, new { motivo, c.Valor }, ct);
                await AssinaturaSql.ConfirmarPagamentoAsync(cx, tx, c.Id, c.Valor, "manual", null, null, "usuario", usuarioId, ct);
                await tx.CommitAsync(ct);
            }
            // Pix ainda aberto desta cobranca: cancelado no MP (se for pago mesmo assim, vira excedente).
            var abertos = await cx.QueryAsync<Guid>(new CommandDefinition(
                "SELECT id FROM pagamento_intent WHERE origem_ref_id = @C AND status IN ('criado', 'aguardando_pagamento')", new { C = cobrancaId }, cancellationToken: ct));
            await CancelarIntentsAsync(abertos, loja, usuarioId, ct);
            _cache.Remove(SituacaoComercialService.CacheKey(loja));
            var politica = await AssinaturaSql.PoliticaAsync(cx, ct);
            var linha = (await AssinaturaSql.CobrancaAsync(cx, null, cobrancaId, false, ct))!;
            return Dto(linha, (await ComprovantesAsync(cx, new[] { linha }, ct)).GetValueOrDefault(cobrancaId), politica);
        }
    }
}
