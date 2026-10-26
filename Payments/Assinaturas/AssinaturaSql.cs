using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.MercadoPago;
using Dapper;
using Npgsql;

namespace APIBack.Payments.Assinaturas
{
    public sealed class AssinaturaRow
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public string PlanoChave { get; set; } = string.Empty;
        public string Periodicidade { get; set; } = Periodicidades.Mensal;
        public decimal Preco { get; set; }
        public string Status { get; set; } = StatusAssinatura.Pendente;
        public string Financeiro { get; set; } = EstadoFinanceiro.EmDia;
        public DateTimeOffset? InadimplenteDesde { get; set; }
        public DateTimeOffset? SuspensaEm { get; set; }
        public DateOnly? Ancora { get; set; }
        public int AncoraCiclo { get; set; } = 1;
        public Guid? IdCicloAtual { get; set; }
        public DateTimeOffset? PeriodoInicio { get; set; }
        public DateTimeOffset? PeriodoFim { get; set; }
        public string? Metodo { get; set; }
        public bool RenovacaoAutomatica { get; set; }
        public DateTimeOffset? RenovacaoAutorizadaEm { get; set; }
        public int? RenovacaoAutorizadaPor { get; set; }
        public string? MpPreapprovalId { get; set; }
        public string? MpPreapprovalStatus { get; set; }
        public decimal? PreapprovalValor { get; set; }
        public string? CartaoFinal { get; set; }
        public string? CartaoBandeira { get; set; }
        public string? MudancaPlano { get; set; }
        public string? MudancaPeriodicidade { get; set; }
        public decimal? MudancaPreco { get; set; }
        public DateTimeOffset? MudancaPedidaEm { get; set; }
        public DateTimeOffset? CancelamentoSolicitadoEm { get; set; }
        public int? CancelamentoPor { get; set; }
        public string? CancelamentoMotivo { get; set; }
        public DateTimeOffset? EncerradaEm { get; set; }
        public string? EncerramentoMotivo { get; set; }
        public int Versao { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    public sealed class CicloRow
    {
        public Guid Id { get; set; }
        public Guid IdAssinatura { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public int Numero { get; set; }
        public string PlanoChave { get; set; } = string.Empty;
        public string Periodicidade { get; set; } = Periodicidades.Mensal;
        public decimal Preco { get; set; }
        public DateTimeOffset Inicio { get; set; }
        public DateTimeOffset Fim { get; set; }
        public string Status { get; set; } = "a_pagar";
        public DateTimeOffset? AplicadoEm { get; set; }
    }

    public sealed class CobrancaRow
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public string Tipo { get; set; } = TiposCobranca.Ciclo;
        public Guid? IdAssinatura { get; set; }
        public Guid? IdCiclo { get; set; }
        public Guid? IdCompra { get; set; }
        public string Descricao { get; set; } = string.Empty;
        public decimal Valor { get; set; }
        public DateTimeOffset Vencimento { get; set; }
        public string Status { get; set; } = StatusCobrancaComercial.Aberta;
        public string? Meio { get; set; }
        public int Emissao { get; set; }
        public Guid? IdIntentAtual { get; set; }
        public long? MpPaymentId { get; set; }
        public DateTimeOffset? PagoEm { get; set; }
        public string? PagoVia { get; set; }
        public decimal ValorPago { get; set; }
        public decimal ValorExcedente { get; set; }
        public string? Detalhe { get; set; }
        public int Versao { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    public sealed class CompraRow
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public long ProdutoAvulsoId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public decimal PrecoUnitario { get; set; }
        public int Quantidade { get; set; }
        public decimal ValorTotal { get; set; }
        public int Motoboys { get; set; }
        public string[] ModulosAtivos { get; set; } = Array.Empty<string>();
        public string Status { get; set; } = "aguardando_pagamento";
        public DateTimeOffset? VigenciaInicio { get; set; }
        public DateTimeOffset? VigenciaFim { get; set; }
        public Guid? IdSolicitacao { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    /// <summary>Plano do catalogo (pacotes_comerciais): preco e composicao sempre lidos no servidor.</summary>
    public sealed class PlanoCatalogoRow
    {
        public string Chave { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public decimal Preco { get; set; }
        public decimal? PrecoAnual { get; set; }
        public string[] ModulosAtivos { get; set; } = Array.Empty<string>();
        public int MotoboysBase { get; set; }
        public string? Cor { get; set; }
        public string? Icone { get; set; }
        public string? Frase { get; set; }
        public bool Arquivado { get; set; }
        public int Ordem { get; set; }

        public decimal? PrecoDe(string periodicidade) => periodicidade == Periodicidades.Anual ? PrecoAnual : Preco;
    }

    /// <summary>Resultado de um pagamento confirmado: a loja precisa recarregar a situacao (cache) e talvez o preapproval mudar de valor.</summary>
    public sealed record EfeitoPagamento(bool Aplicado, bool MudouAcesso, decimal Excedente);

    /// <summary>
    /// Operacoes do dominio que rodam dentro de uma transacao (servico, efeito do pagamento, webhook do cartao recorrente e worker).
    /// E o unico lugar que confirma uma cobranca e aplica o plano na loja.
    /// </summary>
    public static class AssinaturaSql
    {
        public const string ColunasAssinatura = @"id, id_estabelecimento, plano_chave, periodicidade, preco, status, financeiro, inadimplente_desde, suspensa_em,
ancora, ancora_ciclo, id_ciclo_atual, periodo_inicio, periodo_fim, metodo, renovacao_automatica, renovacao_autorizada_em, renovacao_autorizada_por,
mp_preapproval_id, mp_preapproval_status, preapproval_valor, cartao_final, cartao_bandeira, mudanca_plano, mudanca_periodicidade, mudanca_preco,
mudanca_pedida_em, cancelamento_solicitado_em, cancelamento_por, cancelamento_motivo, encerrada_em, encerramento_motivo, versao, created_at";

        public const string ColunasCobranca = @"id, id_estabelecimento, tipo, id_assinatura, id_ciclo, id_compra, descricao, valor, vencimento, status, meio, emissao,
id_intent_atual, mp_payment_id, pago_em, pago_via, valor_pago, valor_excedente, detalhe::text AS detalhe, versao, created_at";

        public const string ColunasCiclo = "id, id_assinatura, id_estabelecimento, numero, plano_chave, periodicidade, preco, inicio, fim, status, aplicado_em";

        public const string ColunasCompra = @"id, id_estabelecimento, produto_avulso_id, nome, preco_unitario, quantidade, valor_total, motoboys, modulos_ativos,
status, vigencia_inicio, vigencia_fim, id_solicitacao, created_at";

        public const string ColunasPlano = @"chave, nome, preco, preco_anual, modulos_ativos::text[] AS modulos_ativos, motoboys_base, cor, icone, frase, arquivado, ordem";

        public static Task<AssinaturaRow?> VivaAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid loja, bool travar, CancellationToken ct) =>
            cx.QueryFirstOrDefaultAsync<AssinaturaRow>(new CommandDefinition(
                $"SELECT {ColunasAssinatura} FROM assinatura WHERE id_estabelecimento = @Loja AND status IN ('pendente', 'ativa'){(travar ? " FOR UPDATE" : "")}",
                new { Loja = loja }, tx, cancellationToken: ct));

        /// <summary>A viva, ou a ultima encerrada (para mostrar historico e o acesso suspenso depois do fim).</summary>
        public static Task<AssinaturaRow?> AtualAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid loja, CancellationToken ct) =>
            cx.QueryFirstOrDefaultAsync<AssinaturaRow>(new CommandDefinition(
                $"SELECT {ColunasAssinatura} FROM assinatura WHERE id_estabelecimento = @Loja ORDER BY (status IN ('pendente', 'ativa')) DESC, created_at DESC LIMIT 1",
                new { Loja = loja }, tx, cancellationToken: ct));

        public static Task<AssinaturaRow?> PorIdAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid id, bool travar, CancellationToken ct) =>
            cx.QueryFirstOrDefaultAsync<AssinaturaRow>(new CommandDefinition(
                $"SELECT {ColunasAssinatura} FROM assinatura WHERE id = @Id{(travar ? " FOR UPDATE" : "")}", new { Id = id }, tx, cancellationToken: ct));

        public static Task<CobrancaRow?> CobrancaAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid id, bool travar, CancellationToken ct) =>
            cx.QueryFirstOrDefaultAsync<CobrancaRow>(new CommandDefinition(
                $"SELECT {ColunasCobranca} FROM comercial_cobranca WHERE id = @Id{(travar ? " FOR UPDATE" : "")}", new { Id = id }, tx, cancellationToken: ct));

        public static Task<CicloRow?> CicloAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid id, CancellationToken ct) =>
            cx.QueryFirstOrDefaultAsync<CicloRow>(new CommandDefinition($"SELECT {ColunasCiclo} FROM assinatura_ciclo WHERE id = @Id FOR UPDATE",
                new { Id = id }, tx, cancellationToken: ct));

        public static Task<PlanoCatalogoRow?> PlanoAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, string chave, CancellationToken ct) =>
            cx.QueryFirstOrDefaultAsync<PlanoCatalogoRow>(new CommandDefinition($"SELECT {ColunasPlano} FROM pacotes_comerciais WHERE chave = @C",
                new { C = chave }, tx, cancellationToken: ct));

        public static async Task<PoliticaInadimplencia> PoliticaAsync(NpgsqlConnection cx, CancellationToken ct)
        {
            try
            {
                var p = await cx.QueryFirstOrDefaultAsync<(int Tolerancia, int[]? Antes, int[]? Depois)?>(new CommandDefinition(
                    "SELECT tolerancia_dias, avisos_antes_dias, avisos_depois_dias FROM pagamento_politica WHERE id = 1", cancellationToken: ct));
                return p is { } v ? new PoliticaInadimplencia(v.Tolerancia, v.Antes ?? Array.Empty<int>(), v.Depois ?? Array.Empty<int>()) : PoliticaInadimplencia.Padrao;
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
            {
                return PoliticaInadimplencia.Padrao;
            }
        }

        public static string DescricaoCiclo(string nomePlano, string periodicidade, DateOnly inicio, DateOnly fim)
        {
            var d = $"Plano {nomePlano} {(periodicidade == Periodicidades.Anual ? "anual" : "mensal")} {inicio:dd/MM/yy} a {fim:dd/MM/yy}";
            return d.Length > 140 ? d[..140] : d;
        }

        /// <summary>Cria o ciclo e a divida dele (idempotente pelo numero do ciclo). Devolve o ciclo existente se ja havia.</summary>
        public static async Task<CicloRow> CriarCicloAsync(NpgsqlConnection cx, NpgsqlTransaction tx, AssinaturaRow a, int numero, string plano, string nomePlano,
            string periodicidade, decimal preco, DateOnly inicio, DateOnly fim, string? meio, CancellationToken ct)
        {
            var id = Guid.NewGuid();
            var novo = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(@"
INSERT INTO assinatura_ciclo (id, id_assinatura, id_estabelecimento, numero, plano_chave, periodicidade, preco, inicio, fim)
VALUES (@Id, @A, @Loja, @N, @Plano, @Per, @Preco, @Ini, @Fim)
ON CONFLICT (id_assinatura, numero) DO NOTHING RETURNING id", new
            {
                Id = id, A = a.Id, Loja = a.IdEstabelecimento, N = numero, Plano = plano, Per = periodicidade, Preco = preco,
                Ini = FusoComercial.InicioDoDia(inicio), Fim = FusoComercial.InicioDoDia(fim)
            }, tx, cancellationToken: ct));
            var ciclo = await cx.QueryFirstAsync<CicloRow>(new CommandDefinition(
                $"SELECT {ColunasCiclo} FROM assinatura_ciclo WHERE id_assinatura = @A AND numero = @N FOR UPDATE", new { A = a.Id, N = numero }, tx, cancellationToken: ct));
            if (!novo.HasValue) return ciclo;

            if (preco > 0)
            {
                var cobrancaId = Guid.NewGuid();
                await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO comercial_cobranca (id, id_estabelecimento, tipo, id_assinatura, id_ciclo, descricao, valor, vencimento, status, meio, idempotency_key)
VALUES (@Id, @Loja, 'ciclo', @A, @C, @D, @V, @Venc, @S, @Meio, @Chave)", new
                {
                    Id = cobrancaId, Loja = a.IdEstabelecimento, A = a.Id, C = ciclo.Id, D = DescricaoCiclo(nomePlano, periodicidade, inicio, fim), V = preco,
                    Venc = ciclo.Inicio, S = meio == "cartao_recorrente" ? StatusCobrancaComercial.Processando : StatusCobrancaComercial.Aberta, Meio = meio,
                    Chave = $"ciclo:{ciclo.Id:N}"
                }, tx, cancellationToken: ct));
                await AvisarAsync(cx, tx, a.IdEstabelecimento, cobrancaId, $"{cobrancaId:N}:gerada", "fatura_gerada",
                    $"Fatura de {Proporcional.Brl(preco)} do plano {nomePlano} gerada, com vencimento em {inicio:dd/MM}.", ct);
            }
            else
            {
                await cx.ExecuteAsync(new CommandDefinition("UPDATE assinatura_ciclo SET status = 'pago' WHERE id = @Id", new { ciclo.Id }, tx, cancellationToken: ct));
                ciclo.Status = "pago";
            }
            await PagamentoAuditoria.RegistrarAsync(cx, tx, a.IdEstabelecimento, "assinatura_ciclo", ciclo.Id.ToString(), "criado", "sistema", null,
                new { assinatura = a.Id, numero, plano, periodicidade, preco, inicio, fim }, ct);
            return ciclo;
        }

        public static Task AvisarAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid loja, Guid? cobranca, string chave, string tipo, string mensagem, CancellationToken ct) =>
            cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO comercial_aviso (id_estabelecimento, id_cobranca, tipo, chave, mensagem) VALUES (@Loja, @Cob, @Tipo, @Chave, @Msg)
ON CONFLICT (id_estabelecimento, chave) DO NOTHING", new { Loja = loja, Cob = cobranca, Tipo = tipo, Chave = chave, Msg = mensagem }, tx, cancellationToken: ct));

        /// <summary>
        /// Dinheiro confirmado para uma cobranca (Pix/cartao avulso pelo nucleo, cartao recorrente, ou baixa manual da Gestao).
        /// Idempotente: a cobranca ja paga (ou cancelada) recebe o valor como excedente e nada e aplicado de novo.
        /// </summary>
        public static async Task<EfeitoPagamento> ConfirmarPagamentoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid cobrancaId, decimal valor, string via,
            Guid? intentId, long? mpPaymentId, string ator, int? usuarioId, CancellationToken ct, long? mpAuthorizedPaymentId = null)
        {
            var c = await CobrancaAsync(cx, tx, cobrancaId, true, ct);
            if (c == null) return new EfeitoPagamento(false, false, 0);

            // O mesmo pagamento (mesma intencao ou mesmo id do MP) chegando de novo nao conta duas vezes.
            if (c.Status == StatusCobrancaComercial.Paga && ((intentId.HasValue && c.IdIntentAtual == intentId) || (mpPaymentId.HasValue && c.MpPaymentId == mpPaymentId)))
                return new EfeitoPagamento(false, false, 0);

            if (!StatusCobrancaComercial.EmAberto(c.Status))
            {
                // Pix pago depois de outro meio, cartao recorrente apos baixa manual...: guarda para a Gestao devolver.
                await cx.ExecuteAsync(new CommandDefinition(
                    "UPDATE comercial_cobranca SET valor_excedente = valor_excedente + @V, versao = versao + 1, updated_at = NOW() WHERE id = @Id",
                    new { V = valor, c.Id }, tx, cancellationToken: ct));
                await PagamentoAuditoria.RegistrarAsync(cx, tx, c.IdEstabelecimento, "comercial_cobranca", c.Id.ToString(), "pagamento_excedente", ator, usuarioId,
                    new { valor, via, intentId, mpPaymentId, status = c.Status }, ct);
                await AvisarAsync(cx, tx, c.IdEstabelecimento, c.Id, $"{c.Id:N}:excedente:{intentId?.ToString("N") ?? mpPaymentId?.ToString() ?? Guid.NewGuid().ToString("N")}",
                    "pagamento_excedente", $"Recebemos {Proporcional.Brl(valor)} a mais em \"{c.Descricao}\". A Zippy vai devolver esse valor.", ct);
                return new EfeitoPagamento(false, false, valor);
            }

            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE comercial_cobranca SET status = 'paga', pago_em = NOW(), pago_via = @Via, valor_pago = @V, meio = COALESCE(meio, @Meio),
       id_intent_atual = COALESCE(@Intent, id_intent_atual), mp_payment_id = COALESCE(@Mp, mp_payment_id),
       mp_authorized_payment_id = COALESCE(@Auth, mp_authorized_payment_id), versao = versao + 1, updated_at = NOW()
 WHERE id = @Id", new
            {
                Via = via, V = valor, Meio = via == "manual" ? "manual" : null, Intent = intentId, Mp = mpPaymentId, Auth = mpAuthorizedPaymentId, c.Id
            }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, c.IdEstabelecimento, "comercial_cobranca", c.Id.ToString(), "paga", ator, usuarioId,
                new { valor, via, intentId, mpPaymentId, tipo = c.Tipo }, ct);
            await AvisarAsync(cx, tx, c.IdEstabelecimento, c.Id, $"{c.Id:N}:paga", "pagamento_confirmado",
                $"Pagamento de {Proporcional.Brl(valor)} confirmado: {c.Descricao}.", ct);

            var mudouAcesso = c.Tipo switch
            {
                TiposCobranca.Ciclo => await CicloPagoAsync(cx, tx, c, ator, usuarioId, ct),
                TiposCobranca.Upgrade => await UpgradePagoAsync(cx, tx, c, ator, usuarioId, ct),
                TiposCobranca.CompraExtra => await CompraPagaAsync(cx, tx, c, ator, usuarioId, ct),
                _ => false
            };
            if (c.IdAssinatura.HasValue)
                mudouAcesso |= await RecalcularFinanceiroAsync(cx, tx, c.IdAssinatura.Value, await PoliticaAsync(cx, ct), DateTimeOffset.UtcNow, ct);
            return new EfeitoPagamento(true, mudouAcesso, 0);
        }

        private static async Task<bool> CicloPagoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CobrancaRow c, string ator, int? usuarioId, CancellationToken ct)
        {
            var ciclo = await CicloAsync(cx, tx, c.IdCiclo!.Value, ct);
            var a = await PorIdAsync(cx, tx, c.IdAssinatura!.Value, true, ct);
            if (ciclo == null || a == null) return false;
            await cx.ExecuteAsync(new CommandDefinition("UPDATE assinatura_ciclo SET status = 'pago', updated_at = NOW() WHERE id = @Id AND status <> 'pago'",
                new { ciclo.Id }, tx, cancellationToken: ct));
            if (a.Status == StatusAssinatura.Pendente)
            {
                await cx.ExecuteAsync(new CommandDefinition("UPDATE assinatura SET status = 'ativa', versao = versao + 1, updated_at = NOW() WHERE id = @Id",
                    new { a.Id }, tx, cancellationToken: ct));
                await PagamentoAuditoria.RegistrarAsync(cx, tx, a.IdEstabelecimento, "assinatura", a.Id.ToString(), "ativada", ator, usuarioId, new { ciclo = ciclo.Id }, ct);
            }
            // Pago no teste: o plano entra quando o ciclo comecar (o worker aplica). Pago no dia ou depois: entra agora.
            if (ciclo.AplicadoEm == null && ciclo.Inicio <= DateTimeOffset.UtcNow)
            {
                await AplicarPlanoAsync(cx, tx, a.IdEstabelecimento, ciclo.PlanoChave, ator, usuarioId, ct);
                await cx.ExecuteAsync(new CommandDefinition("UPDATE assinatura_ciclo SET aplicado_em = NOW() WHERE id = @Id", new { ciclo.Id }, tx, cancellationToken: ct));
                return true;
            }
            return false;
        }

        private sealed class DetalheUpgrade
        {
            public string Tipo { get; set; } = TiposMudanca.Upgrade;
            public string PlanoNovo { get; set; } = string.Empty;
            public string PeriodicidadeNova { get; set; } = Periodicidades.Mensal;
            public decimal PrecoNovo { get; set; }
        }

        private static readonly JsonSerializerOptions JsonDetalhe = new() { PropertyNameCaseInsensitive = true };

        private static async Task<bool> UpgradePagoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CobrancaRow c, string ator, int? usuarioId, CancellationToken ct)
        {
            var d = JsonSerializer.Deserialize<DetalheUpgrade>(c.Detalhe ?? "{}", JsonDetalhe) ?? new DetalheUpgrade();
            var a = await PorIdAsync(cx, tx, c.IdAssinatura!.Value, true, ct);
            if (a == null || a.Status == StatusAssinatura.Encerrada) return false;
            await AplicarMudancaAgoraAsync(cx, tx, a, d.Tipo, d.PlanoNovo, d.PeriodicidadeNova, d.PrecoNovo, ator, usuarioId, ct);
            return true;
        }

        /// <summary>Troca de plano que vale ja (upgrade pago, ou de graca por ser pequena). Mensal -> anual comeca um ciclo anual hoje, ja pago.</summary>
        public static async Task AplicarMudancaAgoraAsync(NpgsqlConnection cx, NpgsqlTransaction tx, AssinaturaRow a, string tipo, string plano, string periodicidade,
            decimal preco, string ator, int? usuarioId, CancellationToken ct)
        {
            if (tipo == TiposMudanca.ParaAnual && a.IdCicloAtual.HasValue)
            {
                var atual = await CicloAsync(cx, tx, a.IdCicloAtual.Value, ct);
                var hoje = FusoComercial.Data(DateTimeOffset.UtcNow);
                var numero = (atual?.Numero ?? 0) + 1;
                // O ciclo mensal acaba agora; um proximo mensal ja gerado (e nao pago) deixa de valer.
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura_ciclo SET fim = GREATEST(inicio + INTERVAL '1 minute', NOW()), updated_at = NOW() WHERE id = @Id;
UPDATE comercial_cobranca SET status = 'cancelada', cancelada_em = NOW(), cancelamento_motivo = 'troca_para_anual', versao = versao + 1, updated_at = NOW()
 WHERE id_assinatura = @A AND tipo = 'ciclo' AND status IN ('aberta', 'processando')
   AND id_ciclo IN (SELECT id FROM assinatura_ciclo WHERE id_assinatura = @A AND numero > @N);
UPDATE assinatura_ciclo SET status = 'cancelado', updated_at = NOW() WHERE id_assinatura = @A AND numero > @N AND status = 'a_pagar';",
                    new { Id = a.IdCicloAtual, A = a.Id, N = atual?.Numero ?? 0 }, tx, cancellationToken: ct));
                var maior = await cx.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COALESCE(MAX(numero), 0) FROM assinatura_ciclo WHERE id_assinatura = @A",
                    new { A = a.Id }, tx, cancellationToken: ct));
                numero = Math.Max(numero, maior + 1);
                var (ini, fim) = CicloDatas.Ciclo(hoje, 1, Periodicidades.Anual);
                var nome = (await PlanoAsync(cx, tx, plano, ct))?.Nome ?? plano;
                // Preco 0 no ciclo: ele e pago pela cobranca de upgrade (sem divida nova).
                var novo = await CriarCicloAsync(cx, tx, a, numero, plano, nome, Periodicidades.Anual, 0, ini, fim, null, ct);
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura_ciclo SET preco = @Preco, status = 'pago', aplicado_em = NOW() WHERE id = @C;
UPDATE assinatura SET plano_chave = @Plano, periodicidade = 'anual', preco = @Preco, ancora = @Ancora, ancora_ciclo = @N, id_ciclo_atual = @C,
       periodo_inicio = @Ini, periodo_fim = @Fim, mudanca_plano = NULL, mudanca_periodicidade = NULL, mudanca_preco = NULL, mudanca_pedida_em = NULL,
       versao = versao + 1, updated_at = NOW()
 WHERE id = @A", new { C = novo.Id, Preco = preco, Plano = plano, Ancora = hoje, N = numero, Ini = novo.Inicio, Fim = novo.Fim, A = a.Id }, tx, cancellationToken: ct));
            }
            else
            {
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET plano_chave = @Plano, preco = @Preco, mudanca_plano = NULL, mudanca_periodicidade = NULL, mudanca_preco = NULL, mudanca_pedida_em = NULL,
       versao = versao + 1, updated_at = NOW() WHERE id = @A;
UPDATE assinatura_ciclo SET plano_chave = @Plano, updated_at = NOW() WHERE id = @C;", new { Plano = plano, Preco = preco, A = a.Id, C = a.IdCicloAtual }, tx, cancellationToken: ct));
            }
            await AplicarPlanoAsync(cx, tx, a.IdEstabelecimento, plano, ator, usuarioId, ct);
            await PagamentoAuditoria.RegistrarAsync(cx, tx, a.IdEstabelecimento, "assinatura", a.Id.ToString(), "plano_alterado", ator, usuarioId,
                new { de = a.PlanoChave, para = plano, tipo, periodicidade, preco }, ct);
        }

        private static async Task<bool> CompraPagaAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CobrancaRow c, string ator, int? usuarioId, CancellationToken ct)
        {
            var compra = await cx.QueryFirstOrDefaultAsync<CompraRow>(new CommandDefinition(
                $"SELECT {ColunasCompra} FROM compra_extra WHERE id = @Id FOR UPDATE", new { Id = c.IdCompra }, tx, cancellationToken: ct));
            if (compra == null || compra.Status != "aguardando_pagamento") return false;
            var agora = DateTimeOffset.UtcNow;
            var hoje = FusoComercial.Data(agora);
            var fim = FusoComercial.InicioDoDia(CicloDatas.Somar(hoje, 1).AddDays(1));
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE compra_extra SET status = 'ativa', ativada_em = NOW(), vigencia_inicio = NOW(), vigencia_fim = @Fim, versao = versao + 1, updated_at = NOW() WHERE id = @Id",
                new { compra.Id, Fim = fim }, tx, cancellationToken: ct));
            var motoboys = compra.Motoboys * compra.Quantidade;
            if (motoboys > 0)
                await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO motoboy_pacotes (estabelecimento_id, quantidade, periodicidade, inicio_utc, fim_utc, lancado_por, id_compra_extra)
VALUES (@Loja, @Q, 'mensal', NOW(), @Fim, @U, @Compra) ON CONFLICT (id_compra_extra) WHERE id_compra_extra IS NOT NULL DO NOTHING",
                    new { Loja = compra.IdEstabelecimento, Q = Math.Min(200, motoboys), Fim = fim, U = usuarioId, Compra = compra.Id }, tx, cancellationToken: ct));
            if (compra.ModulosAtivos.Length > 0)
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE estabelecimentos SET modulos_ativos = ARRAY(SELECT DISTINCT unnest(modulos_ativos || CAST(@M AS text[])::modulo_enum[])) WHERE id = @Loja",
                    new { M = compra.ModulosAtivos, Loja = compra.IdEstabelecimento }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, compra.IdEstabelecimento, "compra_extra", compra.Id.ToString(), "ativada", ator, usuarioId,
                new { compra.Nome, compra.Quantidade, compra.ValorTotal, motoboys, fim }, ct);
            return true;
        }

        /// <summary>
        /// Aplica o plano na loja: modulos do plano + os de compras avulsas vigentes, base de motoboys do plano. Teste vira ativo.
        /// Implantacao e bloqueio manual da Gestao nao mudam de situacao (autorizacao comercial e da Gestao, nao do pagamento).
        /// </summary>
        public static async Task AplicarPlanoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid loja, string plano, string ator, int? usuarioId, CancellationToken ct)
        {
            var p = await PlanoAsync(cx, tx, plano, ct);
            if (p == null) return;
            var antes = await cx.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT situacao_comercial FROM estabelecimentos WHERE id = @Loja FOR UPDATE", new { Loja = loja }, tx, cancellationToken: ct));
            if (antes is not ("teste" or "ativo"))
            {
                await cx.ExecuteAsync(new CommandDefinition("UPDATE estabelecimentos SET plano_comercial = @Plano WHERE id = @Loja",
                    new { Plano = plano, Loja = loja }, tx, cancellationToken: ct));
                return;
            }
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE estabelecimentos e
   SET situacao_comercial = 'ativo', plano_comercial = @Plano, motoboys_base = @Base,
       modulos_ativos = ARRAY(SELECT DISTINCT unnest(CAST(@Modulos AS text[])::modulo_enum[] || COALESCE((
            SELECT array_agg(m)::modulo_enum[] FROM compra_extra ce, unnest(ce.modulos_ativos) m
             WHERE ce.id_estabelecimento = e.id AND ce.status = 'ativa' AND ce.vigencia_fim > NOW()), ARRAY[]::modulo_enum[]))),
       operacao_iniciada_em = COALESCE(operacao_iniciada_em, NOW())
 WHERE e.id = @Loja", new { Plano = plano, Base = p.MotoboysBase, Modulos = p.ModulosAtivos, Loja = loja }, tx, cancellationToken: ct));
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO estabelecimento_situacao_log (estabelecimento_id, acao, situacao_antes, situacao_depois, motivo, usuario_id, detalhes)
VALUES (@Loja, 'ativar', @Antes, 'ativo', 'assinatura', @U, CAST(@D AS jsonb))", new
            {
                Loja = loja, Antes = antes, U = usuarioId, D = JsonSerializer.Serialize(new { plano, origem = "assinatura", ator })
            }, tx, cancellationToken: ct));
        }

        /// <summary>
        /// Recalcula o estado financeiro pela cobranca de ciclo mais antiga em aberto. Upgrade e compra nao pagos nao suspendem nada:
        /// so nao liberam o que foi pedido. Devolve true quando o acesso mudou (suspendeu ou voltou).
        /// </summary>
        public static async Task<bool> RecalcularFinanceiroAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid assinaturaId, PoliticaInadimplencia politica,
            DateTimeOffset agora, CancellationToken ct)
        {
            var a = await PorIdAsync(cx, tx, assinaturaId, true, ct);
            if (a == null) return false;
            var venc = await cx.ExecuteScalarAsync<DateTimeOffset?>(new CommandDefinition(@"
SELECT MIN(vencimento) FROM comercial_cobranca WHERE id_assinatura = @A AND tipo = 'ciclo' AND status IN ('aberta', 'processando')",
                new { A = a.Id }, tx, cancellationToken: ct));
            var hoje = FusoComercial.Data(agora);
            var av = a.Status == StatusAssinatura.Encerrada
                ? new AvaliacaoInadimplencia(EstadoFinanceiro.EmDia, 0, null)
                : Inadimplencia.Avaliar(venc.HasValue ? FusoComercial.Data(venc.Value) : null, hoje, politica.ToleranciaDias);
            if (av.Financeiro == a.Financeiro) return false;

            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET financeiro = @F,
       inadimplente_desde = CASE WHEN @F = 'em_dia' THEN NULL ELSE COALESCE(inadimplente_desde, @Venc) END,
       suspensa_em = CASE WHEN @F = 'suspensa' THEN COALESCE(suspensa_em, NOW()) ELSE NULL END,
       versao = versao + 1, updated_at = NOW()
 WHERE id = @Id", new { F = av.Financeiro, Venc = venc, a.Id }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, a.IdEstabelecimento, "assinatura", a.Id.ToString(), "financeiro", "sistema", null,
                new { de = a.Financeiro, para = av.Financeiro, av.DiasAtraso }, ct);

            var marca = agora.ToUnixTimeSeconds() / 3600;
            if (av.Financeiro == EstadoFinanceiro.Suspensa)
                await AvisarAsync(cx, tx, a.IdEstabelecimento, null, $"suspensa:{a.Id:N}:{marca}", "suspensa",
                    "Operacoes novas suspensas por falta de pagamento. Pague a fatura em aberto: o acesso volta sozinho assim que o pagamento for aprovado.", ct);
            else if (av.Financeiro == EstadoFinanceiro.EmDia && a.Financeiro == EstadoFinanceiro.Suspensa)
                await AvisarAsync(cx, tx, a.IdEstabelecimento, null, $"reativada:{a.Id:N}:{marca}", "reativada",
                    "Pagamento confirmado. Tudo liberado de novo.", ct);
            return av.Financeiro == EstadoFinanceiro.Suspensa || a.Financeiro == EstadoFinanceiro.Suspensa;
        }
    }
}
