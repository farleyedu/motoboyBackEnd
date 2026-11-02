using APIBack.Acompanhamento.Incidentes;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>
/// Incidentes: 3+ lojas com a mesma falha viram um agregado so; sinais sem telemetria ficam sem_monitoramento; validacoes (422) acontecem antes de tocar no banco;
/// migration aditiva que preserva a causa original.
/// </summary>
public class AcompanhamentoIncidentesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static FalhaAberta F(string assinatura, Guid? loja = null, int min = 0, string executor = "indeterminado", string categoria = "ifood") =>
        new(Guid.NewGuid(), categoria, assinatura, loja ?? Guid.NewGuid(), T0.AddMinutes(min), "causa", executor);

    private static IncidenteService ServicoSemBanco() => new(null!);

    // ---- agregacao

    [Fact]
    public void Duas_lojas_nao_formam_incidente()
    {
        Assert.Empty(IncidenteAgregador.Agrupar(new[] { F("ifood:x"), F("ifood:x") }));
    }

    [Fact]
    public void Tres_lojas_com_a_mesma_assinatura_viram_um_unico_agregado_com_as_lojas_vinculadas()
    {
        var a = F("ifood:x", min: 5); var b = F("ifood:x", min: 1); var c = F("ifood:x", min: 3);
        var grupos = IncidenteAgregador.Agrupar(new[] { a, b, c, F("ifood:outra") });
        var g = Assert.Single(grupos);
        Assert.Equal("ifood:x", g.Assinatura);
        Assert.Equal(3, g.LojaIds.Count);
        Assert.Equal(T0.AddMinutes(1), g.Inicio);
        Assert.Equal(3, g.FalhaIds.Count);
    }

    [Fact]
    public void Mesma_loja_repetida_conta_uma_vez()
    {
        var loja = Guid.NewGuid();
        Assert.Empty(IncidenteAgregador.Agrupar(new[] { F("a", loja), F("a", loja, 1), F("a", loja, 2), F("a") }));
    }

    [Fact]
    public void Agregacao_e_deterministica_independente_da_ordem()
    {
        var itens = Enumerable.Range(0, 4).Select(i => F("ifood:x", min: i)).ToList();
        itens.AddRange(Enumerable.Range(0, 3).Select(i => F("canal_meta:y", min: i, categoria: "canal_meta")));
        var a = IncidenteAgregador.Agrupar(itens);
        var b = IncidenteAgregador.Agrupar(Enumerable.Reverse(itens));
        Assert.Equal(a.Select(g => g.Assinatura), b.Select(g => g.Assinatura));
        Assert.Equal(a.Select(g => string.Join(",", g.FalhaIds)), b.Select(g => string.Join(",", g.FalhaIds)));
        Assert.Equal(2, a.Count);
    }

    [Fact]
    public void Executor_do_agregado_so_e_definido_com_consenso()
    {
        var iguais = IncidenteAgregador.Agrupar(Enumerable.Range(0, 3).Select(_ => F("a", executor: "zippy"))).Single();
        Assert.Equal("zippy", iguais.Executor);
        var mistos = IncidenteAgregador.Agrupar(new[] { F("a", executor: "zippy"), F("a", executor: "loja"), F("a", executor: "zippy") }).Single();
        Assert.Equal("indeterminado", mistos.Executor);
    }

    // ---- assinatura e executor

    [Fact]
    public void Assinatura_ignora_numeros_ids_e_caixa()
    {
        var a = IncidenteService.Assinatura("canal_meta", "Erro 131047 no numero 5511999990000 id 3f2b1c4e-1111-2222-3333-444455556666");
        var b = IncidenteService.Assinatura("canal_meta", "erro 131056 no numero 5521888880000 id aaaaaaaa-1111-2222-3333-444455556666");
        Assert.Equal(a, b);
        Assert.NotEqual(a, IncidenteService.Assinatura("ifood", "Erro 131047 no numero 5511999990000"));
    }

    [Theory]
    [InlineData("Token expirado", "loja")]
    [InlineData("invalid_grant", "loja")]
    [InlineData("HTTP 503 service_unavailable", "terceiro")]
    [InlineData("algo estranho", "indeterminado")]
    [InlineData(null, "indeterminado")]
    public void Executor_so_e_atribuido_com_pista_clara(string? erro, string esperado) =>
        Assert.Equal(esperado, IncidenteService.ClassificarExecutor(erro));

    // ---- cobertura: sem telemetria nao inventa

    [Fact]
    public void Categorias_sem_telemetria_declaram_a_fonte_que_falta()
    {
        Assert.Equal(11, CategoriasIncidente.FonteFaltante.Count);
        Assert.Null(CategoriasIncidente.FonteFaltante[CategoriasIncidente.Ifood]);
        Assert.Null(CategoriasIncidente.FonteFaltante[CategoriasIncidente.CanalMeta]);
        foreach (var kv in CategoriasIncidente.FonteFaltante.Where(k => k.Key is not ("ifood" or "canal_meta")))
            Assert.False(string.IsNullOrWhiteSpace(kv.Value), kv.Key);
    }

    // ---- validacoes (422/404 antes do banco)

    private static async Task<IncidenteException> Falha(Func<Task> acao) => await Assert.ThrowsAsync<IncidenteException>(acao);

    [Fact]
    public async Task Registro_sem_prova_ou_categoria_invalida_e_422()
    {
        var s = ServicoSemBanco();
        var ok = new RegistrarFalhaRequest { Categoria = "robo", Fonte = "relato", LojaId = Guid.NewGuid(), CausaProvavel = "robo parado", Prova = "print" };
        var semProva = await Falha(() => s.RegistrarAsync(new RegistrarFalhaRequest { Categoria = ok.Categoria, Fonte = ok.Fonte, LojaId = ok.LojaId, CausaProvavel = ok.CausaProvavel }, 1));
        Assert.Equal(422, semProva.Status);
        var cat = await Falha(() => s.RegistrarAsync(new RegistrarFalhaRequest { Categoria = "xyz", Fonte = "f", LojaId = Guid.NewGuid(), CausaProvavel = "c", Prova = "p" }, 1));
        Assert.Equal(422, cat.Status);
        var exec = await Falha(() => s.RegistrarAsync(new RegistrarFalhaRequest { Categoria = "robo", Fonte = "f", LojaId = Guid.NewGuid(), CausaProvavel = "c", Prova = "p", Executor = "ninguem" }, 1));
        Assert.Equal(422, exec.Status);
    }

    [Fact]
    public async Task Avisar_sem_destinatario_ou_sem_mensagem_validada_e_422_e_nao_prepara_nada()
    {
        var s = ServicoSemBanco();
        var id = Guid.NewGuid();
        var semDest = await Falha(() => s.AvisarAsync(id, new AvisarRequest { Mensagem = "oi", MensagemValidada = true }, 1));
        Assert.Equal("SEM_DESTINATARIO", semDest.Code);
        var semContato = await Falha(() => s.AvisarAsync(id, new AvisarRequest { Mensagem = "oi", MensagemValidada = true, Destinatarios = { new DestinatarioRequest { LojaId = Guid.NewGuid() } } }, 1));
        Assert.Equal(422, semContato.Status);
        var dest = new DestinatarioRequest { LojaId = Guid.NewGuid(), Contato = "+5511999990000" };
        var naoValidada = await Falha(() => s.AvisarAsync(id, new AvisarRequest { Mensagem = "oi", MensagemValidada = false, Destinatarios = { dest } }, 1));
        Assert.Equal("MENSAGEM_NAO_VALIDADA", naoValidada.Code);
        var vazia = await Falha(() => s.AvisarAsync(id, new AvisarRequest { Mensagem = "  ", MensagemValidada = true, Destinatarios = { dest } }, 1));
        Assert.Equal(422, vazia.Status);
    }

    [Fact]
    public async Task Normalizou_exige_confirmacao_e_lidos_exigem_ids()
    {
        var s = ServicoSemBanco();
        Assert.Equal(422, (await Falha(() => s.NormalizouAsync(Guid.NewGuid(), new NormalizouRequest { Confirmado = false }, 1))).Status);
        Assert.Equal(422, (await Falha(() => s.MarcarLidosAsync(Array.Empty<long>(), 1))).Status);
    }

    // ---- migracao

    [Fact]
    public void Migracao_e_aditiva_idempotente_e_garante_unicidade()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261127_03_acompanhamento_incidentes.sql");
        Assert.True(File.Exists(path), $"Migration ausente no output de teste: {path}");
        var sql = File.ReadAllText(path);
        foreach (var destrutivo in new[] { "DROP TABLE", "DROP COLUMN", "TRUNCATE", "DELETE FROM" })
            Assert.DoesNotContain(destrutivo, sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ux_acompanhamento_incidente_aberto", sql, StringComparison.Ordinal);
        Assert.Contains("ux_acompanhamento_incidente_envio UNIQUE (incidente_id, contato)", sql, StringComparison.Ordinal);
        Assert.Contains("ux_acompanhamento_falha_chave", sql, StringComparison.Ordinal);
        Assert.Contains("pendente_servico", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("comercial_cobranca", sql, StringComparison.Ordinal);
    }
}
