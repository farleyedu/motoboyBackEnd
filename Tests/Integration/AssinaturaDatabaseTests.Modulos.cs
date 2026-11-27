using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.Assinaturas;
using APIBack.Service;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>Módulo avulso por loja (Gestão): o ajuste sobrevive à troca de plano e ao salvamento do cadastro.</summary>
public sealed partial class AssinaturaDatabaseTests
{
    private static ModulosDaLojaService Modulos(Banco db) => new(db.Get<NpgsqlDataSource>(), db.Get<IMemoryCache>());

    private static async Task Aplicar(Banco db, string plano)
    {
        await using var cx = await db.Abrir();
        await using var tx = await cx.BeginTransactionAsync();
        await AssinaturaSql.AplicarPlanoAsync(cx, tx, db.Loja, plano, "usuario", 1, CancellationToken.None);
        await tx.CommitAsync();
    }

    private static Task<bool> Tem(Banco db, string modulo) =>
        db.Scalar<bool>($"SELECT '{modulo}'::modulo_enum = ANY (modulos_ativos) FROM estabelecimentos");

    [DeliveryDatabaseFact]
    public async Task ModuloLigadoPelaGestaoContinuaDepoisDeTrocarDePlanoESemMudarOPlano()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        await Aplicar(db, "rota");

        var dto = await Modulos(db).AlterarAsync(db.Loja, "Financeiro", new AlterarModuloRequest { Ligado = true, Motivo = "cortesia" }, 1);

        Assert.Contains("Financeiro", dto.Ligados);
        Assert.Equal(new[] { "Financeiro" }, dto.LigadosPelaGestao);
        Assert.Equal("rota", await db.Scalar<string>("SELECT plano_comercial FROM estabelecimentos"));
        Assert.Equal(1, await db.Scalar<long>("SELECT count(*) FROM gestao_acao_log WHERE acao = 'modulo_ligado'"));

        await Aplicar(db, "partida");
        Assert.True(await Tem(db, "FINANCEIRO"));
        Assert.False(await Tem(db, "MAPA"));

        // Salvar o cadastro com a lista antiga não apaga o ajuste.
        await db.Execute("UPDATE estabelecimentos SET modulos_ativos = '{GERAL,PEDIDOS}'");
        Assert.True(await Tem(db, "FINANCEIRO"));
    }

    [DeliveryDatabaseFact]
    public async Task ModuloDoPlanoDesligadoPelaGestaoNaoVoltaSozinhoEPodeSerReligado()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        await Aplicar(db, "rota");
        var svc = Modulos(db);

        var dto = await svc.AlterarAsync(db.Loja, "Mapa", new AlterarModuloRequest { Ligado = false }, 1);
        Assert.Equal(new[] { "Mapa" }, dto.DesligadosPelaGestao);
        Assert.Contains("Mapa", dto.DoPlano);

        await Aplicar(db, "turbo");
        Assert.False(await Tem(db, "MAPA"));
        Assert.True(await Tem(db, "DELIVERY"));

        // Ligar de novo tira o ajuste: o módulo volta a depender só do plano.
        dto = await svc.AlterarAsync(db.Loja, "Mapa", new AlterarModuloRequest { Ligado = true }, 1);
        Assert.Empty(dto.DesligadosPelaGestao);
        Assert.Empty(dto.LigadosPelaGestao);
        await Aplicar(db, "partida");
        Assert.False(await Tem(db, "MAPA"));
    }

    [DeliveryDatabaseFact]
    public async Task LigarLevaOQueOModuloPrecisaEDesligarOExigidoEhRecusado()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        await Aplicar(db, "partida");
        var svc = Modulos(db);

        var dto = await svc.AlterarAsync(db.Loja, "Delivery", new AlterarModuloRequest { Ligado = true }, 1);
        Assert.Equal(new[] { "Delivery", "Mapa" }, dto.LigadosPelaGestao);

        var recusa = await Assert.ThrowsAsync<ModuloDaLojaException>(() =>
            svc.AlterarAsync(db.Loja, "Mapa", new AlterarModuloRequest { Ligado = false }, 1));
        Assert.Equal(409, recusa.Status);
        Assert.Equal(ModulosDaLojaService.CodigoExigido, recusa.Codigo);
        Assert.Contains("Delivery", recusa.Message);

        // Módulo que só estava ligado pela Gestão some sem deixar lembrança.
        await svc.AlterarAsync(db.Loja, "Delivery", new AlterarModuloRequest { Ligado = false }, 1);
        dto = await svc.AlterarAsync(db.Loja, "Mapa", new AlterarModuloRequest { Ligado = false }, 1);
        Assert.Empty(dto.LigadosPelaGestao);
        Assert.Empty(dto.DesligadosPelaGestao);

        var invalido = await Assert.ThrowsAsync<ModuloDaLojaException>(() =>
            svc.AlterarAsync(db.Loja, "GERAL", new AlterarModuloRequest { Ligado = false }, 1));
        Assert.Equal(422, invalido.Status);
    }

    [DeliveryDatabaseFact]
    public async Task ExtraQuePassaAValerReligaOModuloDesligado()
    {
        await using var db = await Banco.Criar(situacao: "ativo");
        await Aplicar(db, "rota");
        await Modulos(db).AlterarAsync(db.Loja, "Mapa", new AlterarModuloRequest { Ligado = false }, 1);

        var compra = Guid.NewGuid();
        await db.Execute(@"
INSERT INTO compra_extra (id, id_estabelecimento, produto_avulso_id, nome, preco_unitario, quantidade, valor_total, motoboys, modulos_ativos, status, idempotency_key)
VALUES (@Id, @Loja, (SELECT min(id) FROM produtos_avulsos), 'Mapa', 10, 1, 10, 0, '{MAPA}', 'aguardando_pagamento', 'modulo')", new { Id = compra, db.Loja });
        Assert.False(await Tem(db, "MAPA"));

        await db.Execute("UPDATE compra_extra SET status = 'ativa', vigencia_inicio = NOW(), vigencia_fim = NOW() + INTERVAL '30 days'");
        await db.Execute("UPDATE estabelecimentos SET modulos_ativos = modulos_ativos || '{MAPA}'::modulo_enum[]");

        Assert.True(await Tem(db, "MAPA"));
        Assert.Empty((await Modulos(db).ObterAsync(db.Loja)).DesligadosPelaGestao);
    }
}
