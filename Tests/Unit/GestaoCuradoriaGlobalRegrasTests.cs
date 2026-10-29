using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Vinculos;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>L7: correção do catálogo global (antes/depois só do que mudou, validações) e critérios configuráveis.</summary>
public class GestaoCuradoriaGlobalRegrasTests
{
    private static CasoCuradoria Caso(int lojas, int confirmacoes, int divergencias) =>
        new("texto:X", TipoCaso.DePara, "X", "", lojas, confirmacoes, divergencias, 1, DateTimeOffset.UnixEpoch, "X",
            new[] { new OpcaoDoCaso("X", null, lojas, confirmacoes, Guid.NewGuid()) }, Array.Empty<string>(), null, Array.Empty<EventoDoCaso>());

    [Fact]
    public void Diferenca_mostra_so_o_que_mudou_e_recusa_correcao_vazia()
    {
        var (antes, depois) = CuradoriaGlobalRegras.Diferenca(new (string, string?, string?)[]
        {
            ("Nome", "Muçarela", "Muçarela fatiada"), ("Unidade", "g", "g"), ("Categoria", null, "Laticínios"),
        });
        Assert.Equal("Nome: Muçarela · Categoria: —", antes);
        Assert.Equal("Nome: Muçarela fatiada · Categoria: Laticínios", depois);
        Assert.Equal("SEM_MUDANCA", Assert.Throws<GestaoException>(() =>
            CuradoriaGlobalRegras.Diferenca(new (string, string?, string?)[] { ("Nome", "A", "A") })).Codigo);
    }

    [Fact]
    public void Campos_do_global_sao_validados()
    {
        Assert.Equal("ml", CuradoriaGlobalRegras.Unidade(" ML "));
        Assert.Throws<GestaoException>(() => CuradoriaGlobalRegras.Unidade("kg"));
        Assert.Equal("Óleo de soja", CuradoriaGlobalRegras.Nome("  Óleo   de soja ", 120, "do produto"));
        Assert.Throws<GestaoException>(() => CuradoriaGlobalRegras.Nome("   ", 120, "do produto"));
        Assert.Throws<GestaoException>(() => CuradoriaGlobalRegras.Nome(new string('a', 81), 80, "do kit"));
        Assert.Null(CuradoriaGlobalRegras.ArquivoDeIlustracao(" "));
        Assert.Equal("pao_topo", CuradoriaGlobalRegras.ArquivoDeIlustracao("pao_topo"));
        Assert.Throws<GestaoException>(() => CuradoriaGlobalRegras.ArquivoDeIlustracao("https://x.com/foto.jpg"));
        Assert.Equal("MOTIVO_OBRIGATORIO", Assert.Throws<GestaoException>(() => CuradoriaGlobalRegras.Motivo(" ")).Codigo);
    }

    [Fact]
    public void Criterios_tem_limites()
    {
        Assert.Equal(new CriteriosCuradoria(3, 6, 0.15m), CuradoriaGlobalRegras.Criterios(new SalvarCriteriosRequest(3, 6, 0.15m, "m")));
        Assert.Throws<GestaoException>(() => CuradoriaGlobalRegras.Criterios(new SalvarCriteriosRequest(0, 6, 0.1m, "m")));
        Assert.Throws<GestaoException>(() => CuradoriaGlobalRegras.Criterios(new SalvarCriteriosRequest(3, 1001, 0.1m, "m")));
        Assert.Throws<GestaoException>(() => CuradoriaGlobalRegras.Criterios(new SalvarCriteriosRequest(3, 6, 0.51m, "m")));
        Assert.Throws<GestaoException>(() => CuradoriaGlobalRegras.Criterios(new SalvarCriteriosRequest(3, 6, null, "m")));
    }

    [Fact]
    public void Criterios_configurados_mudam_promocao_motivo_e_lote()
    {
        var caso = Caso(lojas: 3, confirmacoes: 6, divergencias: 0);
        var frouxos = new CriteriosCuradoria(3, 6, 0.1m);
        Assert.False(CuradoriaDecisoesRegras.AtingeCriterios(caso));
        Assert.True(CuradoriaDecisoesRegras.AtingeCriterios(caso, frouxos));
        Assert.True(CuradoriaDecisoesRegras.MotivoObrigatorio(caso, DecisaoCuradoria.Aprovar));
        Assert.False(CuradoriaDecisoesRegras.MotivoObrigatorio(caso, DecisaoCuradoria.Aprovar, frouxos));
        var nova = CuradoriaDecisoesRegras.Montar(caso, "aberto", DecisaoCuradoria.Aprovar, null, null, frouxos);
        Assert.Equal(CuradoriaDecisoesRegras.MotivoPadraoAprovacao, nova.Motivo);
        Assert.False(nova.Excecao);
        Assert.Null(CuradoriaDecisoesRegras.MotivoForaDoLote(caso, DecisaoCuradoria.Aprovar, "aberto", frouxos));
        Assert.NotNull(CuradoriaDecisoesRegras.MotivoForaDoLote(caso, DecisaoCuradoria.Aprovar, "aberto", new CriteriosCuradoria(3, 6, 0m) with { Lojas = 4 }));
    }
}
