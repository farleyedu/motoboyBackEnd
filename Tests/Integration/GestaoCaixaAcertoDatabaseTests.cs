using APIBack.Acontecimentos;
using APIBack.DTOs.Delivery;
using APIBack.GestaoLogica.Caixa;
using APIBack.GestaoLogica.Vendas;
using APIBack.Repository;
using APIBack.Security;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// 6C5 contra PostgreSQL de verdade: o dinheiro da entrega fica com o motoboy até o acerto; o lote (parcial ou total) liquida para a
/// gaveta numa transação só, lança a diferença com motivo, não acerta o mesmo pedido duas vezes e a correção vira lote novo.
/// </summary>
public partial class DeliverySyncDatabaseTests
{
    private static decimal Gaveta(IReadOnlyList<ContaDto> contas) => contas.Single(k => k.Nome == "Gaveta do caixa").Saldo;

    [DeliveryDatabaseFact]
    public async Task Gestao6C5_acerto_parcial_com_diferenca_cartao_conferido_e_correcao_em_lote_novo()
    {
        await using var db = await VendaDatabase();
        var livro = Livro(db.Source);
        var caixa = new CaixaService(db.Source, livro);
        var gerente = GestaoPapeis.Resolver((_, _) => true, false);
        var operador = GestaoPapeis.Resolver((m, _) => m is "Caixa", false);
        var hoje = await caixa.Hoje(db.StoreId, default);

        // Pedido 23 pelo app (dinheiro 66,90 + Pix 20); pedido 24 com cartão na maquininha da loja, também com o motoboy 1.
        await new PedidoQueueRepository(db.Source).CompleteDeliveryAsync(db.StoreId, 1, 7, db.SessionId, 4, CompletionRequest(), default);
        await using (var c = await db.Source.OpenConnectionAsync())
        {
            await using var tx = await c.BeginTransactionAsync();
            await VendaDoPedido.PublicarAsync(livro, c, tx, db.StoreId, 24, 7, ModalidadeVenda.Delivery,
                new[] { new DeliveryPaymentPart { Method = "cartao_entrega", Amount = 64, ReceivedConfirmed = true } }, motoboyId: 1);
            await tx.CommitAsync();
        }
        Assert.Equal(100m, Gaveta(await livro.Contas(db.StoreId, default)));

        var mb = Assert.Single(await caixa.MotoboysDoDia(db.StoreId, hoje, default), m => m.MotoboyId == 1);
        Assert.Equal((66.90m, 64m, 20m), (mb.DinheiroCom, mb.CartaoAConferir, mb.Integracao));
        Assert.Equal(new[] { 23, 24 }, mb.Pendentes.Select(e => e.PedidoId).OrderBy(x => x));
        Assert.Null(mb.Chegada); // a chegada vem da 6C6

        // Não bateu sem motivo é recusado; com motivo, lote parcial (o 24 fica com o motoboy) e a falta vira quebra.
        Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() => caixa.Acertar(db.StoreId, 7, operador,
            new AcertoRequest("a1", hoje, 1, new[] { new ItemAcertoRequest(23) }, 60m, null), default))).Codigo);
        var lote = await caixa.Acertar(db.StoreId, 7, operador, new AcertoRequest("a1", hoje, 1, new[] { new ItemAcertoRequest(23) }, 60m, "faltou troco"), default);
        Assert.Equal(("L-0001", 66.90m, 60m, -6.90m, "parcial", 1), (lote.Numero, lote.Esperado, lote.Entregue, lote.Diferenca, lote.Situacao, lote.PendentesDepois));
        Assert.Equal(new ItemAcertoDto(23, 66.90m, 20m, 0m, 0m, false), Assert.Single(lote.Itens));
        Assert.Equal(160m, Gaveta(await livro.Contas(db.StoreId, default))); // 100 + 66,90 − 6,90

        // Reenvio igual devolve o mesmo lote; mesma chave com outro conteúdo, recusado; outra tela com dado velho, 409.
        Assert.Equal(lote.Id, (await caixa.Acertar(db.StoreId, 7, operador, new AcertoRequest("a1", hoje, 1, new[] { new ItemAcertoRequest(23) }, 60m, "faltou troco"), default)).Id);
        Assert.Equal("IDEMPOTENCIA_CONFLITO", (await Assert.ThrowsAsync<GestaoException>(() => caixa.Acertar(db.StoreId, 7, operador,
            new AcertoRequest("a1", hoje, 1, new[] { new ItemAcertoRequest(23) }, 66.90m, null), default))).Codigo);
        Assert.Equal("ACERTO_DESATUALIZADO", (await Assert.ThrowsAsync<GestaoException>(() => caixa.Acertar(db.StoreId, 7, operador,
            new AcertoRequest("a2", hoje, 1, new[] { new ItemAcertoRequest(23) }, 66.90m, null), default))).Codigo);
        Assert.Equal(160m, Gaveta(await livro.Contas(db.StoreId, default)));

        // A fita mostra o dinheiro do acerto como venda e a falta como saída; a venda "com o motoboy" não aparece antes.
        var fita = (await caixa.CarregarDia(db.StoreId, hoje, default))!.Fita;
        Assert.Contains(fita, l => l.Tipo == "venda" && l.Forma == FormaCaixa.Dinheiro && l.Valor == 66.90m && l.Descricao.Contains("Acerto L-0001"));
        Assert.Contains(fita, l => l.Valor == -6.90m);
        Assert.Single(fita, l => l.Valor == 66.90m);

        // Cartão: o comprovante não confirma sozinho; conferido na maquininha, entra (sem dinheiro a devolver) e fecha o motoboy.
        Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() => caixa.Acertar(db.StoreId, 7, operador,
            new AcertoRequest("a3", hoje, 1, new[] { new ItemAcertoRequest(24) }, 0m, null), default))).Codigo);
        var lote2 = await caixa.Acertar(db.StoreId, 7, operador, new AcertoRequest("a3", hoje, 1, new[] { new ItemAcertoRequest(24, true) }, 0m, null), default);
        Assert.Equal(("L-0002", "conferido", 0m, 64m, true), (lote2.Numero, lote2.Situacao, lote2.Esperado, lote2.Itens[0].CartaoManual, lote2.Itens[0].ConferidoManual));
        var situacao = await caixa.SituacaoDoMotoboy(db.StoreId, 1, default);
        Assert.Equal((true, 0, "L-0002"), (situacao.TudoAcertado, situacao.Pendentes, situacao.UltimoLote!.Numero));

        // Correção (só o gerente): lote novo aponta o original, a diferença nova vai para o livro e o original não muda.
        Assert.Equal("SEM_PERMISSAO", (await Assert.ThrowsAsync<GestaoException>(() => caixa.CorrigirAcerto(db.StoreId, 7, operador, lote.Id,
            new CorrigirAcertoRequest("c1", 66.90m, "achou a nota"), default))).Codigo);
        var correcao = await caixa.CorrigirAcerto(db.StoreId, 7, gerente, lote.Id, new CorrigirAcertoRequest("c1", 66.90m, "achou a nota na mochila"), default);
        Assert.Equal(("correcao", lote.Id, 0m), (correcao.Tipo, correcao.CorrigeId, correcao.Diferenca));
        Assert.Equal(166.90m, Gaveta(await livro.Contas(db.StoreId, default)));
        var doDia = await caixa.Acertos(db.StoreId, hoje, default);
        Assert.Equal((60m, correcao.Id), (doDia.Single(l => l.Id == lote.Id).Entregue, doDia.Single(l => l.Id == lote.Id).CorrigidoPor));
        Assert.Equal("JA_CORRIGIDO", (await Assert.ThrowsAsync<GestaoException>(() => caixa.CorrigirAcerto(db.StoreId, 7, gerente, lote.Id,
            new CorrigirAcertoRequest("c2", 70m, "de novo"), default))).Codigo);

        // O fechamento conta com a gaveta certa: 166,90.
        var contagem = new ContagemCaixa(new Dictionary<int, int> { [100] = 1, [50] = 1, [10] = 1, [5] = 1 }, 1.90m);
        Assert.Equal(0m, (await caixa.ConferirContagem(db.StoreId, 7, hoje, contagem, default)).Diferenca);
    }

    [DeliveryDatabaseFact]
    public async Task Gestao6C5_recebimento_no_caixa_quita_a_venda_que_ficou_a_receber_uma_vez()
    {
        await using var db = await VendaDatabase();
        var livro = Livro(db.Source);
        var caixa = new CaixaService(db.Source, livro);
        var operador = GestaoPapeis.Resolver((m, _) => m is "Caixa", false);
        var hoje = await caixa.Hoje(db.StoreId, default);
        // Pedido 24 concluído sem forma de pagamento (sem motoboy): a venda fica a receber, sem conta.
        await using (var c = await db.Source.OpenConnectionAsync())
        {
            await using var tx = await c.BeginTransactionAsync();
            await VendaDoPedido.PublicarAsync(livro, c, tx, db.StoreId, 24, 7, ModalidadeVenda.Salao);
            await tx.CommitAsync();
        }
        var aReceber = Assert.Single(await caixa.AReceberNoCaixa(db.StoreId, default));
        Assert.Equal((24, 64m), (aReceber.PedidoId, aReceber.AReceber));
        Assert.DoesNotContain((await caixa.CarregarDia(db.StoreId, hoje, default))!.Fita, l => l.Valor == 64m);

        Assert.Equal("CAMPO_INVALIDO", (await Assert.ThrowsAsync<GestaoException>(() => caixa.Receber(db.StoreId, 7, operador,
            new RecebimentoRequest("r1", hoje, 24, FormaCaixa.Dinheiro, 50m), default))).Codigo);
        var dia = await caixa.Receber(db.StoreId, 7, operador, new RecebimentoRequest("r1", hoje, 24, FormaCaixa.Dinheiro, 100m), default);
        Assert.Contains(dia.Fita, l => l.Tipo == "venda" && l.Valor == 64m && l.Forma == FormaCaixa.Dinheiro);
        Assert.Equal(164m, Gaveta(await livro.Contas(db.StoreId, default))); // troco não entra no livro
        await caixa.Receber(db.StoreId, 7, operador, new RecebimentoRequest("r1", hoje, 24, FormaCaixa.Dinheiro, 100m), default); // reenvio
        Assert.Equal(164m, Gaveta(await livro.Contas(db.StoreId, default)));
        Assert.Equal("SEM_A_RECEBER", (await Assert.ThrowsAsync<GestaoException>(() => caixa.Receber(db.StoreId, 7, operador,
            new RecebimentoRequest("r2", hoje, 24, FormaCaixa.Pix, 64m), default))).Codigo);
        Assert.Empty(await caixa.AReceberNoCaixa(db.StoreId, default));
    }
}
