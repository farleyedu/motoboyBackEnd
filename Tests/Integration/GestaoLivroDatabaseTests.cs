using APIBack.Acontecimentos;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>Livro de acontecimentos contra PostgreSQL de verdade: idempotencia, duplicidade, imutabilidade e mes fechado.</summary>
public class GestaoLivroDatabaseTests
{
    private sealed class Relogio(DateTimeOffset agora) : TimeProvider
    {
        public DateTimeOffset Agora { get; set; } = agora;
        public override DateTimeOffset GetUtcNow() => Agora.ToUniversalTime();
    }

    private sealed class Livro : IAsyncDisposable
    {
        public NpgsqlDataSource Source { get; private set; } = null!;
        public Guid Loja { get; } = Guid.NewGuid();
        public Relogio Relogio { get; } = new(new DateTimeOffset(2026, 10, 27, 15, 0, 0, TimeSpan.FromHours(-3)));
        public List<AcontecimentoRegistrado> Eventos { get; } = new();
        public AcontecimentosService Service { get; private set; } = null!;
        private readonly string _schema = "gestao_livro_" + Guid.NewGuid().ToString("N");
        private string _base = "";

        private sealed class Escuta(List<AcontecimentoRegistrado> eventos) : IAcontecimentoAssinante
        {
            public Task AoRegistrar(AcontecimentoRegistrado evento, CancellationToken ct) { lock (eventos) eventos.Add(evento); return Task.CompletedTask; }
        }

        public static async Task<Livro> Criar()
        {
            APIBack.Infrastructure.DapperConfiguration.Configure();
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
            if (options.Host != "127.0.0.1" && options.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
            options.PersistSecurityInfo = true;
            var livro = new Livro { _base = options.ConnectionString };
            await using (var admin = new NpgsqlConnection(livro._base)) { await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE SCHEMA {livro._schema}"); }
            options.SearchPath = livro._schema; options.MaxPoolSize = 10;
            livro.Source = NpgsqlDataSource.Create(options.ConnectionString);
            await livro.Execute(@"
CREATE TABLE estabelecimentos(id uuid PRIMARY KEY, timezone_iana text);
CREATE TABLE usuario(id integer PRIMARY KEY, nome text);
INSERT INTO estabelecimentos VALUES (@Loja, 'America/Sao_Paulo');
INSERT INTO usuario VALUES (7, 'Farley'), (8, 'Ana do caixa');", new { livro.Loja });
            var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261105_01_gestao_fundacao.sql"));
            await livro.Execute(migration);
            await livro.Execute(migration); // idempotente
            livro.Service = new AcontecimentosService(livro.Source, new[] { new Escuta(livro.Eventos) },
                NullLogger<AcontecimentosService>.Instance, livro.Relogio);
            return livro;
        }

        public async Task Execute(string sql, object? p = null) { await using var c = await Source.OpenConnectionAsync(); await c.ExecuteAsync(sql, p); }
        public async Task<T> Scalar<T>(string sql, object? p = null) { await using var c = await Source.OpenConnectionAsync(); return (await c.ExecuteScalarAsync<T>(sql, p))!; }

        public async Task<Guid> Conta(string tipo, string nome, string? subtipo = null, int? fecha = null, int? vence = null) =>
            (await Service.CriarConta(Loja, 7, new ContaRequest { ChaveIdempotencia = "conta-" + nome, Tipo = tipo, Nome = nome,
                Subtipo = subtipo, DiaFechamento = fecha, DiaVencimento = vence }, default)).Id;

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            await using var admin = new NpgsqlConnection(_base); await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {_schema} CASCADE");
        }
    }

    private static PublicarAcontecimentoRequest Compra(string chave, decimal valor, Guid? conta = null, string? origem = null,
        string? documento = null, int parcelas = 1, DateOnly? pago = null, DateTimeOffset? fato = null) => new()
    {
        ChaveIdempotencia = chave, Tipo = "compra", Modulo = ModuloGestao.Compras, DataFato = fato ?? new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(-3)),
        Descricao = "Compra no atacado", Origem = origem == null ? null : new OrigemRef { Tipo = "compra", Id = origem }, DocumentoChave = documento,
        Lancamentos = new() { new LancamentoRequest { Natureza = Natureza.Despesa, Valor = valor, ContaId = conta, Parcelas = parcelas,
            DataPagamento = pago, Categoria = "insumos", Finalidade = "producao", Descricao = "Atacadão" } },
    };

    private static async Task<GestaoException> Falha(Func<Task> acao) => await Assert.ThrowsAsync<GestaoException>(acao);

    [DeliveryDatabaseFact]
    public async Task MesmaChaveNaoGravaDuasVezesEOutroConteudoEConflito()
    {
        await using var db = await Livro.Criar();
        var primeiro = await db.Service.Publicar(db.Loja, 7, Compra("compra-1", 230m, origem: "c1"), default);
        var replay = await db.Service.Publicar(db.Loja, 7, Compra("compra-1", 230m, origem: "c1"), default);
        Assert.False(primeiro.Repetido);
        Assert.True(replay.Repetido);
        Assert.Equal(primeiro.Id, replay.Id);
        Assert.Equal(1, await db.Scalar<int>("SELECT count(*)::int FROM gestao_acontecimentos"));
        Assert.Equal(1, await db.Scalar<int>("SELECT count(*)::int FROM gestao_lancamentos"));
        Assert.Single(db.Eventos); // replay nao dispara evento
        Assert.Equal("IDEMPOTENCIA_CONFLITO", (await Falha(() => db.Service.Publicar(db.Loja, 7, Compra("compra-1", 231m, origem: "c1"), default))).Codigo);
    }

    [DeliveryDatabaseFact]
    public async Task ChamadasSimultaneasComAMesmaChaveGravamUmaVez()
    {
        await using var db = await Livro.Criar();
        var resultados = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => db.Service.Publicar(db.Loja, 7, Compra("paralela", 99m), default)));
        Assert.Single(resultados.Select(r => r.Id).Distinct());
        Assert.Equal(1, await db.Scalar<int>("SELECT count(*)::int FROM gestao_acontecimentos"));
        Assert.Equal(5, resultados.Count(r => r.Repetido));
    }

    [DeliveryDatabaseFact]
    public async Task MesmaOrigemOuMesmoDocumentoNaoContaDuasVezes()
    {
        await using var db = await Livro.Criar();
        await db.Service.Publicar(db.Loja, 7, Compra("a", 10m, origem: "c1", documento: "3526 1012-3456"), default);
        Assert.Equal("DUPLICADO", (await Falha(() => db.Service.Publicar(db.Loja, 7, Compra("b", 10m, origem: "c1"), default))).Codigo);
        Assert.Equal("DUPLICADO", (await Falha(() => db.Service.Publicar(db.Loja, 7, Compra("c", 10m, origem: "c2", documento: "35261012.3456"), default))).Codigo);
        Assert.Equal(1, await db.Scalar<int>("SELECT count(*)::int FROM gestao_acontecimentos"));
    }

    [DeliveryDatabaseFact]
    public async Task NadaSeApagaNemSeReescreve()
    {
        await using var db = await Livro.Criar();
        await db.Service.Publicar(db.Loja, 7, Compra("x", 10m), default);
        foreach (var sql in new[] { "UPDATE gestao_lancamentos SET valor = 1", "DELETE FROM gestao_acontecimentos",
                     "UPDATE gestao_acontecimentos SET descricao = 'outra'", "TRUNCATE gestao_lancamentos CASCADE" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Execute(sql));
            Assert.Equal("P0001", ex.SqlState);
        }
        Assert.Equal(10m, await db.Scalar<decimal>("SELECT valor FROM gestao_lancamentos"));
    }

    [DeliveryDatabaseFact]
    public async Task AjusteEEstornoSaoNovosAcontecimentosLigadosAoOriginal()
    {
        await using var db = await Livro.Criar();
        var compra = await db.Service.Publicar(db.Loja, 7, Compra("compra", 100m), default);
        var linha = compra.Acontecimento.Lancamentos.Single();
        var ajuste = await db.Service.Ajustar(db.Loja, 7, new AjusteRequest { ChaveIdempotencia = "aj", LancamentoId = linha.Id, ValorNovo = 92.40m,
            Motivo = "Desconto no cupom" }, default);
        Assert.Equal(compra.Id, ajuste.Acontecimento.CorrigeId);
        Assert.Equal(100m, ajuste.Acontecimento.ValorAnterior);
        Assert.Equal(92.40m, ajuste.Acontecimento.ValorNovo);
        Assert.Equal("Farley", ajuste.Acontecimento.ResponsavelNome);
        Assert.True((await db.Service.Ajustar(db.Loja, 7, new AjusteRequest { ChaveIdempotencia = "aj", LancamentoId = linha.Id, ValorNovo = 92.40m,
            Motivo = "Desconto no cupom" }, default)).Repetido);

        var estorno = await db.Service.Estornar(db.Loja, 7, compra.Id, new EstornoRequest { ChaveIdempotencia = "est", Motivo = "Devolvida" }, default);
        Assert.Equal(-92.40m, estorno.Acontecimento.Lancamentos.Single().Valor);
        Assert.Equal(0m, await db.Scalar<decimal>("SELECT sum(valor) FROM gestao_lancamentos"));
        Assert.Equal(3, await db.Scalar<int>("SELECT count(*)::int FROM gestao_lancamentos"));
        var original = await db.Service.Obter(db.Loja, compra.Id, default);
        Assert.Equal(2, original!.CorrigidoPor.Count);
        Assert.Equal("JA_ESTORNADO", (await Falha(() => db.Service.Estornar(db.Loja, 7, compra.Id,
            new EstornoRequest { ChaveIdempotencia = "est2", Motivo = "De novo" }, default))).Codigo);
        Assert.True((await db.Service.Estornar(db.Loja, 7, compra.Id, new EstornoRequest { ChaveIdempotencia = "est", Motivo = "Devolvida" }, default)).Repetido);
    }

    [DeliveryDatabaseFact]
    public async Task MesFechadoSoAceitaAjuste()
    {
        await using var db = await Livro.Criar();
        var setembro = new DateTimeOffset(2026, 9, 15, 10, 0, 0, TimeSpan.FromHours(-3));
        var compra = await db.Service.Publicar(db.Loja, 7, Compra("set", 50m, fato: setembro), default);
        await db.Service.FecharMes(db.Loja, 7, new DateOnly(2026, 9, 1), new FecharMesRequest { ChaveIdempotencia = "fecha-set" }, default);
        Assert.Equal(new[] { new DateOnly(2026, 9, 1) }, await db.Service.MesesFechados(db.Loja, default));

        Assert.Equal("MES_FECHADO", (await Falha(() => db.Service.Publicar(db.Loja, 7, Compra("set-2", 10m, fato: setembro), default))).Codigo);
        var outubroComReferenciaSetembro = Compra("out-ref-set", 10m);
        outubroComReferenciaSetembro.Lancamentos![0].MesReferencia = new DateOnly(2026, 9, 1);
        Assert.Equal("MES_FECHADO", (await Falha(() => db.Service.Publicar(db.Loja, 7, outubroComReferenciaSetembro, default))).Codigo);

        var ajuste = await db.Service.Ajustar(db.Loja, 7, new AjusteRequest { ChaveIdempotencia = "aj-set",
            LancamentoId = compra.Acontecimento.Lancamentos[0].Id, ValorNovo = 55m, Motivo = "Frete esquecido" }, default);
        Assert.Equal(new DateOnly(2026, 9, 1), ajuste.Acontecimento.Lancamentos[0].MesReferencia);
        // O original fica como estava; o ajuste é outra linha que aponta para ele, com valor anterior, novo e motivo (Integração 6A).
        var deSetembro = await db.Service.Lancamentos(db.Loja, new PeriodoFiltro { Mes = new DateOnly(2026, 9, 1), Limite = 100 }, default);
        var original = deSetembro.Single(l => l.Id == compra.Acontecimento.Lancamentos[0].Id);
        Assert.Equal(50m, original.Valor);
        Assert.Equal(5m, deSetembro.Single(l => l.CorrigeLancamentoId == original.Id).Valor);
        Assert.Equal((50m, 55m, "Frete esquecido", compra.Id), (ajuste.Acontecimento.ValorAnterior, ajuste.Acontecimento.ValorNovo,
            ajuste.Acontecimento.Motivo, ajuste.Acontecimento.CorrigeId));
        Assert.Equal("DUPLICADO", (await Falha(() => db.Service.FecharMes(db.Loja, 7, new DateOnly(2026, 9, 1),
            new FecharMesRequest { ChaveIdempotencia = "fecha-set-2" }, default))).Codigo);

        // O banco tambem recusa, mesmo sem passar pelo servico.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Execute(@"
INSERT INTO gestao_lancamentos (id, estabelecimento_id, acontecimento_id, linha, natureza, sentido, valor, status, mes_referencia)
VALUES (gen_random_uuid(), @Loja, @Id, 9, 'despesa', 'saida', 1, 'a_pagar', '2026-09-01')", new { db.Loja, compra.Id }));
        Assert.Equal("P0002", ex.SqlState);
    }

    [DeliveryDatabaseFact]
    public async Task PagamentoQuitaUmaVezEEstornoDoPagamentoVoltaParaAPagar()
    {
        await using var db = await Livro.Criar();
        var banco = await db.Conta(TipoConta.Banco, "Banco do Brasil");
        var caixa = await db.Conta(TipoConta.Caixa, "Caixa da loja");
        var conta = await db.Service.Publicar(db.Loja, 7, Compra("luz", 412.50m), default);
        var linha = conta.Acontecimento.Lancamentos.Single();
        Assert.Equal(StatusLancamento.APagar, linha.Status);

        var pagamento = await db.Service.Liquidar(db.Loja, 7, new LiquidarRequest { ChaveIdempotencia = "pg-luz", LancamentoId = linha.Id,
            ContaId = banco, DataPagamento = new DateOnly(2026, 10, 27) }, default);
        Assert.Equal(TipoAcontecimento.Pagamento, pagamento.Acontecimento.Tipo);
        var situacao = (await db.Service.Lancamentos(db.Loja, new PeriodoFiltro { Mes = new DateOnly(2026, 10, 1) }, default)).Single();
        Assert.Equal(StatusLancamento.Pago, situacao.Status);
        Assert.Equal(StatusLancamento.APagar, situacao.StatusRegistro);
        Assert.Equal(new DateOnly(2026, 10, 27), situacao.DataPagamento);
        Assert.Equal(-412.50m, (await db.Service.Contas(db.Loja, default)).Single(k => k.Id == banco).Saldo);
        Assert.Equal("DUPLICADO", (await Falha(() => db.Service.Liquidar(db.Loja, 7, new LiquidarRequest { ChaveIdempotencia = "pg-luz-2",
            LancamentoId = linha.Id, ContaId = caixa }, default))).Codigo);

        await db.Service.Estornar(db.Loja, 7, pagamento.Id, new EstornoRequest { ChaveIdempotencia = "est-pg", Motivo = "Pix voltou" }, default);
        Assert.Equal(StatusLancamento.APagar, (await db.Service.Lancamentos(db.Loja, new PeriodoFiltro { Mes = new DateOnly(2026, 10, 1) }, default)).Single().Status);
        Assert.Equal(0m, (await db.Service.Contas(db.Loja, default)).Single(k => k.Id == banco).Saldo);
        await db.Service.Liquidar(db.Loja, 7, new LiquidarRequest { ChaveIdempotencia = "pg-luz-3", LancamentoId = linha.Id, ContaId = caixa }, default);
        Assert.Equal(-412.50m, (await db.Service.Contas(db.Loja, default)).Single(k => k.Id == caixa).Saldo);
    }

    [DeliveryDatabaseFact]
    public async Task CompraNoCartaoSoSaiDoBancoQuandoAFaturaEPaga()
    {
        await using var db = await Livro.Criar();
        var banco = await db.Conta(TipoConta.Banco, "Inter");
        var cartao = await db.Conta(TipoConta.CartaoEmpresa, "Cartão PJ", fecha: 25, vence: 5);
        await db.Service.Publicar(db.Loja, 7, Compra("forno", 300m, conta: cartao, parcelas: 3), default);
        var contas = await db.Service.Contas(db.Loja, default);
        Assert.Equal(0m, contas.Single(k => k.Id == banco).Saldo);
        Assert.Equal(-300m, contas.Single(k => k.Id == cartao).Saldo);

        db.Relogio.Agora = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.FromHours(-3));
        var fatura = await db.Service.PagarFatura(db.Loja, 7, cartao, new DateOnly(2026, 11, 1),
            new LiquidarRequest { ChaveIdempotencia = "fatura-nov", ContaId = banco }, default);
        Assert.Equal(100m, fatura.Acontecimento.ValorNovo);
        contas = await db.Service.Contas(db.Loja, default);
        Assert.Equal(-100m, contas.Single(k => k.Id == banco).Saldo);
        Assert.Equal(-200m, contas.Single(k => k.Id == cartao).Saldo);
        Assert.Equal("CAMPO_INVALIDO", (await Falha(() => db.Service.PagarFatura(db.Loja, 7, cartao, new DateOnly(2026, 11, 1),
            new LiquidarRequest { ChaveIdempotencia = "fatura-nov-2", ContaId = banco }, default))).Codigo);
    }

    [DeliveryDatabaseFact]
    public async Task EstoqueSoMudaComConfirmacaoEEstornoDevolveAQuantidade()
    {
        await using var db = await Livro.Criar();
        var item = Guid.NewGuid();
        var conferencia = new PublicarAcontecimentoRequest
        {
            ChaveIdempotencia = "conf-1", Tipo = "conferencia_item", Modulo = ModuloGestao.Compras, DataFato = db.Relogio.Agora,
            Descricao = "Mussarela conferida", Origem = new OrigemRef { Tipo = "compra_item", Id = "c1:3" },
            Movimentos = new() { new MovimentoRequest { ItemId = item, ItemNome = "Mussarela", Quantidade = 5000, Unidade = "g", Tipo = TipoMovimento.Entrada } },
        };
        Assert.Equal("CONFIRMACAO_HUMANA", (await Falha(() => db.Service.Publicar(db.Loja, 7, conferencia, default))).Codigo);
        conferencia.ConfirmacaoHumana = true;
        Assert.Equal("CONFIRMACAO_HUMANA", (await Falha(() => db.Service.Publicar(db.Loja, null, conferencia, default))).Codigo);
        var ok = await db.Service.Publicar(db.Loja, 8, conferencia, default);
        Assert.Equal(8, ok.Acontecimento.Movimentos.Single().ConfirmadoPor);
        conferencia.ChaveIdempotencia = "conf-2";
        Assert.Equal("DUPLICADO", (await Falha(() => db.Service.Publicar(db.Loja, 8, conferencia, default))).Codigo);

        Assert.Equal("CONFIRMACAO_HUMANA", (await Falha(() => db.Service.Estornar(db.Loja, 7, ok.Id,
            new EstornoRequest { ChaveIdempotencia = "est-conf", Motivo = "Item errado" }, default))).Codigo);
        await db.Service.Estornar(db.Loja, 7, ok.Id, new EstornoRequest { ChaveIdempotencia = "est-conf", Motivo = "Item errado", ConfirmacaoHumana = true }, default);
        Assert.Equal(0m, await db.Scalar<decimal>("SELECT sum(quantidade) FROM gestao_movimentos_estoque WHERE item_id = @item", new { item }));
        var movimentos = await db.Service.Movimentos(db.Loja, new PeriodoFiltro { Mes = new DateOnly(2026, 10, 1), ItemId = item }, default);
        Assert.Equal(2, movimentos.Count);
    }

    [DeliveryDatabaseFact]
    public async Task LinhaDoTempoFiltraPorMesEDia()
    {
        await using var db = await Livro.Criar();
        await db.Service.Publicar(db.Loja, 7, Compra("d20", 10m, fato: new DateTimeOffset(2026, 10, 20, 23, 30, 0, TimeSpan.FromHours(-3))), default);
        await db.Service.Publicar(db.Loja, 7, Compra("d21", 10m, fato: new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.FromHours(-3))), default);
        await db.Service.Publicar(db.Loja, 7, Compra("set", 10m, fato: new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.FromHours(-3))), default);
        Assert.Equal(2, (await db.Service.LinhaDoTempo(db.Loja, new PeriodoFiltro { Mes = new DateOnly(2026, 10, 1) }, default)).Count);
        var dia20 = await db.Service.LinhaDoTempo(db.Loja, new PeriodoFiltro { Mes = new DateOnly(2026, 10, 1), Dia = new DateOnly(2026, 10, 20) }, default);
        Assert.Equal("d20", (await db.Scalar<string>("SELECT chave_idempotencia FROM gestao_acontecimentos WHERE id = @id", new { id = Assert.Single(dia20).Id })));
        Assert.Single(await db.Service.LinhaDoTempo(db.Loja, new PeriodoFiltro { Mes = new DateOnly(2026, 9, 1) }, default));
        Assert.Equal("CAMPO_INVALIDO", (await Falha(() => db.Service.LinhaDoTempo(db.Loja, new PeriodoFiltro(), default))).Codigo);
    }

    [DeliveryDatabaseFact]
    public async Task IntervaloDeDiasPegaQuinzenaOuSemanaDentroDoMes()
    {
        await using var db = await Livro.Criar();
        var outubro = new DateOnly(2026, 10, 1);
        DateTimeOffset Dia(int d) => new(2026, 10, d, 12, 0, 0, TimeSpan.FromHours(-3));
        await db.Service.Publicar(db.Loja, 7, Compra("d05", 10m, fato: Dia(5)), default);
        await db.Service.Publicar(db.Loja, 7, Compra("d15", 10m, fato: Dia(15)), default);
        await db.Service.Publicar(db.Loja, 7, Compra("d16", 10m, fato: Dia(16)), default);
        await db.Service.Publicar(db.Loja, 7, Compra("d27", 10m, fato: Dia(27)), default);

        async Task<int> Quantos(int de, int ate) => (await db.Service.LinhaDoTempo(db.Loja,
            new PeriodoFiltro { Mes = outubro, Dia = new DateOnly(2026, 10, de), Ate = new DateOnly(2026, 10, ate) }, default)).Count;
        Assert.Equal(2, await Quantos(1, 15));   // 1ª quinzena
        Assert.Equal(2, await Quantos(16, 31));  // 2ª quinzena
        Assert.Equal(1, await Quantos(26, 31));  // semana de 26 a 31
        Assert.Equal(2, (await db.Service.Lancamentos(db.Loja, new PeriodoFiltro { Mes = outubro, Dia = new DateOnly(2026, 10, 1), Ate = new DateOnly(2026, 10, 15) }, default)).Count);
        Assert.Equal("CAMPO_INVALIDO", (await Falha(() => Quantos(20, 10))).Codigo);
        Assert.Equal("CAMPO_INVALIDO", (await Falha(() => db.Service.LinhaDoTempo(db.Loja,
            new PeriodoFiltro { Mes = outubro, Dia = new DateOnly(2026, 10, 20), Ate = new DateOnly(2026, 11, 2) }, default))).Codigo);
    }
}
