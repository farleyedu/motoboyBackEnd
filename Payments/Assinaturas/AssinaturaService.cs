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
        Task<AssinaturaDto> DesfazerTrocaAsync(Guid loja, int? usuarioId, CancellationToken ct = default);
        /// <summary>Liga ou troca o cartão com cobrança automática.</summary>
        Task<AssinaturaDto> LigarCartaoAsync(Guid loja, CartaoAutomaticoRequest r, int? usuarioId, CancellationToken ct = default);
        Task<AssinaturaDto> DesligarCartaoAsync(Guid loja, int? usuarioId, CancellationToken ct = default);
        /// <summary>A assinatura viva (ou a última encerrada), para as respostas.</summary>
        Task<AssinaturaDto> AssinaturaAsync(Guid loja, CancellationToken ct = default);
        Task<CobrancaDto> CobrancaAsync(Guid cobrancaId, CancellationToken ct = default);
        Task<CompraDto> CompraAsync(Guid compraId, CancellationToken ct = default);
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
        private readonly APIBack.Payments.InfinitePay.Checkout.IInfinitePayCheckoutService? _link;

        public AssinaturaService(NpgsqlDataSource db, IPixChargeService pix, ICardPaymentService cartao, IMercadoPagoCredencialResolver credenciais,
            IMercadoPagoPreapprovalClient preapproval, IMemoryCache cache, IConfiguration config, ILogger<AssinaturaService> logger,
            APIBack.Payments.InfinitePay.Checkout.IInfinitePayCheckoutService? link = null)
        {
            _link = link;
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
            await AtualizarAntesDeLerAsync(loja, ct);
            await using var cx = await _db.OpenConnectionAsync(ct);
            var agora = DateTimeOffset.UtcNow;
            var dados = await AssinaturaConsulta.UmaAsync(cx, loja, agora, ct);
            var a = dados.Assinatura is { Viva: true } viva ? viva : null;
            var planos = await PlanosAsync(cx, ct);
            var situ = await cx.QueryFirstOrDefaultAsync<(string? Situacao, DateTimeOffset? Fim)>(new CommandDefinition(
                "SELECT situacao_comercial, teste_termina_em FROM estabelecimentos WHERE id = @Loja", new { Loja = loja }, cancellationToken: ct));
            var extras = (await cx.QueryAsync<(long Id, string Nome, decimal Preco, int Motoboys, string[] Modulos, string? Cor)>(new CommandDefinition(
                "SELECT id, nome, preco, motoboys, modulos_ativos::text[], cor FROM produtos_avulsos WHERE NOT arquivado AND preco > 0 ORDER BY preco, id",
                cancellationToken: ct))).Select(x => new ExtraDto(x.Id, x.Nome, x.Preco, x.Motoboys, x.Modulos, x.Cor)).ToList();
            var compras = await ComprasAsync(cx, loja, ct);
            var recente = agora.AddDays(-30);
            var solicitacoes = (await SolicitacoesInternoAsync(cx, loja, ct))
                .Where(s => s.Status == "pendente" || (s.DecididoEm ?? s.CriadaEm) >= recente).ToList();
            var avisos = (await cx.QueryAsync<AvisoDto>(new CommandDefinition(@"
SELECT id, tipo, mensagem, criado_em, lido_em IS NOT NULL AS lido FROM comercial_aviso
 WHERE id_estabelecimento = @Loja AND criado_em > NOW() - INTERVAL '45 days' ORDER BY criado_em DESC, id DESC LIMIT 20",
                new { Loja = loja }, cancellationToken: ct))).ToList();
            var publicKey = await cx.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT public_key FROM mp_conta_zippy WHERE ambiente = @Amb AND estado = 'ativa'", new { Amb = _credenciais.AmbienteAtivo }, cancellationToken: ct));
            // Guarda para nao quebrar o painel (Mercado Pago) se a migration da InfinitePay da Zippy ainda nao foi aplicada.
            var linkDisponivel = false;
            if (_link != null && await cx.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT to_regclass('infinitepay_conta_zippy') IS NOT NULL", cancellationToken: ct)))
                linkDisponivel = await cx.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT EXISTS (SELECT 1 FROM infinitepay_conta_zippy c JOIN infinitepay_conta_zippy_versao v ON v.id_conta = c.id AND v.ativa WHERE c.habilitada)", cancellationToken: ct));
            var dias = situ.Situacao == "teste" ? ProntidaoRegras.DiasRestantes(situ.Fim, DateTimeOffset.UtcNow) : null;

            return new PainelAssinaturaDto(
                a == null ? null : Montar(dados, a, planos),
                new SituacaoResumoDto(situ.Situacao ?? SituacoesComerciais.Ativo, situ.Fim, dias),
                planos.Where(p => !p.Arquivado || p.Chave == a?.PlanoChave).Select(p => new PlanoDto(p.Chave, p.Nome, p.Preco, p.PrecoDe(Periodicidades.Anual), p.Cor,
                    p.Icone, p.Frase, p.ModulosAtivos, p.MotoboysBase, p.Chave == a?.PlanoChave)).ToList(),
                extras, compras, solicitacoes, avisos,
                new CheckoutDto(publicKey, await PixDisponivelAsync(cx, ct), !string.IsNullOrEmpty(publicKey), _credenciais.AmbienteAtivo, linkDisponivel),
                podeGerenciar, 0, dados.Situacao.Estado, dados.Situacao.Acoes, dados.Situacao.MesVigente, dados.Divida,
                // Cartão de assinatura encerrada não existe mais para a loja.
                new CartaoAssinaturaDto(a?.RenovacaoAutomatica ?? false, a?.CartaoBandeira, a?.CartaoFinal), dados.Bloqueio);
        }

        /// <summary>Pix só aparece com a conta da Zippy ativa e sem a última verificação dizer que ela não tem chave Pix.</summary>
        private async Task<bool> PixDisponivelAsync(NpgsqlConnection cx, CancellationToken ct)
        {
            var amb = _credenciais.AmbienteAtivo;
            if (!await cx.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS (SELECT 1 FROM mp_conta_zippy WHERE ambiente = @Amb AND estado = 'ativa')", new { Amb = amb }, cancellationToken: ct)))
                return false;
            if (!await cx.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT to_regclass('mp_verificacao') IS NOT NULL", cancellationToken: ct)))
                return true;
            return await cx.ExecuteScalarAsync<bool?>(new CommandDefinition(@"
SELECT NOT EXISTS (SELECT 1 FROM jsonb_array_elements(v.etapas) e
                    WHERE COALESCE(e ->> 'etapa', e ->> 'Etapa') = 'recebedor_pix' AND COALESCE(e ->> 'ok', e ->> 'Ok') = 'false'
                      AND COALESCE(e ->> 'codigo', e ->> 'Codigo') = 'pix_indisponivel')
  FROM mp_verificacao v WHERE v.ambiente = @Amb ORDER BY v.created_at DESC LIMIT 1", new { Amb = amb }, cancellationToken: ct)) ?? true;
        }

        /// <summary>O que a rotina faria daqui a pouco é feito antes de responder: tentativa vencida some, cancelada no fim encerra.</summary>
        private async Task AtualizarAntesDeLerAsync(Guid loja, CancellationToken ct)
        {
            var agora = DateTimeOffset.UtcNow;
            await AssinaturaOperacoes.ExpirarTentativasAsync(_db, _pix, _logger, loja, agora, ct);
            await using var cx = await _db.OpenConnectionAsync(ct);
            await EncerrarCanceladaVencidaAsync(cx, loja, agora, ct);
            await AssinaturaOperacoes.GarantirPrimeiroCicloAsync(cx, loja, ct);
        }

        /// <summary>Assinatura cancelada cujo período acabou não pode seguir "viva": ela impediria a loja de assinar de novo.</summary>
        private async Task EncerrarCanceladaVencidaAsync(NpgsqlConnection cx, Guid loja, DateTimeOffset agora, CancellationToken ct)
        {
            // Mais de um período pago à frente é consumido um por volta.
            for (var volta = 0; volta < 3; volta++)
            {
                var a = await AssinaturaSql.VivaAsync(cx, null, loja, false, ct);
                if (a is not { Status: StatusAssinatura.Ativa, CancelamentoSolicitadoEm: not null, PeriodoFim: { } fim } || fim > agora) return;
                await using var tx = await cx.BeginTransactionAsync(ct);
                var mudou = await AssinaturaOperacoes.VirarCicloAsync(cx, tx, a.Id, agora, ct);
                await tx.CommitAsync(ct);
                if (!mudou) return;
                _cache.Remove(SituacaoComercialService.CacheKey(loja));
            }
        }

        private static async Task<List<PlanoCatalogoRow>> PlanosAsync(NpgsqlConnection cx, CancellationToken ct) =>
            (await cx.QueryAsync<PlanoCatalogoRow>(new CommandDefinition(
                $"SELECT {AssinaturaSql.ColunasPlano} FROM pacotes_comerciais ORDER BY ordem, preco", cancellationToken: ct))).ToList();

        public async Task<IReadOnlyList<CobrancaDto>> CobrancasAsync(Guid loja, CancellationToken ct = default)
        {
            await AssinaturaOperacoes.ExpirarTentativasAsync(_db, _pix, _logger, loja, DateTimeOffset.UtcNow, ct);
            await using var cx = await _db.OpenConnectionAsync(ct);
            var linhas = (await cx.QueryAsync<CobrancaRow>(new CommandDefinition(
                $"SELECT {AssinaturaSql.ColunasCobranca} FROM comercial_cobranca WHERE id_estabelecimento = @Loja ORDER BY vencimento DESC, created_at DESC LIMIT 200",
                new { Loja = loja }, cancellationToken: ct))).ToList();
            var comprovantes = await ComprovantesAsync(cx, linhas, ct);
            return linhas.Select(c => Dto(c, comprovantes.GetValueOrDefault(c.Id))).ToList();
        }

        public async Task<CobrancaDto> CobrancaAsync(Guid cobrancaId, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var linha = await AssinaturaSql.CobrancaAsync(cx, null, cobrancaId, false, ct) ?? throw PagamentoException.NaoEncontrado("Cobrança não encontrada.");
            return Dto(linha, (await ComprovantesAsync(cx, new[] { linha }, ct)).GetValueOrDefault(cobrancaId));
        }

        private static AssinaturaDto Montar(AssinaturaDaLoja dados, AssinaturaRow a, IReadOnlyList<PlanoCatalogoRow> planos)
        {
            var plano = planos.FirstOrDefault(p => p.Chave == a.PlanoChave);
            var divida = dados.Divida;
            // A única cobrança a pagar agora: a dívida mais antiga; sem dívida, a mensalidade em aberto; por fim, uma tentativa.
            var proxima = (divida == null ? null : dados.Abertas.FirstOrDefault(c => c.Id == divida.Cobrancas[0]))
                ?? dados.Abertas.FirstOrDefault(c => c.Tipo == TiposCobranca.Ciclo)
                ?? dados.Abertas.FirstOrDefault(c => c.IdAssinatura == a.Id);
            var hoje = FusoComercial.Data(DateTimeOffset.UtcNow);
            var nomeMudanca = a.MudancaPlano == null ? null : planos.FirstOrDefault(p => p.Chave == a.MudancaPlano)?.Nome ?? a.MudancaPlano;
            return new AssinaturaDto(a.Id, new PlanoResumoDto(a.PlanoChave, plano?.Nome ?? a.PlanoChave, plano?.Cor, plano?.Icone), a.Periodicidade, a.Preco, a.Status,
                divida == null ? EstadoFinanceiro.EmDia : EstadoFinanceiro.EmAtraso, divida == null ? "liberado" : "tolerancia",
                a.PeriodoInicio, a.PeriodoFim, proxima?.Vencimento, proxima == null ? null : Dto(proxima, null),
                a.Metodo, a.RenovacaoAutomatica, a.CartaoFinal, a.CartaoBandeira, a.MpPreapprovalStatus,
                a.MudancaPlano == null || dados.TrocaEm == null ? null
                    : new MudancaAgendadaDto(a.MudancaPlano, nomeMudanca!, a.MudancaPeriodicidade ?? a.Periodicidade, a.MudancaPreco ?? 0, dados.TrocaEm.Value),
                a.CancelamentoSolicitadoEm == null ? null : new CancelamentoDto(a.CancelamentoSolicitadoEm.Value, a.CancelamentoMotivo, dados.AcessoAte),
                divida == null ? null
                    : new InadimplenciaDto(divida.Desde, Math.Max(0, hoje.DayNumber - FusoComercial.Data(divida.Desde).DayNumber), 0, null, null, divida.Valor),
                a.Versao, a.CreatedAt);
        }

        private static CobrancaDto Dto(CobrancaRow c, ComprovanteDto? comprovante)
        {
            var aberta = StatusCobrancaComercial.EmAberto(c.Status);
            // Só mensalidade atrasa: tentativa de upgrade ou extra nunca é dívida.
            var divida = aberta && c.Tipo == TiposCobranca.Ciclo ? Divida.De(new[] { c.Mensalidade() }, DateTimeOffset.UtcNow) : null;
            var dias = divida == null ? 0 : Math.Max(0, FusoComercial.Data(DateTimeOffset.UtcNow).DayNumber - FusoComercial.Data(divida.Desde).DayNumber);
            object? detalhe = null;
            if (!string.IsNullOrEmpty(c.Detalhe))
            {
                try { detalhe = JsonSerializer.Deserialize<JsonElement>(c.Detalhe); } catch (JsonException) { }
            }
            return new CobrancaDto(c.Id, c.Tipo, c.Descricao, c.Valor, c.Vencimento, c.Status, c.Meio, c.PagoEm, c.PagoVia, c.ValorPago, c.ValorExcedente,
                divida != null, dias, aberta, c.IdIntentAtual, comprovante, detalhe, c.CreatedAt, aberta ? c.ExpiraEm : null, divida != null);
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

        private const string SqlCompra = @"
SELECT ce.id, ce.produto_avulso_id, ce.nome, ce.quantidade, ce.preco_unitario, ce.valor_total, ce.motoboys, ce.status, ce.vigencia_fim,
       (SELECT c.id FROM comercial_cobranca c WHERE c.id_compra = ce.id LIMIT 1) AS cobranca_id, ce.created_at AS criada_em
  FROM compra_extra ce";

        private static async Task<List<CompraDto>> ComprasAsync(NpgsqlConnection cx, Guid loja, CancellationToken ct) =>
            (await cx.QueryAsync<CompraDto>(new CommandDefinition(SqlCompra + @"
 WHERE ce.id_estabelecimento = @Loja AND (ce.status IN ('aguardando_pagamento', 'ativa') OR ce.created_at > NOW() - INTERVAL '90 days')
 ORDER BY ce.created_at DESC LIMIT 30", new { Loja = loja }, cancellationToken: ct))).ToList();

        public async Task<CompraDto> CompraAsync(Guid compraId, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.QueryFirstAsync<CompraDto>(new CommandDefinition(SqlCompra + " WHERE ce.id = @Id", new { Id = compraId }, cancellationToken: ct));
        }

        // ---------- contratar ----------

        public async Task<AssinaturaDto> ContratarAsync(Guid loja, ContratarRequest r, int? usuarioId, CancellationToken ct = default)
        {
            var chavePlano = (r.Plano ?? string.Empty).Trim().ToLowerInvariant();
            var periodicidade = (r.Periodicidade ?? Periodicidades.Mensal).Trim().ToLowerInvariant();
            if (!Periodicidades.Valida(periodicidade)) throw PagamentoException.Invalida("Escolha mensal ou anual.", "periodicidade");
            var chave = string.IsNullOrWhiteSpace(r.IdempotencyKey) ? null : r.IdempotencyKey.Trim();
            if (chave is { Length: > 160 }) throw PagamentoException.Invalida("Chave de idempotência com até 160 caracteres.", "idempotencyKey");

            await using var cx = await _db.OpenConnectionAsync(ct);
            await EncerrarCanceladaVencidaAsync(cx, loja, DateTimeOffset.UtcNow, ct);
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                await cx.ExecuteAsync(new CommandDefinition("SELECT 1 FROM estabelecimentos WHERE id = @Loja FOR UPDATE", new { Loja = loja }, tx, cancellationToken: ct));
                var viva = await AssinaturaSql.VivaAsync(cx, tx, loja, true, ct);
                var daChave = chave == null ? null : await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                    "SELECT id FROM assinatura WHERE id_estabelecimento = @Loja AND idempotency_key = @K", new { Loja = loja, K = chave }, tx, cancellationToken: ct));
                if (viva != null)
                {
                    if (daChave == viva.Id)
                    {
                        await tx.CommitAsync(ct);
                        return await AssinaturaAsync(loja, ct);
                    }
                    throw new PagamentoException(AssinaturaCodigos.JaExiste, 409, "A loja já tem uma assinatura. Use a troca de plano.");
                }
                // A chave de uma assinatura que já terminou não impede assinar de novo.
                if (daChave != null) chave = null;
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
            await AssinaturaOperacoes.GarantirPrimeiroCicloAsync(cx, loja, ct);
            await LimparAsync(loja, ct);
            return await AssinaturaAsync(loja, ct);
        }

        private static async Task<PlanoCatalogoRow> PlanoValidoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, string chave, string periodicidade, CancellationToken ct)
        {
            var plano = chave.Length == 0 || chave == "personalizado" ? null : await AssinaturaSql.PlanoAsync(cx, tx, chave, ct);
            if (plano == null || plano.Arquivado) throw PagamentoException.Regra(AssinaturaCodigos.PlanoInvalido, "Escolha um plano do catálogo.");
            var preco = plano.PrecoDe(periodicidade);
            if (preco is null or <= 0)
                throw PagamentoException.Regra(AssinaturaCodigos.PeriodicidadeIndisponivel, $"O plano {plano.Nome} não tem opção {(periodicidade == Periodicidades.Anual ? "anual" : "mensal")}.");
            return plano;
        }

        public async Task<AssinaturaDto> AssinaturaAsync(Guid loja, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var dados = await AssinaturaConsulta.UmaAsync(cx, loja, DateTimeOffset.UtcNow, ct);
            var a = dados.Assinatura ?? throw new PagamentoException(AssinaturaCodigos.SemAssinatura, 404, "A loja não tem assinatura.");
            return Montar(dados, a, await PlanosAsync(cx, ct));
        }

        /// <summary>Depois do commit: derruba o Pix das cobranças canceladas e o cache da situação.</summary>
        private async Task LimparAsync(Guid loja, CancellationToken ct)
        {
            await AssinaturaOperacoes.CancelarPagamentosDeCanceladasAsync(_db, _pix, _logger, loja, ct);
            _cache.Remove(SituacaoComercialService.CacheKey(loja));
        }

        /// <summary>A regra das tabelas, conferida dentro da transação com a assinatura travada.</summary>
        private static async Task ExigirAsync(NpgsqlConnection cx, Guid loja, Func<FatosAssinatura, Recusa?> regra, CancellationToken ct)
        {
            var dados = await AssinaturaConsulta.UmaAsync(cx, loja, DateTimeOffset.UtcNow, ct);
            if (regra(dados.Fatos) is { } r) throw Recusar(r, dados.Divida);
        }

        private static PagamentoException Recusar(Recusa r, DividaDto? divida) =>
            new(r.Codigo, r.Codigo == AssinaturaCodigos.SemAssinatura ? 404 : 409, r.Motivo,
                divida == null ? null : new Dictionary<string, object?> { ["divida"] = divida });

        private static async Task<AssinaturaRow> VivaOuErroAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid loja, CancellationToken ct) =>
            await AssinaturaSql.VivaAsync(cx, tx, loja, true, ct) ?? throw new PagamentoException(AssinaturaCodigos.SemAssinatura, 404, "A loja não tem assinatura.");

        // ---------- alterar ----------

        public async Task<AlteracaoDto> AlterarAsync(Guid loja, AlterarRequest r, int? usuarioId, CancellationToken ct = default)
        {
            var chavePlano = (r.Plano ?? string.Empty).Trim().ToLowerInvariant();
            await using var cx = await _db.OpenConnectionAsync(ct);
            await EncerrarCanceladaVencidaAsync(cx, loja, DateTimeOffset.UtcNow, ct);
            PrevisaoMudanca previsao;
            CobrancaDto? cobranca = null;
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                var a = await VivaOuErroAsync(cx, tx, loja, ct);
                var periodicidade = (r.Periodicidade ?? a.Periodicidade).Trim().ToLowerInvariant();
                if (!Periodicidades.Valida(periodicidade)) throw PagamentoException.Invalida("Escolha mensal ou anual.", "periodicidade");
                var plano = await PlanoValidoAsync(cx, tx, chavePlano, periodicidade, ct);
                var precoNovo = plano.PrecoDe(periodicidade)!.Value;
                var dados = await AssinaturaConsulta.UmaAsync(cx, loja, DateTimeOffset.UtcNow, ct);
                if (RegrasDeAcesso.Trocar(dados.Fatos) is { } recusa)
                {
                    // Anual valendo para mensal é só pela Gestão, mesmo com dívida: pagar não libera essa troca.
                    if (recusa.Codigo == AssinaturaCodigos.Devendo && periodicidade == Periodicidades.Mensal && RegrasDeAcesso.AnualValendo(dados.Fatos))
                        recusa = RegrasDeAcesso.AnualSoGestao();
                    throw Recusar(recusa, dados.Divida);
                }
                if (periodicidade == Periodicidades.Mensal && RegrasDeAcesso.AnualValendo(dados.Fatos))
                    throw Recusar(RegrasDeAcesso.AnualSoGestao(), null);
                var hoje = FusoComercial.Data(DateTimeOffset.UtcNow);

                // Antes do primeiro pagamento: troca livre, sem proporcional. Muda o valor e mantém o vencimento.
                if (a.Status == StatusAssinatura.Pendente)
                {
                    if (plano.Chave == a.PlanoChave && periodicidade == a.Periodicidade)
                        throw new PagamentoException(AssinaturaCodigos.SemMudanca, 409, "Este já é o seu plano.");
                    previsao = new PrevisaoMudanca(precoNovo < a.Preco ? TiposMudanca.Downgrade : TiposMudanca.Upgrade, a.Preco, precoNovo, 0, 0, 0, 0, "agora", hoje,
                        new[] { "Você ainda não pagou: a fatura passa a ter o valor do plano novo, com o mesmo vencimento." });
                    if (!r.Confirmar) { await tx.CommitAsync(ct); return new AlteracaoDto(previsao, false, null, null); }
                    await AssinaturaOperacoes.TrocarPendenteAsync(cx, tx, a, plano.Chave, periodicidade, precoNovo, usuarioId, ct);
                    await tx.CommitAsync(ct);
                }
                else
                {
                    previsao = await PreverAsync(cx, tx, a, plano, periodicidade, precoNovo, ct);
                    if (previsao.Tipo == TiposMudanca.Nenhuma)
                    {
                        // Voltar ao plano atual desfaz um downgrade agendado.
                        if (a.MudancaPlano == null || !r.Confirmar) throw new PagamentoException(AssinaturaCodigos.SemMudanca, 409, "Este já é o seu plano.");
                        await AssinaturaOperacoes.DesfazerMudancaAsync(cx, tx, a, usuarioId, ct);
                        await tx.CommitAsync(ct);
                    }
                    else
                    {
                        if (!r.Confirmar) { await tx.CommitAsync(ct); return new AlteracaoDto(previsao, false, null, null); }
                        if (r.ValorPrevisto.HasValue && r.ValorPrevisto.Value != previsao.ValorAgora)
                            throw new PagamentoException(AssinaturaCodigos.PrevisaoDesatualizada, 409, "O valor mudou desde a previsão. Confira a conta nova.",
                                new Dictionary<string, object?> { ["previsao"] = previsao });

                        // Uma troca pendente por vez: a nova substitui a anterior.
                        await AssinaturaOperacoes.CancelarTentativasDeUpgradeAsync(cx, tx, a.Id, MotivosCancelamentoCobranca.Substituida, usuarioId, ct);
                        if (previsao.Tipo == TiposMudanca.Downgrade)
                            await AssinaturaOperacoes.AgendarMudancaAsync(cx, tx, a, plano.Chave, periodicidade, precoNovo, usuarioId, ct);
                        else if (previsao.ValorAgora <= 0)
                            await AssinaturaOperacoes.AplicarMudancaAgoraAsync(cx, tx, a, previsao.Tipo, plano.Chave, periodicidade, precoNovo, "usuario", usuarioId, ct);
                        else
                        {
                            var id = await CriarUpgradeAsync(cx, tx, a, plano, periodicidade, precoNovo, previsao, DateTimeOffset.UtcNow.Add(ValidadeDaTentativa),
                                usuarioId, ct);
                            cobranca = Dto((await AssinaturaSql.CobrancaAsync(cx, tx, id, false, ct))!, null);
                        }
                        await tx.CommitAsync(ct);
                    }
                }
            }
            await LimparAsync(loja, ct);
            return new AlteracaoDto(previsao, true, cobranca, await AssinaturaAsync(loja, ct));
        }

        /// <summary>Upgrade e extra não pagos somem junto com o Pix.</summary>
        public static readonly TimeSpan ValidadeDaTentativa = TimeSpan.FromHours(1);

        /// <summary>A conta da troca de uma assinatura ativa, olhando o ciclo atual e a próxima fatura.</summary>
        internal static async Task<PrevisaoMudanca> PreverAsync(NpgsqlConnection cx, NpgsqlTransaction tx, AssinaturaRow a, PlanoCatalogoRow? plano,
            string periodicidade, decimal precoNovo, CancellationToken ct)
        {
            var ciclo = await AssinaturaOperacoes.CicloAtualAsync(cx, tx, a, ct) ?? throw PagamentoException.Estado("Assinatura sem período vigente.", a.Status);
            var proximo = (await AssinaturaOperacoes.CiclosSeguintesAsync(cx, tx, a, ct)).FirstOrDefault();
            var mesmoPlano = (plano?.Chave ?? a.PlanoChave) == a.PlanoChave;
            var previsao = Proporcional.Calcular(a.Preco, a.Periodicidade, precoNovo, periodicidade, FusoComercial.Data(ciclo.Inicio), FusoComercial.Data(ciclo.Fim),
                FusoComercial.Data(DateTimeOffset.UtcNow), mesmoPlano, mesmoPlano ? null : plano?.Preco, ciclo.Status == "pago",
                proximo is { Quitado: true } && (proximo.Periodicidade == periodicidade || proximo.Periodicidade == Periodicidades.Mensal) ? proximo.Preco : null);
            if (previsao.Tipo != TiposMudanca.Downgrade || proximo is not { Quitado: true }) return previsao;
            // A próxima fatura já está paga: a redução vale no período seguinte a ela.
            var depois = FusoComercial.Data(proximo.Fim);
            return previsao with
            {
                AplicaEm = depois,
                Explicacao = new[]
                {
                    $"A próxima fatura já foi paga: a troca vale a partir de {depois:dd/MM/yyyy}.",
                    $"Até lá nada muda. Depois, o plano passa a custar {Proporcional.Brl(precoNovo)}. Seus dados continuam guardados."
                }
            };
        }

        /// <summary>A cobrança da diferença: o plano só muda quando ela for paga. Sem pagamento até o prazo, ela some.</summary>
        internal static async Task<Guid> CriarUpgradeAsync(NpgsqlConnection cx, NpgsqlTransaction tx, AssinaturaRow a, PlanoCatalogoRow plano, string periodicidade,
            decimal precoNovo, PrevisaoMudanca previsao, DateTimeOffset expiraEm, int? usuarioId, CancellationToken ct)
        {
            var id = Guid.NewGuid();
            var descricao = $"Troca para {plano.Nome}{(previsao.Tipo == TiposMudanca.ParaAnual ? " anual" : "")} (diferença)";
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO comercial_cobranca (id, id_estabelecimento, tipo, id_assinatura, id_ciclo, descricao, valor, vencimento, detalhe, idempotency_key, expira_em)
VALUES (@Id, @Loja, 'upgrade', @A, @C, @D, @V, NOW(), CAST(@Det AS jsonb), @K, @Expira)", new
            {
                Id = id, Loja = a.IdEstabelecimento, A = a.Id, C = a.IdCicloAtual, D = AssinaturaSql.Cortar(descricao), V = previsao.ValorAgora, Expira = expiraEm,
                Det = JsonSerializer.Serialize(new
                {
                    tipo = previsao.Tipo, planoAnterior = a.PlanoChave, planoNovo = plano.Chave, periodicidadeNova = periodicidade, precoNovo,
                    precoAtual = a.Preco, previsao.DiasRestantes, previsao.DiasCiclo, previsao.Credito, previsao.Explicacao
                }), K = $"upgrade:{id:N}"
            }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, a.IdEstabelecimento, "comercial_cobranca", id.ToString(), "upgrade_criado", "usuario", usuarioId,
                new { de = a.PlanoChave, para = plano.Chave, valor = previsao.ValorAgora }, ct);
            return id;
        }

        // ---------- cancelar / retomar / desfazer ----------

        public async Task<AssinaturaDto> CancelarAsync(Guid loja, CancelarRequest r, int? usuarioId, CancellationToken ct = default)
        {
            var motivo = string.IsNullOrWhiteSpace(r.Motivo) ? null : r.Motivo.Trim();
            if (motivo is { Length: > 300 }) motivo = motivo[..300];
            await using var cx = await _db.OpenConnectionAsync(ct);
            var agora = DateTimeOffset.UtcNow;
            await EncerrarCanceladaVencidaAsync(cx, loja, agora, ct);
            string? preapproval;
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                var a = await VivaOuErroAsync(cx, tx, loja, ct);
                if (a.CancelamentoSolicitadoEm != null) { await tx.CommitAsync(ct); return await AssinaturaAsync(loja, ct); }
                preapproval = PreapprovalVivo(a);
                // Nunca paga: encerra já (o que venceu continua devido). Paga: vale até o fim do período, mesmo devendo.
                if (a.Status == StatusAssinatura.Pendente)
                    await AssinaturaOperacoes.EncerrarAgoraAsync(cx, tx, a, Encerramentos.AntesDoPagamento, motivo, usuarioId, agora, ct);
                else
                    await AssinaturaOperacoes.CancelarNoFimAsync(cx, tx, a, motivo, usuarioId, agora, ct);
                await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "assinatura", a.Id.ToString(), "cancelamento", "usuario", usuarioId,
                    new { motivo, acessoAte = a.PeriodoFim, status = a.Status }, ct);
                await tx.CommitAsync(ct);
            }
            await LimparAsync(loja, ct);
            if (preapproval != null) await CancelarPreapprovalAsync(preapproval, ct);
            return await AssinaturaAsync(loja, ct);
        }

        private static string? PreapprovalVivo(AssinaturaRow a) => a.MpPreapprovalStatus is "authorized" or "pending" or "paused" ? a.MpPreapprovalId : null;

        /// <summary>Cancela no MP; se falhar, o worker tenta de novo (a renovação já está desligada aqui).</summary>
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
            await EncerrarCanceladaVencidaAsync(cx, loja, DateTimeOffset.UtcNow, ct);
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                var a = await VivaOuErroAsync(cx, tx, loja, ct);
                await ExigirAsync(cx, loja, RegrasDeAcesso.Retomar, ct);
                await AssinaturaOperacoes.RetomarAsync(cx, tx, a, usuarioId, DateTimeOffset.UtcNow, ct);
                await tx.CommitAsync(ct);
            }
            await LimparAsync(loja, ct);
            return await AssinaturaAsync(loja, ct);
        }

        public async Task<AssinaturaDto> DesfazerTrocaAsync(Guid loja, int? usuarioId, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                var a = await VivaOuErroAsync(cx, tx, loja, ct);
                await ExigirAsync(cx, loja, RegrasDeAcesso.Desfazer, ct);
                await AssinaturaOperacoes.DesfazerMudancaAsync(cx, tx, a, usuarioId, ct);
                await tx.CommitAsync(ct);
            }
            await LimparAsync(loja, ct);
            return await AssinaturaAsync(loja, ct);
        }

        // ---------- pagar ----------

        public async Task<CheckoutCobrancaDto> PagarAsync(Guid loja, Guid cobrancaId, PagarCobrancaRequest r, int? usuarioId, CancellationToken ct = default)
        {
            var metodo = (r.Metodo ?? string.Empty).Trim().ToLowerInvariant();
            if (metodo is not (MetodosPagamento.Pix or MetodosPagamento.Cartao or MetodosPagamento.Link)) throw PagamentoException.Invalida("Escolha Pix, cartão ou link.", "metodo");
            await AssinaturaOperacoes.ExpirarTentativasAsync(_db, _pix, _logger, loja, DateTimeOffset.UtcNow, ct);
            await using var cx = await _db.OpenConnectionAsync(ct);
            var c = await AssinaturaSql.CobrancaAsync(cx, null, cobrancaId, false, ct);
            if (c == null || c.IdEstabelecimento != loja) throw PagamentoException.NaoEncontrado("Cobrança não encontrada.");
            if (c.Status == StatusCobrancaComercial.Paga) throw new PagamentoException(PagamentoCodigos.JaPago, 409, "Esta cobrança já foi paga.");
            if (c.Status == StatusCobrancaComercial.Cancelada)
            {
                if (c.CancelamentoMotivo == MotivosCancelamentoCobranca.Expirada)
                    throw new PagamentoException(AssinaturaCodigos.TentativaExpirada, 409, "Essa tentativa expirou. Peça de novo.");
                throw PagamentoException.Estado("Esta cobrança foi cancelada.", c.Status);
            }
            // O MP esta cobrando o cartao (e repete as tentativas sozinho): nao duplicar. Depois de vencida, Pix fica liberado para regularizar.
            if (c.Status == StatusCobrancaComercial.Processando && c.Meio == "cartao_recorrente" && FusoComercial.Data(DateTimeOffset.UtcNow) <= FusoComercial.Data(c.Vencimento))
                throw new PagamentoException(AssinaturaCodigos.CobrancaNoCartao, 409, "Esta fatura será cobrada no cartão cadastrado pelo Mercado Pago.");

            var origem = c.Tipo == TiposCobranca.CompraExtra ? OrigensPagamento.CompraExtra : OrigensPagamento.Assinatura;
            var prefixo = $"{origem}:{c.Id:N}";
            if (metodo == MetodosPagamento.Pix) return await PagarPixAsync(cx, c, origem, prefixo, usuarioId, ct);
            if (metodo == MetodosPagamento.Link) return await PagarLinkAsync(cx, c, origem, prefixo, usuarioId, ct);
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
            throw PagamentoException.Estado("Muitas tentativas para esta cobrança. Fale com a Zippy.");
        }

        private async Task<CheckoutCobrancaDto> PagarPixAsync(NpgsqlConnection cx, CobrancaRow c, string origem, string prefixo, int? usuarioId, CancellationToken ct)
        {
            var cartaoEmAnalise = await cx.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM pagamento_intent WHERE origem_ref_id = @C AND status = 'processando')", new { C = c.Id }, cancellationToken: ct));
            if (cartaoEmAnalise) throw PagamentoException.Estado("Há um pagamento com cartão em análise para esta cobrança.", "processando");

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
            // A tentativa de upgrade ou extra vive enquanto o Pix dela viver.
            var expira = pix.ExpiraEm.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(pix.ExpiraEm.Value, DateTimeKind.Utc)) : (DateTimeOffset?)null;
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE comercial_cobranca SET id_intent_atual = @I, meio = 'pix',
       expira_em = CASE WHEN tipo = 'ciclo' THEN NULL ELSE GREATEST(expira_em, @Expira) END, versao = versao + 1, updated_at = NOW()
 WHERE id = @Id AND status IN ('aberta', 'processando')", new { I = pix.IntentId, c.Id, Expira = expira }, cancellationToken: ct));
            var atual = await AssinaturaSql.CobrancaAsync(cx, null, c.Id, false, ct);
            return new CheckoutCobrancaDto(c.Id, MetodosPagamento.Pix, atual?.Status ?? c.Status, c.Valor,
                new PixCheckoutDto(pix.IntentId, pix.Status, pix.CopiaECola, pix.QrCodeBase64, pix.TicketUrl, pix.ExpiraEm, pix.Reaproveitado), null, null);
        }

        /// <summary>
        /// Link hospedado da InfinitePay na conta da ZIPPY. Sem debito automatico: a InfinitePay nao tem recorrencia, entao cada ciclo e uma
        /// cobranca nova com link novo. O acesso so muda quando o webhook confirma (webhook + /payment_check), nunca pela volta do cliente.
        /// </summary>
        private async Task<CheckoutCobrancaDto> PagarLinkAsync(NpgsqlConnection cx, CobrancaRow c, string origem, string prefixo, int? usuarioId, CancellationToken ct)
        {
            if (_link == null) throw PagamentoException.Regra(APIBack.Payments.Provedores.ProvedorPagamentoCodigos.ProvedorNaoConfigurado, "Pagamento por link indisponível.");
            var emAnalise = await cx.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM pagamento_intent WHERE origem_ref_id = @C AND status = 'processando')", new { C = c.Id }, cancellationToken: ct));
            if (emAnalise) throw PagamentoException.Estado("Há um pagamento em análise para esta cobrança.", "processando");

            var chave = await ChaveEmissaoAsync(cx, c, prefixo + ":link", ct);
            var res = await _link.CriarAsync(new APIBack.Payments.InfinitePay.Checkout.InfinitePayCheckoutRequest
            {
                IdEstabelecimento = c.IdEstabelecimento, Origem = origem, OrigemRefId = c.Id, Valor = c.Valor, Descricao = c.Descricao, IdempotencyKey = chave,
                CriadoPor = usuarioId,
                Itens = new System.Collections.Generic.List<APIBack.Payments.InfinitePay.Cliente.InfinitePayItem>
                {
                    new() { Description = c.Descricao, Price = APIBack.Payments.InfinitePay.Cliente.InfinitePayRegras.Centavos(c.Valor), Quantity = 1 }
                }
            }, ct);
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE comercial_cobranca SET id_intent_atual = @I, meio = 'link', versao = versao + 1, updated_at = NOW() WHERE id = @Id AND status IN ('aberta', 'processando')",
                new { I = res.IntentId, c.Id }, cancellationToken: ct));
            var atual = await AssinaturaSql.CobrancaAsync(cx, null, c.Id, false, ct);
            return new CheckoutCobrancaDto(c.Id, MetodosPagamento.Link, atual?.Status ?? c.Status, c.Valor, null, null, null,
                new LinkCheckoutDto(res.IntentId, res.Status, res.UrlCheckout, res.MetodosEfetivos, res.ExpiraEm, res.Reaproveitado));
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
            var k = r.Cartao ?? throw PagamentoException.Invalida("Dados do cartão obrigatórios.", "cartao");
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
                throw PagamentoException.Invalida("O cartão automático vale só para a mensalidade do plano.", "renovacaoAutomatica");
            var a = await AssinaturaSql.PorIdAsync(cx, null, c.IdAssinatura.Value, false, ct) ?? throw PagamentoException.NaoEncontrado();
            await ExigirAsync(cx, a.IdEstabelecimento, RegrasDeAcesso.Cartao, ct);
            if (c.DividaValor > 0)
                throw PagamentoException.Regra(AssinaturaCodigos.Devendo, "Pague esta cobrança com Pix ou cartão. Depois ligue o cartão automático.");
            var k = ValidarCartaoAutomatico(r.Cartao, r.AutorizoRenovacao);
            await CancelarPixPendenteAsync(cx, c, usuarioId, ct);
            var ciclo = await AssinaturaSql.CicloAsync(cx, null, c.IdCiclo!.Value, ct);
            var inicio = c.Vencimento > DateTimeOffset.UtcNow.AddMinutes(10) ? c.Vencimento : (DateTimeOffset?)null;
            var status = await CriarPreapprovalAsync(cx, a, k, c.Valor, ciclo?.Periodicidade ?? a.Periodicidade, inicio, usuarioId, ct);
            return new CheckoutCobrancaDto(c.Id, MetodosPagamento.Cartao, StatusCobrancaComercial.Processando, c.Valor, null, null,
                new RecorrenteCheckoutDto(status.Status, inicio ?? DateTimeOffset.UtcNow, status.Final));
        }

        public async Task<AssinaturaDto> LigarCartaoAsync(Guid loja, CartaoAutomaticoRequest r, int? usuarioId, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var agora = DateTimeOffset.UtcNow;
            await EncerrarCanceladaVencidaAsync(cx, loja, agora, ct);
            var a = await AssinaturaSql.VivaAsync(cx, null, loja, false, ct)
                ?? throw new PagamentoException(AssinaturaCodigos.SemAssinatura, 404, "A loja não tem assinatura.");
            await ExigirAsync(cx, loja, RegrasDeAcesso.Cartao, ct);
            var k = ValidarCartaoAutomatico(r.Cartao, r.AutorizoRenovacao);

            // O MP cobra a próxima mensalidade: a que já está em aberto, ou a do período seguinte ao que está pago.
            var aberta = await cx.QueryFirstOrDefaultAsync<CobrancaRow>(new CommandDefinition($@"
SELECT {AssinaturaSql.ColunasCobranca} FROM comercial_cobranca WHERE id_assinatura = @A AND tipo = 'ciclo' AND status IN ('aberta', 'processando')
 ORDER BY vencimento LIMIT 1", new { A = a.Id }, cancellationToken: ct));
            decimal valor;
            string periodicidade;
            DateTimeOffset? inicio;
            if (aberta != null)
            {
                var ciclo = await AssinaturaSql.CicloAsync(cx, null, aberta.IdCiclo!.Value, ct);
                valor = aberta.Valor;
                periodicidade = ciclo?.Periodicidade ?? a.Periodicidade;
                inicio = aberta.Vencimento > agora.AddMinutes(10) ? aberta.Vencimento : null;
                await CancelarPixPendenteAsync(cx, aberta, usuarioId, ct);
            }
            else
            {
                if (a.MudancaPeriodicidade != null && a.MudancaPeriodicidade != a.Periodicidade)
                    throw new PagamentoException(AssinaturaCodigos.SemCobranca, 409, "Há uma troca entre mensal e anual marcada. Ligue o cartão depois que ela começar.");
                if (a.PeriodoFim == null) throw new PagamentoException(AssinaturaCodigos.SemCobranca, 409, "Ainda não há mensalidade para cobrar no cartão.");
                var pagoAte = await cx.ExecuteScalarAsync<DateTimeOffset?>(new CommandDefinition(
                    "SELECT MAX(fim) FROM assinatura_ciclo WHERE id_assinatura = @A AND status = 'pago'", new { A = a.Id }, cancellationToken: ct));
                var fim = pagoAte > a.PeriodoFim ? pagoAte.Value : a.PeriodoFim.Value;
                valor = a.MudancaPreco ?? a.Preco;
                periodicidade = a.Periodicidade;
                inicio = fim > agora.AddMinutes(10) ? fim : null;
            }
            if (valor <= 0) throw new PagamentoException(AssinaturaCodigos.SemCobranca, 409, "Sua assinatura é cortesia: não há o que cobrar.");
            await CriarPreapprovalAsync(cx, a, k, valor, periodicidade, inicio, usuarioId, ct);
            _cache.Remove(SituacaoComercialService.CacheKey(loja));
            return await AssinaturaAsync(loja, ct);
        }

        public async Task<AssinaturaDto> DesligarCartaoAsync(Guid loja, int? usuarioId, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            string? preapproval;
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                var a = await VivaOuErroAsync(cx, tx, loja, ct);
                preapproval = PreapprovalVivo(a);
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET renovacao_automatica = FALSE, metodo = NULL, cartao_final = NULL, cartao_bandeira = NULL, versao = versao + 1, updated_at = NOW() WHERE id = @A;
UPDATE comercial_cobranca SET status = 'aberta', meio = NULL, versao = versao + 1, updated_at = NOW()
 WHERE id_assinatura = @A AND status = 'processando' AND meio = 'cartao_recorrente';", new { A = a.Id }, tx, cancellationToken: ct));
                await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "assinatura", a.Id.ToString(), "renovacao_desligada", "usuario", usuarioId, null, ct);
                await tx.CommitAsync(ct);
            }
            if (preapproval != null) await CancelarPreapprovalAsync(preapproval, ct);
            return await AssinaturaAsync(loja, ct);
        }

        private static CartaoCheckoutRequest ValidarCartaoAutomatico(CartaoCheckoutRequest? k, bool autorizo)
        {
            if (!autorizo)
                throw PagamentoException.Regra(AssinaturaCodigos.RenovacaoSemAutorizacao, "Confirme que autoriza a cobrança automática a cada período.");
            if (k == null) throw PagamentoException.Invalida("Dados do cartão obrigatórios.", "cartao");
            if (string.IsNullOrWhiteSpace(k.Token)) throw PagamentoException.Invalida("Token do cartão obrigatório.", "cartao.token");
            var email = k.Pagador?.Email?.Trim();
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) throw PagamentoException.Invalida("E-mail do titular obrigatório.", "cartao.pagador.email");
            return k;
        }

        private async Task<(string? Status, string? Final)> CriarPreapprovalAsync(NpgsqlConnection cx, AssinaturaRow a, CartaoCheckoutRequest k, decimal valor,
            string periodicidade, DateTimeOffset? inicio, int? usuarioId, CancellationToken ct)
        {
            if (PreapprovalVivo(a) is { } anterior) await CancelarPreapprovalAsync(anterior, ct); // troca de cartao

            var cred = await _credenciais.RecebedorZippyAsync(null, ct);
            var nome = (await AssinaturaSql.PlanoAsync(cx, null, a.PlanoChave, ct))?.Nome ?? a.PlanoChave;
            var token = k.Token!.Trim();
            // O final do cartão só existe no token, que o MP deixa consultar antes de usar. Sem ele, o primeiro pagamento preenche.
            string? final = null;
            try
            {
                var digitos = (await _preapproval.ObterTokenCartaoAsync(cred, token, ct)).LastFourDigits;
                if (digitos is { Length: 4 } && digitos.All(char.IsAsciiDigit)) final = digitos;
            }
            catch (MercadoPagoApiException ex) { _logger.LogWarning("[assinatura] ev=final_do_cartao_indisponivel http={Http}", ex.StatusHttp); }

            var corpo = new MpCriarPreapproval
            {
                Reason = $"Zippy - Plano {nome} ({(periodicidade == Periodicidades.Anual ? "anual" : "mensal")})",
                ExternalReference = PreapprovalReferencia.Gerar(cred.Ambiente, a.Id),
                PayerEmail = k.Pagador!.Email!.Trim(),
                CardTokenId = token,
                BackUrl = (_config["Payments:MercadoPago:PainelUrl"] ?? "https://zippygo.com.br").TrimEnd('/') + "/apps/configuracoes/assinatura",
                AutoRecurring = new MpPreapprovalRecorrencia
                {
                    Frequency = Periodicidades.Meses(periodicidade), FrequencyType = "months", TransactionAmount = valor,
                    StartDate = inicio?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
                }
            };
            var chave = $"preapproval:{a.Id:N}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..24].ToLowerInvariant()}";
            MpPreapproval pre;
            try
            {
                pre = await _preapproval.CriarAsync(cred, corpo, chave, ct);
            }
            catch (MercadoPagoApiException ex)
            {
                if (!ex.Indisponivel)
                    throw new PagamentoException(PagamentoCodigos.CartaoRecusado, 422, "O Mercado Pago não aceitou este cartão para a cobrança automática.",
                        new Dictionary<string, object?> { ["mpErro"] = ex.MpErro });
                throw ex.ParaPagamento();
            }
            if (pre.ExternalReference != corpo.ExternalReference)
                throw new PagamentoException(PagamentoCodigos.MercadoPagoRespostaInconsistente, 502, "O Mercado Pago devolveu uma assinatura que não confere.");

            await using var tx = await cx.BeginTransactionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET mp_preapproval_id = @P, mp_preapproval_status = @S, preapproval_valor = @V, renovacao_automatica = TRUE, metodo = 'cartao',
       renovacao_autorizada_em = NOW(), renovacao_autorizada_por = @U, cartao_bandeira = @B, cartao_final = @F, versao = versao + 1, updated_at = NOW()
 WHERE id = @A;
UPDATE comercial_cobranca SET status = 'processando', meio = 'cartao_recorrente', versao = versao + 1, updated_at = NOW()
 WHERE id_assinatura = @A AND tipo = 'ciclo' AND status = 'aberta' AND divida_valor = 0 AND vencimento >= @Hoje;", new
            {
                P = pre.Id, S = pre.Status ?? "pending", V = valor, U = usuarioId, B = k.PaymentMethodId, F = final, A = a.Id,
                Hoje = FusoComercial.InicioDeHoje(DateTimeOffset.UtcNow)
            }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, a.IdEstabelecimento, "assinatura", a.Id.ToString(), "renovacao_autorizada", "usuario", usuarioId,
                new { preapproval = pre.Id, status = pre.Status, valor, inicio, frequencia = periodicidade }, ct);
            await tx.CommitAsync(ct);
            return (pre.Status, final);
        }

        // ---------- compras e solicitacoes ----------

        public async Task<CompraCriadaDto> ComprarAsync(Guid loja, CompraRequest r, int? usuarioId, Guid? solicitacaoId = null, CancellationToken ct = default)
        {
            if (r.Quantidade is < 1 or > 20) throw PagamentoException.Invalida("Quantidade entre 1 e 20.", "quantidade");
            var chave = string.IsNullOrWhiteSpace(r.IdempotencyKey) ? null : r.IdempotencyKey.Trim();
            if (chave == null || chave.Length > 160) throw PagamentoException.Invalida("Chave de idempotência obrigatória (até 160 caracteres).", "idempotencyKey");
            await AssinaturaOperacoes.ExpirarTentativasAsync(_db, _pix, _logger, loja, DateTimeOffset.UtcNow, ct);
            await using var cx = await _db.OpenConnectionAsync(ct);
            await EncerrarCanceladaVencidaAsync(cx, loja, DateTimeOffset.UtcNow, ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition("SELECT 1 FROM estabelecimentos WHERE id = @Loja FOR UPDATE", new { Loja = loja }, tx, cancellationToken: ct));

            // A mesma chave devolve a mesma compra, seja qual for a situação de agora.
            var existente = await cx.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM compra_extra WHERE id_estabelecimento = @Loja AND idempotency_key = @K", new { Loja = loja, K = chave }, tx, cancellationToken: ct));
            if (existente.HasValue)
            {
                await tx.CommitAsync(ct);
                return await CompraCriadaAsync(cx, existente.Value, true, ct);
            }

            var produto = await ProdutoAsync(cx, tx, r.ProdutoAvulsoId, ct);
            await ExigirPodeComprarAsync(cx, loja, produto.Modulos.Length > 0, ct);
            // Duplo clique com chave nova: a mesma compra ainda esperando pagamento e reaproveitada.
            existente = await cx.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(@"
SELECT ce.id FROM compra_extra ce JOIN comercial_cobranca c ON c.id_compra = ce.id
 WHERE ce.id_estabelecimento = @Loja AND ce.produto_avulso_id = @P AND ce.quantidade = @Q AND ce.status = 'aguardando_pagamento'
   AND c.status IN ('aberta', 'processando') AND ce.created_at > NOW() - INTERVAL '30 minutes' ORDER BY ce.created_at DESC LIMIT 1",
                new { Loja = loja, P = r.ProdutoAvulsoId, Q = r.Quantidade }, tx, cancellationToken: ct));
            if (existente.HasValue)
            {
                await tx.CommitAsync(ct);
                return await CompraCriadaAsync(cx, existente.Value, true, ct);
            }

            var total = Proporcional.Arredondar(produto.Preco * r.Quantidade);
            var compraId = Guid.NewGuid();
            var cobrancaId = Guid.NewGuid();
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO compra_extra (id, id_estabelecimento, produto_avulso_id, nome, preco_unitario, quantidade, valor_total, motoboys, modulos_ativos, id_solicitacao, criado_por, idempotency_key)
VALUES (@Id, @Loja, @P, @Nome, @Preco, @Q, @Total, @Motoboys, @Modulos, @Sol, @U, @K);
INSERT INTO comercial_cobranca (id, id_estabelecimento, tipo, id_compra, descricao, valor, vencimento, idempotency_key, expira_em)
VALUES (@Cob, @Loja, 'compra_extra', @Id, @Desc, @Total, NOW(), @KC, @Expira);", new
            {
                Id = compraId, Loja = loja, P = produto.Id, produto.Nome, produto.Preco, Q = r.Quantidade, Total = total, produto.Motoboys, produto.Modulos,
                Sol = solicitacaoId, U = usuarioId, K = chave, Cob = cobrancaId, KC = $"compra:{compraId:N}", Expira = DateTimeOffset.UtcNow.Add(ValidadeDaTentativa),
                Desc = AssinaturaSql.Cortar(r.Quantidade > 1 ? $"{r.Quantidade}x {produto.Nome}" : produto.Nome)
            }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "compra_extra", compraId.ToString(), "criada", usuarioId.HasValue ? "usuario" : "sistema", usuarioId,
                new { produto = produto.Id, produto.Nome, precoUnitario = produto.Preco, r.Quantidade, total, solicitacaoId }, ct);
            await tx.CommitAsync(ct);
            return await CompraCriadaAsync(cx, compraId, false, ct);
        }

        private sealed class ProdutoRow
        {
            public long Id { get; set; }
            public string Nome { get; set; } = string.Empty;
            public decimal Preco { get; set; }
            public int Motoboys { get; set; }
            public string[] Modulos { get; set; } = Array.Empty<string>();
            public bool Arquivado { get; set; }
        }

        private static async Task<ProdutoRow> ProdutoAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, long id, CancellationToken ct)
        {
            var p = await cx.QueryFirstOrDefaultAsync<ProdutoRow>(new CommandDefinition(
                "SELECT id, nome, preco, motoboys, modulos_ativos::text[] AS modulos, arquivado FROM produtos_avulsos WHERE id = @Id", new { Id = id }, tx,
                cancellationToken: ct));
            if (p is not { Arquivado: false } || p.Preco <= 0) throw PagamentoException.Regra(AssinaturaCodigos.ProdutoInvalido, "Este recurso não está à venda.");
            return p;
        }

        /// <summary>Extra só com plano ativo; devendo, só motoboys.</summary>
        private static Task ExigirPodeComprarAsync(NpgsqlConnection cx, Guid loja, bool modulo, CancellationToken ct) =>
            ExigirAsync(cx, loja, f => RegrasDeAcesso.Extra(f, modulo), ct);


        private static async Task<CompraCriadaDto> CompraCriadaAsync(NpgsqlConnection cx, Guid compraId, bool reaproveitada, CancellationToken ct)
        {
            var compra = await cx.QueryFirstAsync<CompraDto>(new CommandDefinition(SqlCompra + " WHERE ce.id = @Id", new { Id = compraId }, cancellationToken: ct));
            var c = await AssinaturaSql.CobrancaAsync(cx, null, compra.CobrancaId!.Value, false, ct);
            return new CompraCriadaDto(compra, Dto(c!, null), reaproveitada);
        }

        public async Task<SolicitacaoDto> SolicitarAsync(Guid loja, SolicitacaoRequest r, int usuarioId, CancellationToken ct = default)
        {
            if (r.Quantidade is < 1 or > 20) throw PagamentoException.Invalida("Quantidade entre 1 e 20.", "quantidade");
            var motivo = string.IsNullOrWhiteSpace(r.Motivo) ? null : r.Motivo.Trim();
            if (motivo is { Length: > 300 }) motivo = motivo[..300];
            await using var cx = await _db.OpenConnectionAsync(ct);
            var produto = await ProdutoAsync(cx, null, r.ProdutoAvulsoId, ct);
            await ExigirPodeComprarAsync(cx, loja, produto.Modulos.Length > 0, ct);
            var id = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(@"
INSERT INTO solicitacao_compra (id, id_estabelecimento, produto_avulso_id, quantidade, motivo, solicitante_id)
VALUES (@Id, @Loja, @P, @Q, @M, @U)
ON CONFLICT (id_estabelecimento, produto_avulso_id, solicitante_id) WHERE status = 'pendente' DO NOTHING RETURNING id",
                new { Id = Guid.NewGuid(), Loja = loja, P = r.ProdutoAvulsoId, Q = r.Quantidade, M = motivo, U = usuarioId }, cancellationToken: ct));
            if (id.HasValue)
                await AssinaturaSql.AvisarAsync(cx, null, loja, null, $"solicitacao:{id:N}", "solicitacao_compra", "Um funcionário pediu a compra de um recurso. Aprove ou recuse em Assinatura.", ct);
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
                if (linha == null) throw PagamentoException.NaoEncontrado("Pedido não encontrado.");
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
                    throw new PagamentoException(AssinaturaCodigos.JaDecidida, 409, "Este pedido já foi decidido.");
                }
                // Conferido antes de aprovar: aprovar sem poder comprar deixaria o pedido aprovado sem compra.
                if (r.Aprovar)
                {
                    var produto = await ProdutoAsync(cx, tx, s.Produto, ct);
                    await ExigirPodeComprarAsync(cx, loja, produto.Modulos.Length > 0, ct);
                }
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE solicitacao_compra SET status = @S, decidido_por = @U, decidido_em = NOW(), decisao_motivo = @M, updated_at = NOW() WHERE id = @Id",
                    new { S = r.Aprovar ? "aprovada" : "recusada", U = usuarioId, M = motivo, Id = solicitacaoId }, tx, cancellationToken: ct));
                await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "solicitacao_compra", solicitacaoId.ToString(), r.Aprovar ? "aprovada" : "recusada", "usuario", usuarioId,
                    new { motivo }, ct);
                // Quem pediu precisa saber a resposta.
                var nomeDoProduto = await cx.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT nome FROM produtos_avulsos WHERE id = @Id", new { Id = s.Produto },
                    tx, cancellationToken: ct));
                await AssinaturaSql.AvisarAsync(cx, tx, loja, null, $"decisao:{solicitacaoId:N}", "solicitacao_decidida",
                    r.Aprovar ? $"O pedido de \"{nomeDoProduto}\" foi aprovado."
                        : $"O pedido de \"{nomeDoProduto}\" foi recusado.{(motivo == null ? "" : " Motivo: " + motivo)}", ct);
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
            await using var cx = await _db.OpenConnectionAsync(ct);
            Guid loja;
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                var c = await AssinaturaSql.CobrancaAsync(cx, tx, cobrancaId, true, ct) ?? throw PagamentoException.NaoEncontrado("Cobrança não encontrada.");
                if (c.Status == StatusCobrancaComercial.Paga) throw new PagamentoException(PagamentoCodigos.JaPago, 409, "Esta cobrança já foi paga.");
                if (c.Status == StatusCobrancaComercial.Cancelada) throw PagamentoException.Estado("Esta cobrança foi cancelada.", c.Status);
                loja = c.IdEstabelecimento;
                await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "comercial_cobranca", c.Id.ToString(), "baixa_manual", "usuario", usuarioId, new { motivo, c.Valor }, ct);
                await AssinaturaSql.ConfirmarPagamentoAsync(cx, tx, c.Id, c.Valor, "manual", null, null, "usuario", usuarioId, ct);
                await tx.CommitAsync(ct);
            }
            // Pix ainda aberto desta cobranca: cancelado no MP (se for pago mesmo assim, vira excedente).
            await AssinaturaOperacoes.CancelarPagamentosDaCobrancaAsync(cx, _pix, _logger, cobrancaId, loja, ct);
            await LimparAsync(loja, ct);
            return await CobrancaAsync(cobrancaId, ct);
        }
    }
}
