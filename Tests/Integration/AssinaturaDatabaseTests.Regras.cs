using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.Assinaturas;
using APIBack.Payments.MercadoPago;
using APIBack.Service;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>Regras de 26/11/2026: tabelas do cliente, os defeitos corrigidos, a Gestão e o catálogo, contra PostgreSQL real.</summary>
public sealed partial class AssinaturaDatabaseTests
{
    private sealed partial class Banco
    {
        public IAssinaturaService Assinaturas => Get<IAssinaturaService>();
        public IGestaoAssinaturaService Gestao => Get<IGestaoAssinaturaService>();
        public GestaoCentralService Central() => new(_source);
        public SituacaoComercialService Situacao() => new(_source, new MemoryCache(new MemoryCacheOptions()), new Mock<IProntidaoService>().Object);

        public async Task Baixa(Guid cobranca) => await Assinaturas.BaixaManualAsync(cobranca, new BaixaManualRequest(), 1);

        /// <summary>Contrata e paga o primeiro ciclo.</summary>
        public async Task<AssinaturaDto> Assinar(string plano, string periodicidade = "mensal")
        {
            await Assinaturas.ContratarAsync(Loja, new ContratarRequest { Plano = plano, Periodicidade = periodicidade, IdempotencyKey = Guid.NewGuid().ToString("N") }, 7);
            var c = (await Assinaturas.CobrancasAsync(Loja)).Single(x => x.PodePagar && x.Tipo == "ciclo");
            await Baixa(c.Id);
            return await Assinaturas.AssinaturaAsync(Loja);
        }

        /// <summary>Deixa o período atual perto do fim e roda a rotina: a próxima fatura nasce.</summary>
        public async Task GerarProximaFatura()
        {
            await Execute("UPDATE assinatura SET periodo_fim = NOW() + INTERVAL '2 days'");
            await Execute(@"UPDATE assinatura_ciclo ci SET fim = a.periodo_fim, inicio = LEAST(ci.inicio, NOW() - INTERVAL '1 day')
  FROM assinatura a WHERE a.id_ciclo_atual = ci.id");
            await Worker().RodarAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        }

        public async Task VirarPeriodo() => await Worker().RodarAsync(DateTimeOffset.UtcNow.AddDays(3), CancellationToken.None);

        public Task<decimal> ValorDoCiclo(int numero) => Scalar<decimal>(
            $"SELECT c.valor FROM comercial_cobranca c JOIN assinatura_ciclo ci ON ci.id = c.id_ciclo WHERE ci.numero = {numero} AND c.status <> 'cancelada'");

        public async Task<Guid> CobrancaDoCiclo(int numero) => await Scalar<Guid>(
            $"SELECT c.id FROM comercial_cobranca c JOIN assinatura_ciclo ci ON ci.id = c.id_ciclo WHERE ci.numero = {numero} AND c.status <> 'cancelada'");

        public async Task<Guid> Upgrade(string plano, string periodicidade = "mensal")
        {
            var r = await Assinaturas.AlterarAsync(Loja, new AlterarRequest { Plano = plano, Periodicidade = periodicidade, Confirmar = true }, 7);
            return r.Cobranca!.Id;
        }

        /// <summary>A fatura atual passa a estar vencida há N dias (em aberto).</summary>
        public Task Vencer(int dias) => Execute($@"
UPDATE comercial_cobranca SET status = 'aberta', pago_em = NULL, pago_via = NULL, valor_pago = 0, vencimento = NOW() - INTERVAL '{dias} days'
 WHERE tipo = 'ciclo' AND status <> 'cancelada';
UPDATE assinatura_ciclo SET status = 'a_pagar' WHERE status <> 'cancelado';");
    }

    private static async Task<string> Codigo(Func<Task> acao) => (await Assert.ThrowsAsync<PagamentoException>(acao)).Codigo;

    // ---------- devendo ----------

    [DeliveryDatabaseFact]
    public async Task DevendoRecusaTrocaEModuloAceitaMotoboysECancelamento()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        await db.Assinar("rota");
        await db.Execute("INSERT INTO produtos_avulsos (nome, preco, modulos_ativos) VALUES ('Mapa extra', 30, '{MAPA}')");
        var motoboys = await db.Scalar<long>("SELECT id FROM produtos_avulsos WHERE motoboys = 3");
        var modulo = await db.Scalar<long>("SELECT id FROM produtos_avulsos WHERE nome = 'Mapa extra'");
        await db.Vencer(5);

        Assert.Equal("ASS_DEVENDO", await Codigo(() => db.Assinaturas.AlterarAsync(db.Loja, new AlterarRequest { Plano = "turbo" }, 7)));
        Assert.Equal("ASS_DEVENDO", await Codigo(() => db.Assinaturas.AlterarAsync(db.Loja, new AlterarRequest { Plano = "partida", Confirmar = true }, 7)));
        Assert.Equal("ASS_MODULO_DEVENDO", await Codigo(() => db.Assinaturas.ComprarAsync(db.Loja, new CompraRequest { ProdutoAvulsoId = modulo, IdempotencyKey = "m" }, 7)));
        Assert.Equal("ASS_MODULO_DEVENDO", await Codigo(() => db.Assinaturas.SolicitarAsync(db.Loja, new SolicitacaoRequest { ProdutoAvulsoId = modulo }, 9)));
        Assert.Equal("ASS_DEVENDO", await Codigo(() => db.Assinaturas.LigarCartaoAsync(db.Loja, CartaoValido(), 7)));
        var compra = await db.Assinaturas.ComprarAsync(db.Loja, new CompraRequest { ProdutoAvulsoId = motoboys, IdempotencyKey = "mb" }, 7);
        Assert.False(compra.Cobranca.ContaComoDivida);
        var cancelada = await db.Assinaturas.CancelarAsync(db.Loja, new CancelarRequest(), 7); // sem motivo
        Assert.NotNull(cancelada.Cancelamento);
        var painel = await db.Assinaturas.PainelAsync(db.Loja, true);
        Assert.Equal("devendo", painel.Estado);
        Assert.Equal(249m, painel.Divida!.Valor); // a dívida continua; o extra aberto não entra
    }

    [DeliveryDatabaseFact]
    public async Task ExtraSemPlanoAtivoERecusado()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        var produto = await db.Scalar<long>("SELECT id FROM produtos_avulsos LIMIT 1");
        Assert.Equal("ASS_EXTRA_SEM_PLANO", await Codigo(() => db.Assinaturas.ComprarAsync(db.Loja, new CompraRequest { ProdutoAvulsoId = produto, IdempotencyKey = "a" }, 7)));
        await db.Assinaturas.ContratarAsync(db.Loja, new ContratarRequest { Plano = "rota", IdempotencyKey = "k" }, 7);
        Assert.Equal("ASS_EXTRA_SEM_PLANO", await Codigo(() => db.Assinaturas.ComprarAsync(db.Loja, new CompraRequest { ProdutoAvulsoId = produto, IdempotencyKey = "b" }, 7)));
    }

    // ---------- tentativas ----------

    [DeliveryDatabaseFact]
    public async Task UpgradeEExtraNaoPagosSomemSemVirarDividaNemMudarVencimento()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        var antes = await db.Assinar("rota");
        var upgrade = await db.Upgrade("turbo");
        var produto = await db.Scalar<long>("SELECT id FROM produtos_avulsos LIMIT 1");
        var compra = await db.Assinaturas.ComprarAsync(db.Loja, new CompraRequest { ProdutoAvulsoId = produto, IdempotencyKey = "a" }, 7);
        await db.Assinaturas.PagarAsync(db.Loja, upgrade, new PagarCobrancaRequest { Metodo = "pix" }, 7);
        var detalhe = await db.Assinaturas.CobrancaAsync(upgrade);
        Assert.NotNull(detalhe.ExpiraEm);
        Assert.False(detalhe.Atrasada);

        // Ainda dentro da hora: nada some.
        await db.Worker().RodarAsync(DateTimeOffset.UtcNow.AddMinutes(30), CancellationToken.None);
        Assert.Equal(2, await db.Scalar<long>("SELECT count(*) FROM comercial_cobranca WHERE tipo <> 'ciclo' AND status = 'aberta'"));

        await db.Worker().RodarAsync(DateTimeOffset.UtcNow.AddMinutes(70), CancellationToken.None);
        Assert.Equal(2, await db.Scalar<long>("SELECT count(*) FROM comercial_cobranca WHERE tipo <> 'ciclo' AND status = 'cancelada' AND cancelamento_motivo = 'expirada'"));
        Assert.Equal("cancelada", await db.Scalar<string>("SELECT status FROM compra_extra"));
        Assert.Equal("rota", await db.Scalar<string>("SELECT plano_chave FROM assinatura"));
        var painel = await db.Assinaturas.PainelAsync(db.Loja, true);
        Assert.Null(painel.Divida);
        Assert.Equal("em_dia", painel.Estado);
        Assert.Equal(antes.PeriodoFim, painel.Assinatura!.PeriodoFim);
        Assert.Equal("ASS_TENTATIVA_EXPIRADA", await Codigo(() => db.Assinaturas.PagarAsync(db.Loja, upgrade, new PagarCobrancaRequest { Metodo = "pix" }, 7)));
        Assert.Equal(compra.Compra.Id, (await db.Assinaturas.PainelAsync(db.Loja, true)).Compras.Single().Id); // nada apagado
    }

    // ---------- defeito 1: encerrada ----------

    [DeliveryDatabaseFact]
    public async Task EncerradaNaoVoltaComoVivaEReassinarComDividaCobraTudoJunto()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        await db.Assinar("rota");
        await db.Vencer(40);
        var divida = await db.Scalar<Guid>("SELECT id FROM comercial_cobranca WHERE tipo = 'ciclo'");
        await db.Assinaturas.CancelarAsync(db.Loja, new CancelarRequest(), 7);
        await db.Execute("UPDATE assinatura SET periodo_fim = NOW() - INTERVAL '1 day'");

        // Sem esperar a rotina: o painel já mostra a encerrada como encerrada.
        var painel = await db.Assinaturas.PainelAsync(db.Loja, true);
        Assert.Null(painel.Assinatura);
        Assert.Equal("devendo", painel.Estado);
        Assert.True(painel.Acoes.Assinar.Pode);
        Assert.False(painel.Acoes.Retomar.Pode);

        var nova = await db.Assinaturas.ContratarAsync(db.Loja, new ContratarRequest { Plano = "turbo", IdempotencyKey = "volta" }, 7);
        Assert.Equal("pendente", nova.Status);
        Assert.Equal(598m, nova.ProximaCobranca!.Valor); // 249 de dívida + 349 do primeiro período
        painel = await db.Assinaturas.PainelAsync(db.Loja, true);
        Assert.Equal("devendo", painel.Estado);
        Assert.Equal(249m, painel.Divida!.Valor);
        Assert.Equal(new[] { nova.ProximaCobranca.Id }, painel.Divida.Cobrancas.ToArray());
        Assert.Equal("agrupada", await db.Scalar<string>($"SELECT cancelamento_motivo FROM comercial_cobranca WHERE id = '{divida}'"));
        Assert.Equal("ASS_DEVENDO", await Codigo(() => db.Assinaturas.PagarAsync(db.Loja, nova.ProximaCobranca.Id,
            new PagarCobrancaRequest { Metodo = "cartao", RenovacaoAutomatica = true, AutorizoRenovacao = true, Cartao = CartaoValido().Cartao }, 7)));

        // Desistir devolve a dívida antiga como estava.
        await db.Assinaturas.CancelarAsync(db.Loja, new CancelarRequest(), 7);
        Assert.Equal("aberta", await db.Scalar<string>($"SELECT status FROM comercial_cobranca WHERE id = '{divida}'"));
        Assert.Equal(249m, (await db.Assinaturas.PainelAsync(db.Loja, true)).Divida!.Valor);

        // Assina de novo e paga: o plano só vale agora, e a dívida está quitada.
        var outra = await db.Assinaturas.ContratarAsync(db.Loja, new ContratarRequest { Plano = "turbo", IdempotencyKey = "volta2" }, 7);
        Assert.Equal("rota", await db.Scalar<string>("SELECT plano_comercial FROM estabelecimentos"));
        await db.Baixa(outra.ProximaCobranca!.Id);
        painel = await db.Assinaturas.PainelAsync(db.Loja, true);
        Assert.Equal("em_dia", painel.Estado);
        Assert.Null(painel.Divida);
        Assert.Equal("turbo", await db.Scalar<string>("SELECT plano_comercial FROM estabelecimentos"));
    }

    [DeliveryDatabaseFact]
    public async Task EncerradaSemDividaAssinaOQueQuiserInclusiveMensalVindoDoAnual()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        await db.Assinar("rota", "anual");
        Assert.Equal("ASS_ANUAL_SO_GESTAO", await Codigo(() => db.Assinaturas.AlterarAsync(db.Loja, new AlterarRequest { Plano = "rota", Periodicidade = "mensal" }, 7)));
        await db.Assinaturas.CancelarAsync(db.Loja, new CancelarRequest(), 7);
        Assert.Equal("ASS_CANCELADA_RETOME", await Codigo(() => db.Assinaturas.AlterarAsync(db.Loja, new AlterarRequest { Plano = "turbo", Periodicidade = "anual" }, 7)));
        await db.Execute("UPDATE assinatura SET periodo_fim = NOW() - INTERVAL '1 day'");
        var nova = await db.Assinaturas.ContratarAsync(db.Loja, new ContratarRequest { Plano = "partida", Periodicidade = "mensal", IdempotencyKey = "n" }, 7);
        Assert.Equal(149m, nova.ProximaCobranca!.Valor);
    }

    // ---------- defeito 2: próxima fatura já gerada ----------

    [DeliveryDatabaseFact]
    public async Task UpgradeComProximaFaturaEmAbertoValeDepoisDaVirada()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        await db.Assinar("rota");
        await db.GerarProximaFatura();
        var antiga = await db.CobrancaDoCiclo(2);
        await db.Baixa(await db.Upgrade("turbo"));
        Assert.Equal(349m, await db.ValorDoCiclo(2));
        Assert.Equal("cancelada", await db.Scalar<string>($"SELECT status FROM comercial_cobranca WHERE id = '{antiga}'"));
        await db.VirarPeriodo();
        Assert.Equal("turbo", await db.Scalar<string>("SELECT plano_chave FROM assinatura"));
        Assert.Equal("turbo", await db.Scalar<string>("SELECT plano_comercial FROM estabelecimentos"));
    }

    [DeliveryDatabaseFact]
    public async Task UpgradeComProximaFaturaPagaCobraADiferencaDelaJunto()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        await db.Assinar("rota");
        await db.GerarProximaFatura();
        await db.Baixa(await db.CobrancaDoCiclo(2));
        var previsao = await db.Assinaturas.AlterarAsync(db.Loja, new AlterarRequest { Plano = "turbo" }, 7);
        Assert.True(previsao.Previsao.ValorAgora >= 100m, $"veio {previsao.Previsao.ValorAgora}");
        await db.Baixa(await db.Upgrade("turbo"));
        await db.VirarPeriodo();
        Assert.Equal("turbo", await db.Scalar<string>("SELECT plano_chave FROM assinatura"));
        Assert.Equal(349m, await db.Scalar<decimal>("SELECT preco FROM assinatura"));
    }

    [DeliveryDatabaseFact]
    public async Task DowngradeDepoisDaProximaFaturaNaoSePerde()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        await db.Assinar("turbo");
        await db.GerarProximaFatura();
        await db.Assinaturas.AlterarAsync(db.Loja, new AlterarRequest { Plano = "partida", Confirmar = true }, 7);
        Assert.Equal(149m, await db.ValorDoCiclo(2));

        // Desfazer devolve a fatura ao plano atual; marcar de novo refaz.
        await db.Assinaturas.DesfazerTrocaAsync(db.Loja, 7);
        Assert.Equal(349m, await db.ValorDoCiclo(2));
        await db.Assinaturas.AlterarAsync(db.Loja, new AlterarRequest { Plano = "partida", Confirmar = true }, 7);
        await db.VirarPeriodo();
        Assert.Equal("partida", await db.Scalar<string>("SELECT plano_chave FROM assinatura"));
        Assert.Equal("partida", await db.Scalar<string>("SELECT plano_comercial FROM estabelecimentos"));
        Assert.True(await db.Scalar<bool>("SELECT mudanca_plano IS NULL FROM assinatura"));
    }

    [DeliveryDatabaseFact]
    public async Task DowngradeComProximaFaturaPagaValeNoCicloSeguinte()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        await db.Assinar("turbo");
        await db.GerarProximaFatura();
        await db.Baixa(await db.CobrancaDoCiclo(2));
        await db.Assinaturas.AlterarAsync(db.Loja, new AlterarRequest { Plano = "partida", Confirmar = true }, 7);
        await db.VirarPeriodo();
        Assert.Equal("turbo", await db.Scalar<string>("SELECT plano_chave FROM assinatura"));
        Assert.Equal("partida", await db.Scalar<string>("SELECT mudanca_plano FROM assinatura"));
        await db.GerarProximaFatura();
        Assert.Equal(149m, await db.ValorDoCiclo(3));
    }

    [DeliveryDatabaseFact]
    public async Task CancelarERetomarContinuaGerandoFatura()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        await db.Assinar("rota");
        await db.GerarProximaFatura();
        await db.Assinaturas.CancelarAsync(db.Loja, new CancelarRequest(), 7);
        Assert.Equal(0, await db.Scalar<long>("SELECT count(*) FROM comercial_cobranca WHERE tipo = 'ciclo' AND status = 'aberta'"));
        await db.Assinaturas.RetomarAsync(db.Loja, 7);
        Assert.Equal(249m, await db.ValorDoCiclo(2));
        Assert.Equal("aberta", await db.Scalar<string>($"SELECT status FROM comercial_cobranca WHERE id = '{await db.CobrancaDoCiclo(2)}'"));
        await db.VirarPeriodo();
        Assert.Equal(2, await db.Scalar<int>("SELECT ci.numero FROM assinatura a JOIN assinatura_ciclo ci ON ci.id = a.id_ciclo_atual"));
    }

    // ---------- defeito 3 ----------

    [DeliveryDatabaseFact]
    public async Task CancelarNaoPerdeOProximoPeriodoJaPago()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        await db.Assinar("rota");
        await db.GerarProximaFatura();
        await db.Baixa(await db.CobrancaDoCiclo(2));
        var cancelada = await db.Assinaturas.CancelarAsync(db.Loja, new CancelarRequest(), 7);
        var fimDoSegundo = await db.Scalar<DateTime>("SELECT fim FROM assinatura_ciclo WHERE numero = 2");
        Assert.Equal(fimDoSegundo, cancelada.Cancelamento!.AcessoAte!.Value.UtcDateTime);
        await db.VirarPeriodo();
        Assert.Equal("ativa", await db.Scalar<string>("SELECT status FROM assinatura"));
        Assert.Equal(2, await db.Scalar<int>("SELECT ci.numero FROM assinatura a JOIN assinatura_ciclo ci ON ci.id = a.id_ciclo_atual"));
        await db.Worker().RodarAsync(DateTimeOffset.UtcNow.AddDays(70), CancellationToken.None);
        Assert.Equal("encerrada", await db.Scalar<string>("SELECT status FROM assinatura"));
    }

    // ---------- defeito 4 ----------

    [DeliveryDatabaseFact]
    public async Task TrocaAntesDoPrimeiroPagamentoMantemOVencimento()
    {
        await using var db = await Banco.Criar(situacao: "teste");
        var a = await db.Assinaturas.ContratarAsync(db.Loja, new ContratarRequest { Plano = "rota", IdempotencyKey = "k" }, 7);
        await db.Assinaturas.AlterarAsync(db.Loja, new AlterarRequest { Plano = "turbo", Confirmar = true }, 7);
        var b = await db.Assinaturas.AssinaturaAsync(db.Loja);
        Assert.Equal(a.ProximoVencimento, b.ProximoVencimento);
        Assert.Equal(349m, b.ProximaCobranca!.Valor);
        // Mensal para anual antes de pagar: livre, mesmo vencimento, e dá para voltar.
        await db.Assinaturas.AlterarAsync(db.Loja, new AlterarRequest { Plano = "turbo", Periodicidade = "anual", Confirmar = true }, 7);
        await db.Assinaturas.AlterarAsync(db.Loja, new AlterarRequest { Plano = "partida", Periodicidade = "mensal", Confirmar = true }, 7);
        b = await db.Assinaturas.AssinaturaAsync(db.Loja);
        Assert.Equal(a.ProximoVencimento, b.ProximoVencimento);
        Assert.Equal(149m, b.ProximaCobranca!.Valor);
        Assert.Equal(1, await db.Scalar<long>("SELECT count(*) FROM comercial_cobranca WHERE status = 'aberta'"));
    }

    // ---------- defeitos 6 e 7: Pix e cartão ----------

    private static CartaoAutomaticoRequest CartaoValido() => new()
    {
        AutorizoRenovacao = true,
        Cartao = new CartaoCheckoutRequest { Token = "tok_123", PaymentMethodId = "visa", Pagador = new PagadorInfo { Email = "dono@loja.test" } }
    };

    [DeliveryDatabaseFact]
    public async Task PixSoApareceComContaDaZippyECartaoAutomaticoLigaEDesliga()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        await db.Assinar("rota");
        Assert.True((await db.Assinaturas.PainelAsync(db.Loja, true)).Checkout.PixDisponivel);

        var ligado = await db.Assinaturas.LigarCartaoAsync(db.Loja, CartaoValido(), 7);
        Assert.True(ligado.RenovacaoAutomatica);
        Assert.Equal("4242", ligado.CartaoFinal);
        var painel = await db.Assinaturas.PainelAsync(db.Loja, true);
        Assert.True(painel.Cartao.Automatico);
        Assert.Equal("4242", painel.Cartao.Final);
        Assert.EndsWith("/apps/configuracoes/assinatura", db.Mp.Preapprovals.Values.Single()["back_url"]!.GetValue<string>());

        // O valor acompanha a troca: a rotina acerta o preapproval com o preço marcado.
        await db.Baixa(await db.Upgrade("turbo"));
        await db.Worker().RodarAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(349m, db.Mp.Preapprovals.Values.Single()["auto_recurring"]!["transaction_amount"]!.GetValue<decimal>());

        var desligado = await db.Assinaturas.DesligarCartaoAsync(db.Loja, 7);
        Assert.False(desligado.RenovacaoAutomatica);
        Assert.Null(desligado.CartaoFinal);
        Assert.Equal("cancelled", db.Mp.Preapprovals.Values.Single()["status"]!.GetValue<string>());

        await db.Execute("UPDATE mp_conta_zippy SET estado = 'inativa'");
        Assert.False((await db.Assinaturas.PainelAsync(db.Loja, true)).Checkout.PixDisponivel);
    }

    // ---------- defeito 9 ----------

    [DeliveryDatabaseFact]
    public async Task QuemPediuOExtraRecebeADecisao()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        await db.Assinar("rota");
        var produto = await db.Scalar<long>("SELECT id FROM produtos_avulsos LIMIT 1");
        var s = await db.Assinaturas.SolicitarAsync(db.Loja, new SolicitacaoRequest { ProdutoAvulsoId = produto }, 9);
        await db.Assinaturas.DecidirAsync(db.Loja, s.Id, new DecisaoRequest { Aprovar = false, Motivo = "agora não" }, 7);
        var painel = await db.Assinaturas.PainelAsync(db.Loja, false);
        Assert.Equal("recusada", painel.Solicitacoes.Single().Status);
        Assert.Contains(painel.Avisos, a => a.Tipo == "solicitacao_decidida" && a.Mensagem.Contains("agora não"));
        await db.Execute("UPDATE solicitacao_compra SET decidido_em = NOW() - INTERVAL '31 days'");
        Assert.Empty((await db.Assinaturas.PainelAsync(db.Loja, false)).Solicitacoes);
    }

    // ---------- Gestão ----------

    [DeliveryDatabaseFact]
    public async Task GestaoCriaPassaAnualParaMensalECancelaComRegistro()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        var criada = await db.Gestao.CriarAsync(db.Loja, new GestaoCriarAssinaturaRequest { Plano = "rota", Periodicidade = "anual" }, 1);
        Assert.Equal("ativa", criada.Status);
        Assert.Equal(2490m, criada.ProximaCobranca!.Valor);
        await db.Baixa(criada.ProximaCobranca.Id);

        var previsao = await db.Gestao.AlterarAsync(db.Loja,
            new GestaoAlterarAssinaturaRequest { Plano = "rota", Periodicidade = "mensal", Quando = "proximo_ciclo" }, 1);
        Assert.False(previsao.Confirmada);
        Assert.Contains(previsao.Previsao.Explicacao, e => e.Contains("só a Gestão"));
        await db.Gestao.AlterarAsync(db.Loja,
            new GestaoAlterarAssinaturaRequest { Plano = "rota", Periodicidade = "mensal", Quando = "proximo_ciclo", Confirmar = true }, 1);
        Assert.Equal("mensal", await db.Scalar<string>("SELECT mudanca_periodicidade FROM assinatura"));

        var agora = await db.Gestao.AlterarAsync(db.Loja,
            new GestaoAlterarAssinaturaRequest { Plano = "rota", Periodicidade = "mensal", Quando = "agora", Confirmar = true, Motivo = "pedido do dono" }, 1);
        Assert.Equal("mensal", agora.Assinatura!.Periodicidade);
        Assert.Equal(249m, agora.Assinatura.ProximaCobranca!.Valor);

        var cancelada = await db.Gestao.CancelarAsync(db.Loja, new GestaoCancelarAssinaturaRequest { Quando = "fim_do_periodo" }, 1);
        Assert.NotNull(cancelada.Cancelamento);
        await db.Gestao.RetomarAsync(db.Loja, new GestaoMotivoRequest(), 1);
        await db.Gestao.CancelarAsync(db.Loja, new GestaoCancelarAssinaturaRequest { Quando = "agora" }, 1);
        Assert.Equal("encerrada", await db.Scalar<string>("SELECT status FROM assinatura"));
        Assert.True(await db.Scalar<long>("SELECT count(*) FROM gestao_acao_log WHERE usuario_id = 1") >= 5);
        Assert.Equal("ativo", await db.Scalar<string>("SELECT situacao_comercial FROM estabelecimentos")); // nada bloqueia
    }

    [DeliveryDatabaseFact]
    public async Task GestaoTrocaDevendoEUpgradeSoValeQuandoPago()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        await db.Assinar("rota");
        await db.GerarProximaFatura();
        await db.Execute("UPDATE comercial_cobranca SET vencimento = NOW() - INTERVAL '9 days' WHERE status = 'aberta'");
        var r = await db.Gestao.AlterarAsync(db.Loja, new GestaoAlterarAssinaturaRequest { Plano = "turbo", Confirmar = true }, 1);
        Assert.Contains(r.Previsao.Explicacao, e => e.Contains("deve"));
        Assert.NotNull(r.Cobranca);
        Assert.True(r.Cobranca!.ExpiraEm > DateTimeOffset.UtcNow.AddDays(6));
        Assert.Equal("rota", await db.Scalar<string>("SELECT plano_chave FROM assinatura"));
        var cortesia = await db.Gestao.AlterarAsync(db.Loja, new GestaoAlterarAssinaturaRequest { Plano = "turbo", Cortesia = true, Confirmar = true }, 1);
        Assert.Equal("turbo", cortesia.Assinatura!.Plano.Chave);
        Assert.Equal(0m, cortesia.Assinatura.Preco);
        Assert.Equal("turbo", await db.Scalar<string>("SELECT plano_comercial FROM estabelecimentos"));
    }

    [DeliveryDatabaseFact]
    public async Task GestaoDaExtrasCancelaMudaVencimentoEDevolveExcedente()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        var produto = await db.Scalar<long>("SELECT id FROM produtos_avulsos LIMIT 1");
        var presente = await db.Gestao.ExtraAsync(db.Loja, new GestaoExtraRequest { Motoboys = 2, Dias = 10, Cortesia = true }, 1);
        Assert.NotNull(presente.PacoteId);
        var cortesia = await db.Gestao.ExtraAsync(db.Loja, new GestaoExtraRequest { ProdutoAvulsoId = produto, Dias = 15, Cortesia = true }, 1);
        Assert.Equal("ativa", cortesia.Compra!.Status);
        Assert.Equal(0m, cortesia.Compra.ValorTotal);
        Assert.NotNull(cortesia.PacoteId);
        var paga = await db.Gestao.ExtraAsync(db.Loja, new GestaoExtraRequest { ProdutoAvulsoId = produto, Dias = 30 }, 1);
        Assert.Equal("aguardando_pagamento", paga.Compra!.Status);
        await db.Gestao.CancelarCobrancaAsync(paga.Cobranca!.Id, new GestaoMotivoRequest(), 1);
        Assert.Equal("cancelada", await db.Scalar<string>($"SELECT status FROM compra_extra WHERE id = '{paga.Compra.Id}'"));
        Assert.Equal(3, await db.Scalar<long>("SELECT count(*) FROM compra_extra UNION ALL SELECT 0 LIMIT 1") + 1);

        var assinatura = await db.Gestao.CriarAsync(db.Loja, new GestaoCriarAssinaturaRequest { Plano = "rota" }, 1);
        var mensalidade = assinatura.ProximaCobranca!.Id;
        var nova = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(12);
        var movida = await db.Gestao.VencimentoAsync(mensalidade, new GestaoVencimentoRequest { Vencimento = nova.ToString("yyyy-MM-dd") }, 1);
        Assert.Equal(nova, FusoComercial.Data(movida.Vencimento));
        await db.Gestao.CancelarCobrancaAsync(mensalidade, new GestaoMotivoRequest { Motivo = "mês de presente" }, 1);
        var painel = await db.Assinaturas.PainelAsync(db.Loja, true);
        Assert.Equal("sem_cobranca", painel.MesVigente);
        Assert.Equal("em_dia", painel.Estado);

        // Pagamento em cima de cobrança cancelada: aparece para devolver e some depois de marcado.
        await db.Execute($"UPDATE comercial_cobranca SET valor_excedente = 249, excedente_em = NOW() WHERE id = '{mensalidade}'");
        var excedente = Assert.Single(await db.Gestao.ExcedentesAsync());
        Assert.Equal(249m, excedente.Valor);
        await db.Gestao.ExcedenteDevolvidoAsync(mensalidade, new GestaoMotivoRequest(), 1);
        Assert.Empty(await db.Gestao.ExcedentesAsync());
    }

    [DeliveryDatabaseFact]
    public async Task ListaDaGestaoFiltraPorEstadoMesEBusca()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        await db.Assinar("rota");
        await db.Vencer(3);
        var linha = Assert.Single(await db.Gestao.ListarAsync("devendo", "atrasado", "loj"));
        Assert.Equal("rota", linha.Plano!.Chave);
        Assert.Equal(249m, linha.Divida!.Valor);
        Assert.False(linha.Bloqueada);
        Assert.Empty(await db.Gestao.ListarAsync("em_dia", null, null));
        Assert.Empty(await db.Gestao.ListarAsync(null, null, "outra"));
    }

    [DeliveryDatabaseFact]
    public async Task AtivarCriaAssinaturaEBloquearPodePararACobranca()
    {
        await using var db = await Banco.Criar(situacao: "teste");
        var situacao = db.Situacao();
        await situacao.AplicarAsync(db.Loja, new AcaoSituacaoRequest("ativar", null, "rota", null), 1);
        Assert.Equal("ativa", await db.Scalar<string>("SELECT status FROM assinatura"));
        Assert.Equal(249m, await db.Scalar<decimal>("SELECT valor FROM comercial_cobranca WHERE status = 'aberta'"));

        // Mudar o plano atualiza a assinatura e a mensalidade em aberto: nada é desfeito depois.
        await situacao.AplicarAsync(db.Loja, new AcaoSituacaoRequest("ativar", null, "turbo", null), 1);
        Assert.Equal("turbo", await db.Scalar<string>("SELECT plano_chave FROM assinatura"));
        Assert.Equal(349m, await db.Scalar<decimal>("SELECT valor FROM comercial_cobranca WHERE status = 'aberta'"));
        await db.Baixa(await db.Scalar<Guid>("SELECT id FROM comercial_cobranca WHERE status = 'aberta'"));
        Assert.Equal("turbo", await db.Scalar<string>("SELECT plano_comercial FROM estabelecimentos"));

        await db.GerarProximaFatura();
        await situacao.AplicarAsync(db.Loja, new AcaoSituacaoRequest("bloquear", null, null, null), 1); // a cobrança continua
        Assert.Equal("ativa", await db.Scalar<string>("SELECT status FROM assinatura"));
        await situacao.AplicarAsync(db.Loja, new AcaoSituacaoRequest("bloquear", null, null, null, PararCobranca: true), 1);
        Assert.Equal("encerrada", await db.Scalar<string>("SELECT status FROM assinatura"));
        Assert.Equal(0, await db.Scalar<long>("SELECT count(*) FROM comercial_cobranca WHERE status = 'aberta'"));
        var painel = await db.Assinaturas.PainelAsync(db.Loja, true);
        Assert.Equal(1, painel.Bloqueio!.Por!.Id == 1 ? 1 : 0);

        // Pagar não desbloqueia; só a Gestão.
        Assert.Equal("bloqueado", await db.Scalar<string>("SELECT situacao_comercial FROM estabelecimentos"));
        await situacao.AplicarAsync(db.Loja, new AcaoSituacaoRequest("desbloquear", null, null, null), 1);
        Assert.Equal("ativo", await db.Scalar<string>("SELECT situacao_comercial FROM estabelecimentos"));
    }

    [DeliveryDatabaseFact]
    public async Task AtivarComCortesiaNaoCobraEAPrimeiraCobrancaPodeSerAmanha()
    {
        await using var db = await Banco.Criar(situacao: "teste");
        await db.Situacao().AplicarAsync(db.Loja, new AcaoSituacaoRequest("ativar", null, "rota", null, Cortesia: true), 1);
        Assert.Equal(0m, await db.Scalar<decimal>("SELECT preco FROM assinatura"));
        Assert.Equal(0, await db.Scalar<long>("SELECT count(*) FROM comercial_cobranca"));

        await using var outro = await Banco.Criar(situacao: "teste");
        var amanha = FusoComercial.Data(DateTimeOffset.UtcNow).AddDays(1);
        await using (var cx = await outro.Abrir())
        await using (var tx = await cx.BeginTransactionAsync())
        {
            await AssinaturaOperacoes.AtivarPelaGestaoAsync(cx, tx, outro.Loja, "rota", null, false, amanha, 1, CancellationToken.None);
            await tx.CommitAsync();
        }
        var vencimento = await outro.Scalar<DateTime>("SELECT vencimento FROM comercial_cobranca");
        Assert.Equal(amanha, FusoComercial.Data(new DateTimeOffset(vencimento, TimeSpan.Zero)));
    }

    // ---------- catálogo ----------

    [DeliveryDatabaseFact]
    public async Task CatalogoMantemModulosDeExtrasEProtegePlanoEmUso()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        await db.Assinar("rota");
        await db.Execute("INSERT INTO produtos_avulsos (nome, preco, modulos_ativos) VALUES ('Financeiro', 30, '{FINANCEIRO}')");
        var extra = await db.Scalar<long>("SELECT id FROM produtos_avulsos WHERE nome = 'Financeiro'");
        await db.Gestao.ExtraAsync(db.Loja, new GestaoExtraRequest { ProdutoAvulsoId = extra, Dias = 30, Cortesia = true }, 1);
        var catalogo = db.Catalogo();
        await catalogo.AtualizarAsync("rota", new EditarPacoteComercialRequest(new[] { "Pedidos" }, 1, PrecoAnual: 0), 1);
        Assert.True(await db.Scalar<bool>("SELECT 'FINANCEIRO' = ANY(modulos_ativos) FROM estabelecimentos"));
        Assert.True(await db.Scalar<bool>("SELECT preco_anual IS NULL FROM pacotes_comerciais WHERE chave = 'rota'"));
        await catalogo.SalvarAvulsoAsync(extra, new SalvarProdutoAvulsoRequest("Financeiro", 35, Array.Empty<string>(), 0, null));
        Assert.True(await db.Scalar<bool>($"SELECT 'FINANCEIRO' = ANY(modulos_ativos) FROM produtos_avulsos WHERE id = {extra}"));

        // Plano marcado numa troca também não pode ser excluído, mesmo sem nenhuma loja nele.
        await db.Assinaturas.AlterarAsync(db.Loja, new AlterarRequest { Plano = "partida", Confirmar = true }, 7);
        await Assert.ThrowsAsync<SituacaoComercialException>(() => catalogo.ExcluirAsync("partida"));
        await catalogo.ExcluirAsync("turbo");
    }

    // ---------- migração e limpeza ----------

    [DeliveryDatabaseFact]
    public async Task MigracaoVerificaDesfazEALimpezaSoCancelaTentativasAntigas()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        await db.Assinar("rota");
        var pasta = Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery");
        var migracao = File.ReadAllText(Path.Combine(pasta, "20261210_01_assinatura_regras.sql"));
        await db.Execute("UPDATE assinatura SET financeiro = 'suspensa', suspensa_em = NOW()");
        await db.Execute(migracao); // de novo: idempotente
        Assert.Equal("em_atraso", await db.Scalar<string>("SELECT financeiro FROM assinatura"));
        var verificacao = File.ReadAllText(Path.Combine(pasta, "20261210_02_assinatura_regras_verify.sql"));
        Assert.True(await db.Scalar<bool>($"SELECT bool_and(ok) FROM ({verificacao.TrimEnd().TrimEnd(';')}) v"));

        var antiga = await db.Upgrade("turbo");
        await db.Execute("UPDATE comercial_cobranca SET expira_em = NULL WHERE tipo = 'upgrade'");
        await db.Worker().RodarAsync(DateTimeOffset.UtcNow.AddDays(2), CancellationToken.None);
        Assert.Equal("aberta", await db.Scalar<string>($"SELECT status FROM comercial_cobranca WHERE id = '{antiga}'")); // o servidor não mexe

        await db.Execute("UPDATE assinatura SET financeiro = 'em_atraso'"); // a rotina já tinha recalculado
        await db.Execute(File.ReadAllText(Path.Combine(db.RaizDoProjeto(), "Migrations", "Delivery", "rollback", "20261210_01_assinatura_regras.down.sql")));
        Assert.Equal("suspensa", await db.Scalar<string>("SELECT financeiro FROM assinatura"));
        await db.Execute(migracao);
        Assert.Equal("em_atraso", await db.Scalar<string>("SELECT financeiro FROM assinatura"));

        var limpeza = File.ReadAllText(Path.Combine(db.RaizDoProjeto(), "Migrations", "Delivery", "manual", "20261210_limpeza_tentativas_antigas.sql"));
        var passo2 = limpeza[(limpeza.IndexOf("/*", StringComparison.Ordinal) + 2)..limpeza.LastIndexOf("*/", StringComparison.Ordinal)];
        await db.Execute(passo2);
        Assert.Equal("cancelada", await db.Scalar<string>($"SELECT status FROM comercial_cobranca WHERE id = '{antiga}'"));
        Assert.Equal("paga", await db.Scalar<string>("SELECT status FROM comercial_cobranca WHERE tipo = 'ciclo'"));
    }
}
