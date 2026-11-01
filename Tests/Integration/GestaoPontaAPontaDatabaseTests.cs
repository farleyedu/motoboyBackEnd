using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Caixa;
using APIBack.GestaoLogica.Estoque;
using APIBack.Repository;
using APIBack.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// 6C4 contra PostgreSQL de verdade, com a conclusão real do pedido: pedido → venda (dinheiro + Pix) → fita e gaveta → baixa pela
/// ficha → fechamento, mais concluir de novo e cancelar depois de concluído. A cada passo, o aviso de tempo real chega uma vez, só
/// depois do commit, para a loja e as áreas certas.
/// </summary>
public partial class DeliverySyncDatabaseTests
{
    private sealed class PainelFalso : IGestaoTempoRealPublisher
    {
        private readonly List<(Guid Loja, string[] Recursos, Guid Id)> _avisos = new();
        public Task Invalidar(Guid loja, IReadOnlyList<string> recursos, Guid id, long versao, CancellationToken ct)
        {
            lock (_avisos) _avisos.Add((loja, recursos.ToArray(), id));
            return Task.CompletedTask;
        }
        public (Guid Loja, string[] Recursos, Guid Id)[] Todos { get { lock (_avisos) return _avisos.ToArray(); } }
    }

    private sealed class FichaFixa(Guid produto, FichaTecnica ficha) : IFichaTecnicaLeitor
    {
        public Task<IReadOnlyDictionary<Guid, FichaTecnica>> Ler(Guid loja, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, FichaTecnica>>(ids.Contains(produto) ? new Dictionary<Guid, FichaTecnica> { [produto] = ficha } : new());
    }

    /// <summary>O canal vale para o banco todo: testes em paralelo (outras lojas) também avisam. Conta só os desta loja.</summary>
    private static async Task<(Guid Loja, string[] Recursos, Guid Id)[]> AvisosAte(PainelFalso painel, Guid loja, int quantos)
    {
        (Guid Loja, string[] Recursos, Guid Id)[] Da() => painel.Todos.Where(a => a.Loja == loja).ToArray();
        for (var i = 0; i < 60 && Da().Length < quantos; i++) await Task.Delay(100);
        await Task.Delay(400); // nada a mais (duplicado) chega depois
        return Da();
    }

    [DeliveryDatabaseFact]
    public async Task Gestao6C4_pedido_venda_fita_baixa_e_fechamento_avisam_uma_vez_cada_depois_do_commit()
    {
        await using var db = await VendaDatabase();
        var burger = Guid.NewGuid(); var pao = Guid.NewGuid();
        await db.Execute(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Inteligencia", "20261019_01_ingredientes.sql")));
        await db.Execute(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261108_01_gestao_estoque.sql")));
        await db.Execute(@"CREATE TABLE pedido_item(id bigserial PRIMARY KEY, pedido_id integer, produto_id uuid, nome text, quantidade integer, ordem integer DEFAULT 0);
INSERT INTO pedido_item (pedido_id, produto_id, nome, quantidade) VALUES (23, @burger, 'X-Burger', 2);
INSERT INTO insumo (id, estabelecimento_id, nome, unidade, categoria, criado_por) VALUES (@pao, @s, 'Pão brioche', 'un', 'Ingrediente', 7);",
            new { burger, pao, s = db.StoreId });

        var painel = new PainelFalso();
        var services = new ServiceCollection();
        services.AddSingleton(db.Source);
        services.AddSingleton<IGestaoTempoRealPublisher>(painel);
        services.AddSingleton<IAcontecimentoAssinante, GestaoTempoRealAssinante>();
        services.AddLogging();
        services.AddScoped<IAcontecimentosService, AcontecimentosService>();
        await using var sp = services.BuildServiceProvider();
        // Mesmos assinantes do Program.cs: transação própria avisa em processo; a dos módulos, pelo canal.
        var livro = new AcontecimentosService(db.Source, sp.GetServices<IAcontecimentoAssinante>(), NullLogger<AcontecimentosService>.Instance);
        var ouvinte = new GestaoTempoRealOuvinte(db.Source, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<GestaoTempoRealOuvinte>.Instance);
        await ouvinte.StartAsync(default);
        await Task.Delay(300);
        try
        {
            var gerente = GestaoPapeis.Resolver((_, _) => true, false);
            var caixa = new CaixaService(db.Source, livro);
            var hoje = await caixa.Hoje(db.StoreId, default);

            // 1) Conclusão do app (dinheiro 66,90 + Pix 20) e o replay do app: uma venda, um aviso.
            var repo = new PedidoQueueRepository(db.Source);
            var conclusao = CompletionRequest();
            await repo.CompleteDeliveryAsync(db.StoreId, 1, 7, db.SessionId, 4, conclusao, default);
            await repo.CompleteDeliveryAsync(db.StoreId, 1, 7, db.SessionId, 4, conclusao, default); // replay do app
            var (venda23, _, _) = await Venda(db, 23);
            var a1 = Assert.Single(await AvisosAte(painel, db.StoreId, 1));
            Assert.Equal((db.StoreId, venda23), (a1.Loja, a1.Id));
            Assert.Contains(GestaoTempoReal.Caixa, a1.Recursos);
            Assert.Contains(GestaoTempoReal.Financeiro, a1.Recursos);

            // 2) Fita e gaveta (relidas pela tela depois do aviso): o Pix está na maquininha; o dinheiro está com o motoboy (6C5), não na gaveta.
            var dia = (await caixa.CarregarDia(db.StoreId, hoje, default))!;
            Assert.Contains(dia.Fita, l => l.Tipo == "venda");
            Assert.Equal(100m, (await livro.Contas(db.StoreId, default)).Single(k => k.Nome == "Gaveta do caixa").Saldo);

            // 3) Baixa pela ficha (2 lanches → 2 pães), com confirmação: aviso só do estoque, sem mexer em dinheiro.
            var estoque = new EstoqueService(db.Source, livro,
                new FichaFixa(burger, new FichaTecnica(burger, new[] { new ComponenteFicha(pao, "Pão brioche", 1, "un") })), new ItensDoPedidoFake(db.Source));
            var previa = await estoque.PreviaBaixa(db.StoreId, new[] { 23 }, false, default);
            var baixa = await estoque.RegistrarBaixa(db.StoreId, 7, gerente, new BaixaVendasRequest(new[] { 23 }, true, previa.Assinatura), default);
            Assert.Equal(-2m, (await estoque.Saldos(db.StoreId, default)).Single(s => s.InsumoId == pao).Saldo);
            var a2 = (await AvisosAte(painel, db.StoreId, 2)).Single(a => a.Id == baixa.Acontecimento.Id);
            Assert.Equal(new[] { GestaoTempoReal.Estoque }, a2.Recursos);

            // 3b) Acerto do motoboy (6C5): o lote liquida os 66,90 para a gaveta; um aviso (caixa e financeiro).
            var lote = await caixa.Acertar(db.StoreId, 7, gerente, new AcertoRequest("lote-1", hoje, 1, new[] { new ItemAcertoRequest(23) }, 66.90m, null), default);
            Assert.Equal(("L-0001", 0m), (lote.Numero, lote.Diferenca));
            Assert.Equal(166.90m, (await livro.Contas(db.StoreId, default)).Single(k => k.Nome == "Gaveta do caixa").Saldo);
            await AvisosAte(painel, db.StoreId, 3);

            // 4) Concluir o pedido 24 (pago, sem forma: a receber) e cancelar depois de concluído: estorno avisa uma vez.
            await repo.CompleteCurrentAsync(db.StoreId, 7, 1);
            var (venda24, _, _) = await Venda(db, 24);
            Assert.Equal("cancelado", await repo.CancelarConcluidoAsync(db.StoreId, 7, 24, "Cliente devolveu"));
            Assert.Equal("cancelado", await repo.CancelarConcluidoAsync(db.StoreId, 7, 24, "Cliente devolveu"));
            await AvisosAte(painel, db.StoreId, 5);

            // 5) Contagem às cegas e fechamento (bateu: 166,90; fica 100, 66,90 ao cofre).
            var contagem = new ContagemCaixa(new Dictionary<int, int> { [100] = 1, [50] = 1, [10] = 1, [5] = 1 }, 1.90m);
            Assert.Equal(0m, (await caixa.ConferirContagem(db.StoreId, 7, hoje, contagem, default)).Diferenca);
            var fechado = await caixa.Fechar(db.StoreId, 7, gerente,
                new FecharCaixaRequest(hoje, contagem, new Dictionary<string, decimal> { ["pix"] = 20m }, null, 100m), default);
            Assert.False(fechado.Aberto);
            Assert.Equal(66.90m, fechado.Fechamento!.Cofre);
            var todos = await AvisosAte(painel, db.StoreId, 7);

            // Um aviso por acontecimento, nenhum repetido, todos desta loja, nenhum com valor (só loja, áreas e id).
            Assert.Equal(todos.Length, todos.Select(a => a.Id).Distinct().Count());
            Assert.Contains(todos, a => a.Id == venda24);
            Assert.Equal(7, todos.Length); // venda 23, baixa, acerto (liquidação), venda 24, estorno 24, fechamento, recolhimento
            Assert.All(todos.Where(a => a.Id != baixa.Acontecimento.Id), a => Assert.Contains(GestaoTempoReal.Caixa, a.Recursos));
        }
        finally
        {
            await ouvinte.StopAsync(default);
        }
    }
}
