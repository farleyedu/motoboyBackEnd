using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Acompanhamento.Saude;
using APIBack.Payments.Assinaturas;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>Carteira, Dinheiro, linha do tempo e saúde das lojas lendo só a assinatura.</summary>
public sealed partial class AssinaturaDatabaseTests
{
    private const string TabelasDaGestao = @"
CREATE TABLE empresas (id uuid PRIMARY KEY, nome_fantasia text);
CREATE TABLE pedido (id_estabelecimento uuid, data_pedido timestamptz, origem text, status_pedido int);
CREATE TABLE crm_contrato (id uuid PRIMARY KEY, estabelecimento_id uuid, status text, id_oportunidade uuid, mensalidade_saas numeric DEFAULT 0);
CREATE TABLE crm_lancamento (contrato_id uuid, data_pagamento date, data_vencimento date, status text, descricao text, valor_pago numeric, valor_total numeric);
CREATE TABLE zippy_conta (id bigserial PRIMARY KEY, valor numeric, valor_pago numeric, status text, pago_em timestamptz, categoria text, vencimento date);
CREATE TABLE gestao_nota (id bigserial PRIMARY KEY, estabelecimento_id uuid, concluida_em timestamptz, criado_em timestamptz DEFAULT NOW(), texto text, autor_id int);
CREATE TABLE acompanhamento (id uuid PRIMARY KEY, oportunidade_id uuid, estabelecimento_id uuid, fase text, fase_desde timestamptz, faixa text, pausa_ate date, criado_em timestamptz);
CREATE TABLE acompanhamento_passagem (acompanhamento_id uuid, criada_em timestamptz, autor_tipo text);
CREATE TABLE acompanhamento_historico (acompanhamento_id uuid, em timestamptz, autor_tipo text);
CREATE TABLE acompanhamento_proximo_passo (acompanhamento_id uuid, realizado_em timestamptz);
CREATE TABLE chamado (id uuid PRIMARY KEY, estabelecimento_id uuid, criado_em timestamptz, prazo_em timestamptz, status text);
CREATE TABLE chamado_mensagem (chamado_id uuid, criado_em timestamptz, da_zippy boolean);";

    [DeliveryDatabaseFact]
    public async Task CarteiraDinheiroESaudeVemDaAssinaturaETentativaNaoEAtraso()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        await db.Execute(TabelasDaGestao);
        // Contrato antigo do CRM: não pode mais virar mensalidade nem receita.
        await db.Execute("INSERT INTO crm_contrato (id, estabelecimento_id, status, mensalidade_saas) VALUES (gen_random_uuid(), @L, 'ativo', 999)", new { L = db.Loja });
        await db.Assinar("rota", "anual");
        await db.Upgrade("turbo", "anual");
        await db.Execute("UPDATE comercial_cobranca SET vencimento = NOW() - INTERVAL '20 days', expira_em = NULL WHERE tipo = 'upgrade'");
        var central = db.Central();

        var loja = Assert.Single(await central.CarteiraAsync());
        Assert.Equal("em_dia", loja.EstadoAssinatura);
        Assert.Equal("pago", loja.MesVigente);
        Assert.Equal(207.50m, loja.Mensalidade); // 2490 / 12
        Assert.Null(loja.Divida);
        Assert.Equal(0m, loja.ValorAtrasado);
        Assert.Equal(1, loja.Contratos);
        Assert.NotNull(loja.Saude);
        Assert.DoesNotContain(loja.Saude!.Motivos, m => m.Codigo == "pagamento_atrasado");

        var hoje = FusoComercial.Data(DateTimeOffset.UtcNow);
        var mes = await central.DinheiroAsync(new DateOnly(hoje.Year, hoje.Month, 1));
        Assert.Equal(207.50m, mes.ReceitaRecorrente);
        Assert.Equal(207.50m, mes.Novas);
        Assert.Equal(0m, mes.Perdidas);
        Assert.Equal(0m, mes.Atrasado);
        Assert.Equal(0m, mes.AReceber);
        Assert.Equal(2490m, mes.Entrou);
        Assert.DoesNotContain(await central.LinhaDoTempoAsync(db.Loja), i => i.Titulo == "atrasado");

        await db.Gestao.CancelarAsync(db.Loja, new GestaoCancelarAssinaturaRequest { Quando = "agora" }, 1);
        mes = await central.DinheiroAsync(new DateOnly(hoje.Year, hoje.Month, 1));
        Assert.Equal(0m, mes.ReceitaRecorrente);
        Assert.Equal(207.50m, mes.Perdidas);
        loja = Assert.Single(await central.CarteiraAsync());
        Assert.Equal("encerrada", loja.EstadoAssinatura);
        Assert.Null(loja.Mensalidade);
        Assert.Contains(loja.Saude!.Motivos, m => m.Codigo == "assinatura_encerrada_em_uso");
    }

    [DeliveryDatabaseFact]
    public async Task SaudeAlertaDevendoELojaAtivaSemAssinatura()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        await db.Execute(TabelasDaGestao);
        var central = db.Central();
        Assert.Contains(Assert.Single(await central.CarteiraAsync()).Saude!.Motivos, m => m.Codigo == "ativa_sem_assinatura");

        await db.Assinar("rota");
        await db.Vencer(4);
        var loja = Assert.Single(await central.CarteiraAsync());
        Assert.Equal(249m, loja.ValorAtrasado);
        Assert.Equal("atrasado", loja.MesVigente);
        Assert.Contains(loja.Saude!.Motivos, m => m.Codigo == "pagamento_atrasado");
        Assert.Equal(249m, (await central.DinheiroAsync(FusoComercial.Data(DateTimeOffset.UtcNow))).Atrasado);
        await using var cx = await db.Abrir();
        var entrada = Assert.Single(await SaudeSql.EntradasDasLojasAsync(cx, DateTimeOffset.UtcNow, db.Loja, CancellationToken.None));
        Assert.Equal("viva", entrada.SituacaoDaAssinatura);
    }
}
