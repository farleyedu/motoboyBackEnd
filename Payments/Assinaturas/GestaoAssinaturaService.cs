using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.MercadoPago;
using APIBack.Service;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Payments.Assinaturas
{
    public interface IGestaoAssinaturaService
    {
        Task<IReadOnlyList<GestaoAssinaturaLinhaDto>> ListarAsync(string? estado, string? mesVigente, string? busca, CancellationToken ct = default);
        Task<AssinaturaDto> CriarAsync(Guid loja, GestaoCriarAssinaturaRequest r, int? usuarioId, CancellationToken ct = default);
        Task<AlteracaoDto> AlterarAsync(Guid loja, GestaoAlterarAssinaturaRequest r, int? usuarioId, CancellationToken ct = default);
        Task<AssinaturaDto> CancelarAsync(Guid loja, GestaoCancelarAssinaturaRequest r, int? usuarioId, CancellationToken ct = default);
        Task<AssinaturaDto> RetomarAsync(Guid loja, GestaoMotivoRequest r, int? usuarioId, CancellationToken ct = default);
        Task<GestaoExtraCriadoDto> ExtraAsync(Guid loja, GestaoExtraRequest r, int? usuarioId, CancellationToken ct = default);
        Task<CobrancaDto> CancelarCobrancaAsync(Guid cobrancaId, GestaoMotivoRequest r, int? usuarioId, CancellationToken ct = default);
        Task<CobrancaDto> VencimentoAsync(Guid cobrancaId, GestaoVencimentoRequest r, int? usuarioId, CancellationToken ct = default);
        Task<IReadOnlyList<ExcedenteDto>> ExcedentesAsync(CancellationToken ct = default);
        Task<CobrancaDto> ExcedenteDevolvidoAsync(Guid cobrancaId, GestaoMotivoRequest r, int? usuarioId, CancellationToken ct = default);
    }

    /// <summary>
    /// A Gestão não tem restrição: faz qualquer ação, mesmo a que a loja não pode. Cada ação registra quem e quando (motivo opcional).
    /// O que a loja não poderia fazer aparece como aviso na prévia.
    /// </summary>
    public sealed class GestaoAssinaturaService : IGestaoAssinaturaService
    {
        /// <summary>Cobrança de upgrade ou extra criada pela Gestão: a loja tem uma semana para pagar.</summary>
        public static readonly TimeSpan ValidadeDaCobrancaDaGestao = TimeSpan.FromDays(7);

        private readonly NpgsqlDataSource _db;
        private readonly IAssinaturaService _assinaturas;
        private readonly IPixChargeService _pix;
        private readonly IMemoryCache _cache;
        private readonly ILogger<GestaoAssinaturaService> _logger;

        public GestaoAssinaturaService(NpgsqlDataSource db, IAssinaturaService assinaturas, IPixChargeService pix, IMemoryCache cache,
            ILogger<GestaoAssinaturaService> logger)
        {
            _db = db;
            _assinaturas = assinaturas;
            _pix = pix;
            _cache = cache;
            _logger = logger;
        }

        public async Task<IReadOnlyList<GestaoAssinaturaLinhaDto>> ListarAsync(string? estado, string? mesVigente, string? busca, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var lojas = await AssinaturaConsulta.CarregarAsync(cx, null, DateTimeOffset.UtcNow, ct);
            var planos = (await cx.QueryAsync<(string Chave, string Nome)>(new CommandDefinition("SELECT chave, nome FROM pacotes_comerciais", cancellationToken: ct)))
                .ToDictionary(p => p.Chave, p => p.Nome);
            string Nome(string chave) => planos.GetValueOrDefault(chave) ?? chave;

            var filtroEstado = Limpo(estado);
            var filtroMes = Limpo(mesVigente);
            var texto = Limpo(busca);
            var comparar = CultureInfo.GetCultureInfo("pt-BR").CompareInfo;

            return lojas.Values
                .Where(l => filtroEstado == null || l.Situacao.Estado == filtroEstado)
                .Where(l => filtroMes == null || l.Situacao.MesVigente == filtroMes)
                .Where(l => texto == null || comparar.IndexOf(l.Nome, texto, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)
                .OrderBy(l => l.Nome, StringComparer.Create(CultureInfo.GetCultureInfo("pt-BR"), ignoreCase: true))
                .Select(l =>
                {
                    // Quem desistiu antes de pagar não tem plano; a encerrada mostra o que tinha.
                    var a = l.Assinatura;
                    if (a is { Status: StatusAssinatura.Encerrada, EncerramentoMotivo: Encerramentos.AntesDoPagamento }) a = null;
                    var viva = a is { Viva: true };
                    return new GestaoAssinaturaLinhaDto(l.Loja, l.Nome, l.Situacao.Estado, a == null ? null : new PlanoDaListaDto(a.PlanoChave, Nome(a.PlanoChave)),
                        a?.Periodicidade, a?.Preco, viva ? l.ProximoVencimento : null, l.Situacao.MesVigente,
                        l.Divida == null ? null : new DividaResumoDto(l.Divida.Valor, l.Divida.Desde), l.Fatos.SituacaoLoja == SituacoesComerciais.Bloqueado,
                        viva && a!.MudancaPlano != null ? new TrocaDaListaDto(Nome(a.MudancaPlano), l.TrocaEm) : null,
                        viva && a!.CancelamentoSolicitadoEm != null ? l.AcessoAte : null);
                }).ToList();
        }

        public async Task<AssinaturaDto> CriarAsync(Guid loja, GestaoCriarAssinaturaRequest r, int? usuarioId, CancellationToken ct = default)
        {
            var periodicidade = Periodicidade(r.Periodicidade, Periodicidades.Mensal);
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                await TravarLojaAsync(cx, tx, loja, ct);
                var (plano, preco) = await PlanoEPrecoAsync(cx, tx, r.Plano, periodicidade, r.Cortesia, ct);
                var viva = await AssinaturaSql.VivaAsync(cx, tx, loja, true, ct);
                if (viva != null) throw new PagamentoException(AssinaturaCodigos.JaExiste, 409, "A loja já tem uma assinatura. Use alterar.");
                await AssinaturaOperacoes.CriarPelaGestaoAsync(cx, tx, loja, plano.Chave, plano.Nome, periodicidade, preco, FusoComercial.Data(DateTimeOffset.UtcNow),
                    usuarioId, ct);
                await AssinaturaSql.AplicarPlanoAsync(cx, tx, loja, plano.Chave, "usuario", usuarioId, ct);
                await RegistrarAsync(cx, tx, loja, usuarioId, "assinatura_criada", r.Motivo, $"Plano {plano.Nome} {periodicidade}{(r.Cortesia ? ", cortesia" : "")}", ct);
                await tx.CommitAsync(ct);
            }
            await LimparAsync(loja, ct);
            return await _assinaturas.AssinaturaAsync(loja, ct);
        }

        public async Task<AlteracaoDto> AlterarAsync(Guid loja, GestaoAlterarAssinaturaRequest r, int? usuarioId, CancellationToken ct = default)
        {
            var quando = (r.Quando ?? "agora").Trim().ToLowerInvariant();
            if (quando is not ("agora" or "proximo_ciclo")) throw PagamentoException.Invalida("Escolha agora ou próximo ciclo.", "quando");
            var agora = quando == "agora";
            await using var cx = await _db.OpenConnectionAsync(ct);
            PrevisaoMudanca previsao;
            CobrancaDto? cobranca = null;
            Guid? novaCobranca = null;
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                await TravarLojaAsync(cx, tx, loja, ct);
                var a = await VivaAsync(cx, tx, loja, ct);
                var periodicidade = Periodicidade(r.Periodicidade, a.Periodicidade);
                var (plano, preco) = await PlanoEPrecoAsync(cx, tx, r.Plano, periodicidade, r.Cortesia, ct);
                var igual = plano.Chave == a.PlanoChave && periodicidade == a.Periodicidade && preco == a.Preco;
                if (igual && a.MudancaPlano == null) throw new PagamentoException(AssinaturaCodigos.SemMudanca, 409, "A assinatura já está assim.");

                var hoje = FusoComercial.Data(DateTimeOffset.UtcNow);
                var ativa = a.Status == StatusAssinatura.Ativa;
                var menor = preco < a.Preco;
                var tipo = menor ? TiposMudanca.Downgrade : TiposMudanca.Upgrade;
                PrevisaoMudanca Simples(string t, decimal valor, string aplicacao, DateOnly em, params string[] texto) =>
                    new(t, a.Preco, preco, 0, 0, 0, valor, aplicacao, em, texto);

                if (igual)
                    previsao = Simples(TiposMudanca.Nenhuma, 0, "agora", hoje, "A troca marcada é desfeita.");
                else if (!ativa)
                    previsao = Simples(tipo, 0, "agora", hoje, r.Cortesia ? "Cortesia: a assinatura passa a valer agora, sem cobrança."
                        : "Ainda não houve pagamento: a fatura passa a ter o valor do plano novo.");
                else if (!agora)
                {
                    var ciclo = await AssinaturaOperacoes.CicloAtualAsync(cx, tx, a, ct) ?? throw PagamentoException.Estado("Assinatura sem período vigente.", a.Status);
                    var proximo = (await AssinaturaOperacoes.CiclosSeguintesAsync(cx, tx, a, ct)).FirstOrDefault();
                    var em = FusoComercial.Data(proximo is { Quitado: true } ? proximo.Fim : ciclo.Fim);
                    previsao = Simples(tipo, 0, "fim_do_periodo", em, $"Vale a partir de {em:dd/MM/yyyy}, por {Proporcional.Brl(preco)}.");
                }
                else if (r.Cortesia)
                    previsao = Simples(tipo, 0, "agora", hoje, "Cortesia: vale agora, sem cobrança. O que ainda não venceu deixa de ser cobrado.");
                else
                {
                    var conta = await AssinaturaService.PreverAsync(cx, tx, a, plano, periodicidade, preco, ct);
                    if (conta.Tipo == TiposMudanca.Nenhuma)
                        previsao = Simples(tipo, 0, "agora", hoje, "Mesmo plano: o valor novo entra no que ainda não venceu.");
                    else if (conta.Tipo != TiposMudanca.Downgrade)
                        previsao = conta.ValorAgora > 0
                            ? conta with { Explicacao = conta.Explicacao.Append("A loja tem 7 dias para pagar; sem pagamento, nada muda.").ToList() }
                            : conta;
                    else if (periodicidade != a.Periodicidade)
                        previsao = Simples(TiposMudanca.Downgrade, preco, "agora", hoje,
                            $"Vale agora: começa um período novo hoje, com cobrança de {Proporcional.Brl(preco)}.", "O que já foi pago não é devolvido sozinho.");
                    else
                        previsao = Simples(TiposMudanca.Downgrade, 0, "agora", hoje, "Vale agora, sem estorno do que já foi pago.");
                }

                var avisos = await AvisosAsync(cx, loja, periodicidade, ct);
                previsao = previsao with { Explicacao = avisos.Concat(previsao.Explicacao).ToList() };
                if (!r.Confirmar) { await tx.CommitAsync(ct); return new AlteracaoDto(previsao, false, null, null); }

                if (igual)
                    await AssinaturaOperacoes.DesfazerMudancaAsync(cx, tx, a, usuarioId, ct);
                else if (!ativa)
                {
                    if (r.Cortesia)
                    {
                        await AssinaturaOperacoes.AtivarPendenteAsync(cx, tx, a, plano.Chave, plano.Nome, periodicidade, preco, hoje, usuarioId, ct);
                        await AssinaturaSql.AplicarPlanoAsync(cx, tx, loja, plano.Chave, "usuario", usuarioId, ct);
                    }
                    else await AssinaturaOperacoes.TrocarPendenteAsync(cx, tx, a, plano.Chave, periodicidade, preco, usuarioId, ct);
                }
                else if (!agora)
                {
                    if (a.CancelamentoSolicitadoEm != null)
                        throw new PagamentoException(AssinaturaCodigos.CanceladaRetome, 409, "Retome a assinatura antes de marcar a troca.");
                    await AssinaturaOperacoes.AgendarMudancaAsync(cx, tx, a, plano.Chave, periodicidade, preco, usuarioId, ct);
                }
                else if (!r.Cortesia && previsao.Tipo is TiposMudanca.Upgrade or TiposMudanca.ParaAnual && previsao.ValorAgora > 0)
                {
                    await AssinaturaOperacoes.CancelarTentativasDeUpgradeAsync(cx, tx, a.Id, MotivosCancelamentoCobranca.Substituida, usuarioId, ct);
                    novaCobranca = await AssinaturaService.CriarUpgradeAsync(cx, tx, a, plano, periodicidade, preco, previsao,
                        DateTimeOffset.UtcNow.Add(ValidadeDaCobrancaDaGestao), usuarioId, ct);
                }
                else if (!r.Cortesia && previsao.Tipo == TiposMudanca.ParaAnual)
                {
                    await AssinaturaOperacoes.AplicarMudancaAgoraAsync(cx, tx, a, TiposMudanca.ParaAnual, plano.Chave, periodicidade, preco, "usuario", usuarioId, ct);
                }
                else
                {
                    await AssinaturaOperacoes.MudarAgoraPelaGestaoAsync(cx, tx, a, plano.Chave, periodicidade, preco, usuarioId, ct);
                    await AssinaturaSql.AplicarPlanoAsync(cx, tx, loja, plano.Chave, "usuario", usuarioId, ct);
                }
                await RegistrarAsync(cx, tx, loja, usuarioId, "assinatura_alterada", r.Motivo,
                    $"{a.PlanoChave} → {plano.Chave} {periodicidade}, {quando.Replace('_', ' ')}{(r.Cortesia ? ", cortesia" : "")}", ct);
                await tx.CommitAsync(ct);
            }
            await LimparAsync(loja, ct);
            if (novaCobranca.HasValue) cobranca = await _assinaturas.CobrancaAsync(novaCobranca.Value, ct);
            return new AlteracaoDto(previsao, true, cobranca, await _assinaturas.AssinaturaAsync(loja, ct));
        }

        /// <summary>A situação que a Gestão precisa ver antes de agir.</summary>
        private static async Task<List<string>> AvisosAsync(NpgsqlConnection cx, Guid loja, string periodicidadeNova, CancellationToken ct)
        {
            var dados = await AssinaturaConsulta.UmaAsync(cx, loja, DateTimeOffset.UtcNow, ct);
            var avisos = new List<string>();
            if (dados.Divida is { } d)
                avisos.Add($"Esta loja deve {Proporcional.Brl(d.Valor)} desde {FusoComercial.Data(d.Desde):dd/MM}.");
            if (dados.Fatos.Cancelada && dados.AcessoAte is { } ate)
                avisos.Add($"A assinatura está cancelada e vale até {FusoComercial.Data(ate):dd/MM}.");
            if (periodicidadeNova == Periodicidades.Mensal && RegrasDeAcesso.AnualValendo(dados.Fatos))
                avisos.Add("Anual para mensal é uma troca que só a Gestão faz.");
            return avisos;
        }

        public async Task<AssinaturaDto> CancelarAsync(Guid loja, GestaoCancelarAssinaturaRequest r, int? usuarioId, CancellationToken ct = default)
        {
            var quando = (r.Quando ?? "fim_do_periodo").Trim().ToLowerInvariant();
            if (quando is not ("agora" or "fim_do_periodo")) throw PagamentoException.Invalida("Escolha agora ou fim do período.", "quando");
            var motivo = Cortar(r.Motivo);
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                await TravarLojaAsync(cx, tx, loja, ct);
                var a = await VivaAsync(cx, tx, loja, ct);
                var agora = DateTimeOffset.UtcNow;
                if (quando == "agora" || a.Status == StatusAssinatura.Pendente)
                    await AssinaturaOperacoes.EncerrarAgoraAsync(cx, tx, a, Encerramentos.PelaGestao, motivo, usuarioId, agora, ct);
                else if (a.CancelamentoSolicitadoEm == null)
                    await AssinaturaOperacoes.CancelarNoFimAsync(cx, tx, a, motivo, usuarioId, agora, ct);
                else
                    throw new PagamentoException(AssinaturaCodigos.SemMudanca, 409, "A assinatura já está cancelada.");
                await RegistrarAsync(cx, tx, loja, usuarioId, "assinatura_cancelada", motivo, quando.Replace('_', ' '), ct);
                await tx.CommitAsync(ct);
            }
            await LimparAsync(loja, ct);
            return await _assinaturas.AssinaturaAsync(loja, ct);
        }

        public async Task<AssinaturaDto> RetomarAsync(Guid loja, GestaoMotivoRequest r, int? usuarioId, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                await TravarLojaAsync(cx, tx, loja, ct);
                var a = await VivaAsync(cx, tx, loja, ct);
                if (a.CancelamentoSolicitadoEm == null) throw new PagamentoException(AssinaturaCodigos.SemMudanca, 409, "A assinatura não está cancelada.");
                await AssinaturaOperacoes.RetomarAsync(cx, tx, a, usuarioId, DateTimeOffset.UtcNow, ct);
                await RegistrarAsync(cx, tx, loja, usuarioId, "assinatura_retomada", r.Motivo, null, ct);
                await tx.CommitAsync(ct);
            }
            await LimparAsync(loja, ct);
            return await _assinaturas.AssinaturaAsync(loja, ct);
        }

        private sealed class ProdutoRow
        {
            public long Id { get; set; }
            public string Nome { get; set; } = string.Empty;
            public decimal Preco { get; set; }
            public int Motoboys { get; set; }
            public string[] Modulos { get; set; } = Array.Empty<string>();
        }

        public async Task<GestaoExtraCriadoDto> ExtraAsync(Guid loja, GestaoExtraRequest r, int? usuarioId, CancellationToken ct = default)
        {
            if (r.Dias is < 1 or > 730) throw PagamentoException.Invalida("O extra vale de 1 a 730 dias.", "dias");
            if (r.Motoboys is < 0 or > 200) throw PagamentoException.Invalida("Motoboys entre 0 e 200.", "motoboys");
            await using var cx = await _db.OpenConnectionAsync(ct);
            Guid? compraId = null, cobrancaId = null;
            long? pacoteId = null;
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                await TravarLojaAsync(cx, tx, loja, ct);
                var produto = r.ProdutoAvulsoId == null ? null : await cx.QueryFirstOrDefaultAsync<ProdutoRow>(new CommandDefinition(
                    "SELECT id, nome, preco, motoboys, modulos_ativos::text[] AS modulos FROM produtos_avulsos WHERE id = @Id", new { Id = r.ProdutoAvulsoId }, tx,
                    cancellationToken: ct)) ?? throw PagamentoException.Regra(AssinaturaCodigos.ProdutoInvalido, "Extra não encontrado.");
                var motoboys = r.Motoboys is > 0 ? r.Motoboys.Value : produto?.Motoboys ?? 0;
                if (produto == null && motoboys <= 0) throw PagamentoException.Invalida("Escolha um extra ou a quantidade de motoboys.", "motoboys");
                if (!r.Cortesia && (produto == null || produto.Preco <= 0))
                    throw PagamentoException.Regra(AssinaturaCodigos.ProdutoInvalido, "Para cobrar, escolha um extra do catálogo com preço.");

                if (produto == null)
                {
                    // Só motoboys, de presente: o pacote é o que os créditos leem.
                    var fim = FusoComercial.InicioDoDia(FusoComercial.Data(DateTimeOffset.UtcNow).AddDays(r.Dias + 1));
                    pacoteId = await cx.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO motoboy_pacotes (estabelecimento_id, quantidade, periodicidade, inicio_utc, fim_utc, lancado_por)
VALUES (@Loja, @Q, 'mensal', NOW(), @Fim, @U) RETURNING id", new { Loja = loja, Q = motoboys, Fim = fim, U = usuarioId }, tx, cancellationToken: ct));
                }
                else
                {
                    compraId = Guid.NewGuid();
                    var preco = r.Cortesia ? 0 : produto.Preco;
                    await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO compra_extra (id, id_estabelecimento, produto_avulso_id, nome, preco_unitario, quantidade, valor_total, motoboys, modulos_ativos, criado_por, idempotency_key, cortesia, dias)
VALUES (@Id, @Loja, @P, @Nome, @Preco, 1, @Preco, @Motoboys, @Modulos, @U, @K, @Cortesia, @Dias)", new
                    {
                        Id = compraId, Loja = loja, P = produto.Id, produto.Nome, Preco = preco, Motoboys = motoboys, produto.Modulos, U = usuarioId,
                        K = $"gestao:{compraId:N}", r.Cortesia, r.Dias
                    }, tx, cancellationToken: ct));
                    if (r.Cortesia)
                    {
                        var compra = await cx.QueryFirstAsync<CompraRow>(new CommandDefinition(
                            $"SELECT {AssinaturaSql.ColunasCompra} FROM compra_extra WHERE id = @Id FOR UPDATE", new { Id = compraId }, tx, cancellationToken: ct));
                        await AssinaturaSql.AtivarCompraAsync(cx, tx, compra, "usuario", usuarioId, ct);
                        pacoteId = await cx.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT id FROM motoboy_pacotes WHERE id_compra_extra = @Id",
                            new { Id = compraId }, tx, cancellationToken: ct));
                    }
                    else
                    {
                        cobrancaId = Guid.NewGuid();
                        await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO comercial_cobranca (id, id_estabelecimento, tipo, id_compra, descricao, valor, vencimento, idempotency_key, expira_em)
VALUES (@Id, @Loja, 'compra_extra', @C, @D, @V, NOW(), @K, @Expira)", new
                        {
                            Id = cobrancaId, Loja = loja, C = compraId, D = AssinaturaSql.Cortar(produto.Nome), V = preco, K = $"compra:{compraId:N}",
                            Expira = DateTimeOffset.UtcNow.Add(ValidadeDaCobrancaDaGestao)
                        }, tx, cancellationToken: ct));
                    }
                }
                await RegistrarAsync(cx, tx, loja, usuarioId, "extra_lancado", r.Motivo,
                    $"{produto?.Nome ?? $"+{motoboys} motoboys"}, {r.Dias} dias{(r.Cortesia ? ", cortesia" : "")}", ct);
                await tx.CommitAsync(ct);
            }
            await LimparAsync(loja, ct);
            return new GestaoExtraCriadoDto(compraId.HasValue ? await _assinaturas.CompraAsync(compraId.Value, ct) : null,
                cobrancaId.HasValue ? await _assinaturas.CobrancaAsync(cobrancaId.Value, ct) : null, pacoteId);
        }

        /// <summary>Dispensa uma cobrança. A mensalidade dispensada deixa o período sem cobrança, e a assinatura segue.</summary>
        public async Task<CobrancaDto> CancelarCobrancaAsync(Guid cobrancaId, GestaoMotivoRequest r, int? usuarioId, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            Guid loja;
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                var c = await AssinaturaSql.CobrancaAsync(cx, tx, cobrancaId, true, ct) ?? throw PagamentoException.NaoEncontrado("Cobrança não encontrada.");
                if (c.Status == StatusCobrancaComercial.Paga) throw new PagamentoException(PagamentoCodigos.JaPago, 409, "Esta cobrança já foi paga.");
                loja = c.IdEstabelecimento;
                if (c.Status != StatusCobrancaComercial.Cancelada)
                {
                    await AssinaturaOperacoes.CancelarCobrancaAsync(cx, tx, c, MotivosCancelamentoCobranca.Gestao, usuarioId, ct);
                    if (c.Tipo == TiposCobranca.Ciclo && c.IdCiclo.HasValue) await DispensarCicloAsync(cx, tx, c, usuarioId, ct);
                    await RegistrarAsync(cx, tx, loja, usuarioId, "cobranca_cancelada", r.Motivo, $"{c.Descricao} · {Proporcional.Brl(c.Valor)}", ct);
                }
                await tx.CommitAsync(ct);
            }
            await LimparAsync(loja, ct);
            return await _assinaturas.CobrancaAsync(cobrancaId, ct);
        }

        private static async Task DispensarCicloAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CobrancaRow c, int? usuarioId, CancellationToken ct)
        {
            await cx.ExecuteAsync(new CommandDefinition("UPDATE assinatura_ciclo SET status = 'pago', updated_at = NOW() WHERE id = @Id AND status = 'a_pagar'",
                new { Id = c.IdCiclo }, tx, cancellationToken: ct));
            var a = await AssinaturaSql.PorIdAsync(cx, tx, c.IdAssinatura!.Value, true, ct);
            if (a == null) return;
            if (a.Status == StatusAssinatura.Pendente && a.IdCicloAtual == c.IdCiclo)
            {
                await cx.ExecuteAsync(new CommandDefinition(
                    "UPDATE assinatura SET status = 'ativa', ativada_em = COALESCE(ativada_em, NOW()), versao = versao + 1, updated_at = NOW() WHERE id = @Id",
                    new { a.Id }, tx, cancellationToken: ct));
                var ciclo = (await AssinaturaSql.CicloAsync(cx, tx, c.IdCiclo!.Value, ct))!;
                if (ciclo.Inicio <= DateTimeOffset.UtcNow)
                {
                    await AssinaturaSql.AplicarPlanoSeMudouAsync(cx, tx, a.IdEstabelecimento, ciclo.PlanoChave, "usuario", usuarioId, ct);
                    await cx.ExecuteAsync(new CommandDefinition("UPDATE assinatura_ciclo SET aplicado_em = COALESCE(aplicado_em, NOW()) WHERE id = @Id",
                        new { ciclo.Id }, tx, cancellationToken: ct));
                }
            }
            await AssinaturaSql.RecalcularFinanceiroAsync(cx, tx, a.Id, DateTimeOffset.UtcNow, ct);
        }

        public async Task<CobrancaDto> VencimentoAsync(Guid cobrancaId, GestaoVencimentoRequest r, int? usuarioId, CancellationToken ct = default)
        {
            if (!DateOnly.TryParseExact(r.Vencimento?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dia))
                throw PagamentoException.Invalida("Informe o vencimento como AAAA-MM-DD.", "vencimento");
            await using var cx = await _db.OpenConnectionAsync(ct);
            Guid loja;
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                var c = await AssinaturaSql.CobrancaAsync(cx, tx, cobrancaId, true, ct) ?? throw PagamentoException.NaoEncontrado("Cobrança não encontrada.");
                if (!StatusCobrancaComercial.EmAberto(c.Status)) throw PagamentoException.Estado("Só cobrança em aberto muda de vencimento.", c.Status);
                loja = c.IdEstabelecimento;
                // Numa tentativa de upgrade ou extra, o novo dia é o último para pagar. Na cobrança com dívida somada, a dívida passa a contar dele.
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE comercial_cobranca SET vencimento = @Venc,
       expira_em = CASE WHEN tipo = 'ciclo' THEN NULL ELSE @FimDoDia END,
       divida_desde = CASE WHEN divida_valor > 0 THEN @Venc ELSE divida_desde END,
       versao = versao + 1, updated_at = NOW() WHERE id = @Id",
                    new { c.Id, Venc = FusoComercial.InicioDoDia(dia), FimDoDia = FusoComercial.InicioDoDia(dia.AddDays(1)) }, tx, cancellationToken: ct));
                if (c.IdAssinatura.HasValue) await AssinaturaSql.RecalcularFinanceiroAsync(cx, tx, c.IdAssinatura.Value, DateTimeOffset.UtcNow, ct);
                await RegistrarAsync(cx, tx, loja, usuarioId, "vencimento_alterado", r.Motivo,
                    $"{c.Descricao}: {FusoComercial.Data(c.Vencimento):dd/MM/yyyy} → {dia:dd/MM/yyyy}", ct);
                await tx.CommitAsync(ct);
            }
            await LimparAsync(loja, ct);
            return await _assinaturas.CobrancaAsync(cobrancaId, ct);
        }

        public async Task<IReadOnlyList<ExcedenteDto>> ExcedentesAsync(CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return (await cx.QueryAsync<ExcedenteDto>(new CommandDefinition(@"
SELECT c.id AS cobranca_id, c.id_estabelecimento AS estabelecimento_id, COALESCE(e.nome_fantasia, 'Loja') AS loja, c.descricao,
       c.valor_excedente - c.excedente_devolvido AS valor, COALESCE(c.excedente_em, c.updated_at) AS quando
  FROM comercial_cobranca c JOIN estabelecimentos e ON e.id = c.id_estabelecimento
 WHERE c.valor_excedente > c.excedente_devolvido
 ORDER BY COALESCE(c.excedente_em, c.updated_at) DESC", cancellationToken: ct))).ToList();
        }

        public async Task<CobrancaDto> ExcedenteDevolvidoAsync(Guid cobrancaId, GestaoMotivoRequest r, int? usuarioId, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                var linha = await cx.QueryFirstOrDefaultAsync<(Guid Loja, string Descricao, decimal Falta)?>(new CommandDefinition(
                    "SELECT id_estabelecimento, descricao, valor_excedente - excedente_devolvido FROM comercial_cobranca WHERE id = @Id FOR UPDATE",
                    new { Id = cobrancaId }, tx, cancellationToken: ct)) ?? throw PagamentoException.NaoEncontrado("Cobrança não encontrada.");
                var (loja, descricao, falta) = linha;
                if (falta <= 0) throw new PagamentoException(AssinaturaCodigos.SemMudanca, 409, "Não há valor a devolver nesta cobrança.");
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE comercial_cobranca SET excedente_devolvido = valor_excedente, excedente_devolvido_em = NOW(), excedente_devolvido_por = @U, versao = versao + 1, updated_at = NOW()
 WHERE id = @Id", new { Id = cobrancaId, U = usuarioId }, tx, cancellationToken: ct));
                await RegistrarAsync(cx, tx, loja, usuarioId, "excedente_devolvido", r.Motivo, $"{descricao} · {Proporcional.Brl(falta)}", ct);
                await tx.CommitAsync(ct);
            }
            return await _assinaturas.CobrancaAsync(cobrancaId, ct);
        }

        // ---------- apoio ----------

        private static string? Limpo(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

        private static string? Cortar(string? motivo) => Limpo(motivo) is { Length: > 300 } m ? m[..300] : Limpo(motivo);

        private static string Periodicidade(string? pedida, string padrao)
        {
            var p = (Limpo(pedida) ?? padrao).ToLowerInvariant();
            return Periodicidades.Valida(p) ? p : throw PagamentoException.Invalida("Escolha mensal ou anual.", "periodicidade");
        }

        private static async Task TravarLojaAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid loja, CancellationToken ct)
        {
            if (await cx.ExecuteScalarAsync<int?>(new CommandDefinition("SELECT 1 FROM estabelecimentos WHERE id = @Loja FOR UPDATE", new { Loja = loja }, tx,
                    cancellationToken: ct)) == null)
                throw PagamentoException.NaoEncontrado("Estabelecimento não encontrado.");
        }

        private static async Task<AssinaturaRow> VivaAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid loja, CancellationToken ct) =>
            await AssinaturaSql.VivaAsync(cx, tx, loja, true, ct)
            ?? throw new PagamentoException(AssinaturaCodigos.SemAssinatura, 404, "A loja não tem assinatura em andamento. Crie uma nova.");

        /// <summary>A Gestão pode usar plano arquivado; "personalizado" (sem preço no catálogo) só como cortesia.</summary>
        private static async Task<(PlanoCatalogoRow Plano, decimal Preco)> PlanoEPrecoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, string? chave,
            string periodicidade, bool cortesia, CancellationToken ct)
        {
            var c = (chave ?? string.Empty).Trim().ToLowerInvariant();
            var plano = c.Length == 0 ? null : await AssinaturaSql.PlanoAsync(cx, tx, c, ct);
            if (plano == null && c == "personalizado" && cortesia) plano = new PlanoCatalogoRow { Chave = c, Nome = "Personalizado" };
            if (plano == null) throw PagamentoException.Regra(AssinaturaCodigos.PlanoInvalido, "Escolha um plano do catálogo (personalizado só como cortesia).");
            if (cortesia) return (plano, 0);
            var preco = plano.PrecoDe(periodicidade);
            if (preco is null or <= 0)
                throw PagamentoException.Regra(AssinaturaCodigos.PeriodicidadeIndisponivel,
                    $"O plano {plano.Nome} não tem preço {(periodicidade == Periodicidades.Anual ? "anual" : "mensal")}. Marque cortesia ou escolha outro.");
            return (plano, preco.Value);
        }

        /// <summary>Quem e quando, na auditoria dos pagamentos e na linha do tempo da loja.</summary>
        private static async Task RegistrarAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid loja, int? usuarioId, string acao, string? motivo, string? detalhe,
            CancellationToken ct)
        {
            motivo = Cortar(motivo);
            await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "gestao_assinatura", loja.ToString(), acao, "usuario", usuarioId, new { motivo, detalhe }, ct);
            if (await cx.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT to_regclass('gestao_acao_log') IS NOT NULL", transaction: tx, cancellationToken: ct)))
                await cx.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO gestao_acao_log (usuario_id, estabelecimento_id, acao, detalhe) VALUES (@U, @Loja, @Acao, @Detalhe)",
                    new { U = usuarioId, Loja = loja, Acao = acao, Detalhe = string.Join(" · ", new[] { detalhe, motivo }.Where(x => x != null)) }, tx,
                    cancellationToken: ct));
        }

        private async Task LimparAsync(Guid loja, CancellationToken ct)
        {
            await AssinaturaOperacoes.CancelarPagamentosDeCanceladasAsync(_db, _pix, _logger, loja, ct);
            _cache.Remove(SituacaoComercialService.CacheKey(loja));
        }
    }
}
