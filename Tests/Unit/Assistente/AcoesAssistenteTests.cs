using System;
using System.Collections.Generic;
using APIBack.Assistente;
using APIBack.DTOs.Cardapio;
using APIBack.DTOs.Configuracoes;
using APIBack.DTOs.Delivery;
using Xunit;

namespace APIBack.Tests.Unit.Assistente;

public sealed class AcoesAssistenteTests
{
    private static CardapioProdutoContractDto P(string nome) => new() { Id = Guid.NewGuid(), Nome = nome };

    [Fact]
    public void NomeIgualSemAcentoGanha()
    {
        var lista = new List<CardapioProdutoContractDto> { P("X-Bacon Duplo"), P("X-Bacon"), P("Pão de Queijo") };
        Assert.Equal("X-Bacon", AcoesRegras.EscolherProduto("x-bacon", lista).Nome);
        Assert.Equal("Pão de Queijo", AcoesRegras.EscolherProduto("pao de  queijo", lista).Nome);
    }

    [Fact]
    public void UmSoQueContemEhEle()
    {
        var lista = new List<CardapioProdutoContractDto> { P("Coca-Cola 2L"), P("Guaraná") };
        Assert.Equal("Coca-Cola 2L", AcoesRegras.EscolherProduto("coca", lista).Nome);
    }

    [Fact]
    public void VariosParecidosPerguntaQual()
    {
        var lista = new List<CardapioProdutoContractDto> { P("Coca-Cola 2L"), P("Coca-Cola Lata") };
        var ex = Assert.Throws<AcaoAssistenteException>(() => AcoesRegras.EscolherProduto("coca", lista));
        Assert.Contains("Coca-Cola Lata", ex.Message);
        Assert.Contains("Pergunte qual", ex.Message);
    }

    [Fact]
    public void NenhumDaNaoEncontrado()
    {
        var ex = Assert.Throws<AcaoAssistenteException>(() => AcoesRegras.EscolherProduto("pizza", [P("Coca")]));
        Assert.Equal(404, ex.Status);
    }

    [Fact]
    public void HorarioEspecialFechadoPorPadrao()
    {
        var r = AcaoCriarHorarioEspecial.Normalizar(new ParametrosAcao(null, "2026-12-25", null, "10:00", "12:00", " Natal "));
        Assert.True(r.Fechado);
        Assert.Null(r.AbreAs);
        Assert.Equal("Natal", r.Motivo);
    }

    [Theory]
    [InlineData("25/12/2026", false, "18:00", "23:00", "Data inválida")]
    [InlineData("2026-12-25", false, "18h", "23:00", "abrir inválido")]
    [InlineData("2026-12-25", false, "18:00", "18:00", "diferentes")]
    public void HorarioEspecialInvalido(string data, bool fechado, string abre, string fecha, string motivo)
    {
        var ex = Assert.Throws<AcaoAssistenteException>(() => AcaoCriarHorarioEspecial.Normalizar(new ParametrosAcao(null, data, fechado, abre, fecha, null)));
        Assert.Contains(motivo, ex.Message);
    }

    [Fact]
    public void HorarioEspecialMostraAntesEDepois()
    {
        var novo = AcaoCriarHorarioEspecial.Normalizar(new ParametrosAcao(null, "2026-11-20", false, "8:00", "14:00", null));
        var semAnterior = AcaoCriarHorarioEspecial.Montar(novo, null);
        Assert.Equal("Horário normal da semana", semAnterior.Antes);
        Assert.Equal("Aberto das 08:00 às 14:00", semAnterior.Depois);
        Assert.Contains("20/11", semAnterior.Resumo);
        Assert.Null(semAnterior.Parametros["anterior"]);

        var comAnterior = AcaoCriarHorarioEspecial.Montar(novo, new HorarioEspecialDto { Id = 3, Data = "2026-11-20", Fechado = true });
        Assert.Equal("Fechado o dia todo", comAnterior.Antes);
        Assert.NotNull(comAnterior.Parametros["anterior"]);
    }

    [Fact]
    public void AbrirHojeRecusaLojaJaAberta()
    {
        var ex = Assert.Throws<AcaoAssistenteException>(() =>
            AcaoAbrirLojaHoje.Montar(new StoreOperationDto { AbertoAgora = true, FechaAs = "23:00" }, "23:00"));
        Assert.Equal("SEM_MUDANCA", ex.Codigo);
    }

    [Fact]
    public void AbrirHojeRecusaQuandoNaoPodeAbrir()
    {
        var ex = Assert.Throws<AcaoAssistenteException>(() =>
            AcaoAbrirLojaHoje.Montar(new StoreOperationDto { PodeAbrir = false, ComoResolver = "Cadastre o horário." }, "23:00"));
        Assert.Contains("Cadastre o horário.", ex.Message);
    }

    [Fact]
    public void AbrirHojeGuardaDataDaLoja()
    {
        var p = AcaoAbrirLojaHoje.Montar(new StoreOperationDto { PodeAbrir = true, DataLocal = "2026-11-17" }, "23:00");
        Assert.Equal("Fechada", p.Antes);
        Assert.Equal("Aberta hoje até 23:00", p.Depois);
        Assert.Equal("2026-11-17", AcoesRegras.Texto(p.Parametros, "dataLocal"));
    }
}
