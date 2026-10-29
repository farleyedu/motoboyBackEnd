using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Vinculos;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>Curadoria Zippy: critérios, estado pela última decisão, motivo, lote, reabertura e casos gerados dos sinais.</summary>
public class GestaoCuradoriaDecisoesRegrasTests
{
    private static readonly DateTimeOffset Hoje = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Muss = Guid.NewGuid(), Catu = Guid.NewGuid(), Req = Guid.NewGuid();

    private static CasoCuradoria Caso(string tipo = TipoCaso.DePara, int lojas = 14, int confirmacoes = 31, int divergencias = 1) =>
        new("texto:QJO MUSS", tipo, "QJO MUSS → Muçarela", "", lojas, confirmacoes, divergencias, 3, Hoje, "QJO MUSS",
            new[] { new OpcaoDoCaso("Muçarela", null, lojas, confirmacoes, Muss) }, Array.Empty<string>(), null, Array.Empty<EventoDoCaso>());

    private static RegistroDecisao Registro(string decisao, int minuto, Guid? corrige = null) =>
        new(Guid.NewGuid(), "texto:QJO MUSS", "x", TipoCaso.DePara, decisao, "Marina", "m", Hoje.AddMinutes(minuto), "", "", null, corrige,
            null, null, null);

    private static SinalDeVinculo Sinal(string desc, Guid global, string nomeGlobal, int dia, int conf = 1, string status = "validado") =>
        new("QJO MUSS", desc, Guid.NewGuid(), "Loja " + dia, "Cidade " + (dia % 3), global, nomeGlobal, status, Hoje.AddDays(-dia), conf);

    [Fact]
    public void Criterios_pedem_5_lojas_10_confirmacoes_e_ate_10_por_cento_de_divergencia()
    {
        Assert.True(CuradoriaDecisoesRegras.AtingeCriterios(Caso()));
        Assert.False(CuradoriaDecisoesRegras.AtingeCriterios(Caso(lojas: 4)));
        Assert.False(CuradoriaDecisoesRegras.AtingeCriterios(Caso(confirmacoes: 9, divergencias: 0)));
        Assert.False(CuradoriaDecisoesRegras.AtingeCriterios(Caso(confirmacoes: 19, divergencias: 5)));
    }

    [Fact]
    public void Estado_sai_da_ultima_decisao_e_reabrir_volta_a_aberto()
    {
        Assert.Equal("aberto", CuradoriaDecisoesRegras.EstadoDoCaso("texto:QJO MUSS", Array.Empty<RegistroDecisao>()));
        var aprovado = Registro(DecisaoCuradoria.Aprovar, 1);
        Assert.Equal("aprovado", CuradoriaDecisoesRegras.EstadoDoCaso("texto:QJO MUSS", new[] { aprovado }));
        var reaberto = Registro(DecisaoCuradoria.Reabrir, 2, aprovado.Id);
        Assert.Equal("aberto", CuradoriaDecisoesRegras.EstadoDoCaso("texto:QJO MUSS", new[] { reaberto, aprovado }));
    }

    [Fact]
    public void Aprovar_dentro_dos_criterios_usa_motivo_padrao_e_fora_deles_exige_motivo_e_marca_excecao()
    {
        var ok = CuradoriaDecisoesRegras.Montar(Caso(), "aberto", DecisaoCuradoria.Aprovar, null, null);
        Assert.Equal((CuradoriaDecisoesRegras.MotivoPadraoAprovacao, false), (ok.Motivo, ok.Excecao));
        Assert.Equal(("Vale só nas lojas", "Validado para todas as lojas"), (ok.Anterior, ok.Novo));

        var poucas = Caso(lojas: 2, confirmacoes: 3, divergencias: 0);
        Assert.Equal("MOTIVO_OBRIGATORIO", Assert.Throws<GestaoException>(() =>
            CuradoriaDecisoesRegras.Montar(poucas, "aberto", DecisaoCuradoria.Aprovar, " ", null)).Codigo);
        Assert.True(CuradoriaDecisoesRegras.Montar(poucas, "aberto", DecisaoCuradoria.Aprovar, "Caso claro", null).Excecao);
    }

    [Fact]
    public void Corrigir_exige_produto_observar_guarda_confirmacoes_e_caso_decidido_nao_aceita_outra_decisao()
    {
        Assert.Equal("ALVO_OBRIGATORIO", Assert.Throws<GestaoException>(() =>
            CuradoriaDecisoesRegras.Montar(Caso(), "aberto", DecisaoCuradoria.Corrigir, "Marca errada", null)).Codigo);
        var corrigido = CuradoriaDecisoesRegras.Montar(Caso(), "aberto", DecisaoCuradoria.Corrigir, "Marca errada", "Requeijão cremoso");
        Assert.Equal(("Muçarela", "Requeijão cremoso"), (corrigido.Anterior, corrigido.Novo));
        Assert.Equal(31, CuradoriaDecisoesRegras.Montar(Caso(), "aberto", DecisaoCuradoria.Observar, "Poucos sinais", null).ConfirmacoesNoRegistro);
        Assert.NotNull(CuradoriaDecisoesRegras.Montar(Caso(), "observacao", DecisaoCuradoria.Rejeitar, "Divergência", null));
        Assert.Equal("CASO_JA_DECIDIDO", Assert.Throws<GestaoException>(() =>
            CuradoriaDecisoesRegras.Montar(Caso(), "aprovado", DecisaoCuradoria.Rejeitar, "x", null)).Codigo);
        Assert.Equal("CAMPO_INVALIDO", Assert.Throws<GestaoException>(() =>
            CuradoriaDecisoesRegras.Montar(Caso(), "aberto", DecisaoCuradoria.Reabrir, "x", null)).Codigo);
    }

    [Fact]
    public void Reabrir_so_a_ultima_decisao_e_nunca_uma_reabertura()
    {
        var a = Registro(DecisaoCuradoria.Observar, 1);
        var b = Registro(DecisaoCuradoria.Rejeitar, 2);
        CuradoriaDecisoesRegras.ValidarReabertura(b, new[] { a, b }, "Marca errada");
        Assert.Equal("REGISTRO_JA_REABERTO", Assert.Throws<GestaoException>(() =>
            CuradoriaDecisoesRegras.ValidarReabertura(a, new[] { a, b }, "x")).Codigo);
        var r = Registro(DecisaoCuradoria.Reabrir, 3, b.Id);
        Assert.Equal("REGISTRO_JA_REABERTO", Assert.Throws<GestaoException>(() =>
            CuradoriaDecisoesRegras.ValidarReabertura(r, new[] { a, b, r }, "x")).Codigo);
        Assert.Equal("MOTIVO_OBRIGATORIO", Assert.Throws<GestaoException>(() =>
            CuradoriaDecisoesRegras.ValidarReabertura(b, new[] { a, b }, " ")).Codigo);
    }

    [Fact]
    public void Lote_so_aprova_o_que_cumpre_criterios_e_nao_pede_escolha()
    {
        Assert.Null(CuradoriaDecisoesRegras.MotivoForaDoLote(Caso(), DecisaoCuradoria.Aprovar, "aberto"));
        Assert.NotNull(CuradoriaDecisoesRegras.MotivoForaDoLote(Caso(TipoCaso.Contra), DecisaoCuradoria.Aprovar, "aberto"));
        Assert.NotNull(CuradoriaDecisoesRegras.MotivoForaDoLote(Caso(lojas: 2), DecisaoCuradoria.Aprovar, "aberto"));
        Assert.Null(CuradoriaDecisoesRegras.MotivoForaDoLote(Caso(TipoCaso.Contra), DecisaoCuradoria.Observar, "aberto"));
        Assert.Equal("Já foi decidido.", CuradoriaDecisoesRegras.MotivoForaDoLote(Caso(), DecisaoCuradoria.Observar, "rejeitado"));
    }

    [Fact]
    public void Mesmo_texto_em_itens_globais_diferentes_vira_lojas_divergem()
    {
        var sinais = new[]
        {
            Sinal("REQ CREMOSO 1,5KG", Catu, "Catupiry original", 30, 3), Sinal("REQ CREMOSO 1,5KG", Catu, "Catupiry original", 20, 2),
            Sinal("REQUEIJAO CREM 1.5KG", Req, "Requeijão cremoso", 10, 2, "candidato"),
        };
        var caso = CuradoriaDecisoesRegras.CasoDeTexto("REQ CREMOSO 1 5KG", sinais, desfeitos: 1)!;
        Assert.Equal((TipoCaso.Contra, "texto:REQ CREMOSO 1 5KG", "REQ CREMOSO 1,5KG"), (caso.Tipo, caso.Id, caso.Nome));
        Assert.Equal((3, 7, 3), (caso.Lojas, caso.Confirmacoes, caso.Divergencias)); // 1 desfeito + 2 do lado menos aceito
        Assert.Equal(new[] { Catu, Req }, caso.Opcoes.Select(o => o.GlobalId!.Value));
        Assert.Contains("Lojas divergem", caso.Historico.Select(h => h.Titulo));
        Assert.Equal(2, caso.Variacoes.Count);
    }

    [Fact]
    public void Texto_com_um_item_so_e_de_para_e_uma_loja_so_nao_vira_caso()
    {
        var depara = CuradoriaDecisoesRegras.CasoDeTexto("QJO MUSS", new[] { Sinal("QJO MUSS", Muss, "Muçarela", 3), Sinal("QJO MUSS", Muss, "Muçarela", 1) }, 0)!;
        Assert.Equal((TipoCaso.DePara, "QJO MUSS → Muçarela", 0), (depara.Tipo, depara.Nome, depara.Divergencias));
        var umaLoja = Sinal("QJO MUSS", Muss, "Muçarela", 3);
        Assert.Null(CuradoriaDecisoesRegras.CasoDeTexto("QJO MUSS", new[] { umaLoja, umaLoja with { ConfirmadoEm = Hoje } }, 0));
    }

    [Fact]
    public void Duplicidade_junta_validados_parecidos_da_mesma_unidade_e_o_mais_antigo_fica()
    {
        var antigo = new ItemGlobal(Guid.NewGuid(), "Óleo de soja 900ml", "ml", "Mercearia", "validado", Hoje.AddYears(-1), 312, 400, 6);
        var novo = new ItemGlobal(Guid.NewGuid(), "Óleo de soja Liza 900ml", "ml", "Mercearia", "validado", Hoje.AddMonths(-5), 76, 90, 3);
        var outraUnidade = novo with { Id = Guid.NewGuid(), Unidade = "un" };
        var outraMedida = novo with { Id = Guid.NewGuid(), Nome = "Óleo de soja 500ml" };
        var caso = Assert.Single(CuradoriaDecisoesRegras.CasosDeDuplicidade(new[] { novo, antigo, outraUnidade, outraMedida }));
        Assert.Equal(($"dup:{antigo.Id}:{novo.Id}", 388), (caso.Id, caso.Lojas));
        Assert.Equal(antigo.Id, caso.Opcoes[0].GlobalId);
    }

    [Fact]
    public void Produto_candidato_aponta_parecido_validado_e_categoria_local_so_com_tres_lojas_fora_do_global()
    {
        var item = new ItemGlobal(Guid.NewGuid(), "Caixa hambúrguer kraft M", "un", null, "candidato", Hoje, 2, 3, 1);
        var parecido = new ItemGlobal(Guid.NewGuid(), "Caixa kraft hambúrguer", "un", "Embalagens", "validado", Hoje, 9, 20, 4);
        var caso = CuradoriaDecisoesRegras.CasoDeProduto(item, new[] { "CX HAMB KRAFT M" }, new[] { parecido }, "Burger X");
        Assert.Equal(("prod:" + item.Id, TipoCaso.Produto), (caso.Id, caso.Tipo));
        Assert.Contains("Caixa kraft hambúrguer", caso.Sinal);

        var (l1, l2, l3) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var usos = new[] { ("Descartáveis", l1, Hoje), ("DESCARTAVEIS", l2, Hoje), ("descartáveis ", l3, Hoje), ("Embalagens", l1, Hoje),
            ("Embalagens", l2, Hoje), ("Embalagens", l3, Hoje), ("Molhos", l1, Hoje), ("Molhos", l2, Hoje) };
        var cat = Assert.Single(CuradoriaDecisoesRegras.CasosDeCategoria(usos, new[] { "embalagens", null }));
        Assert.Equal(("cat:DESCARTAVEIS", 3), (cat.Id, cat.Lojas));
    }
}
