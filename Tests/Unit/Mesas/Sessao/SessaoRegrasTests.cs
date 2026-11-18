using APIBack.Mesas.Auditoria;
using APIBack.Mesas.Sessao;
using Xunit;
using R = APIBack.Mesas.Sessao.SessaoRegras;

namespace APIBack.Tests.Unit.Mesas.Sessao;

public sealed class SessaoRegrasTests
{
    private static readonly DateTime Agora = new(2026, 11, 17, 22, 0, 0, DateTimeKind.Utc);
    private static readonly R.LimitesAlerta Padrao = new(120, 25);

    [Theory]
    [InlineData("ocupada", true)]
    [InlineData("em_atendimento", true)]
    [InlineData("aguardando_pagamento", true)]
    [InlineData("livre", false)]
    [InlineData("aguardando_limpeza", false)]
    [InlineData("indisponivel", false)]
    public void Aberta_segue_o_contrato(string status, bool aberta) => Assert.Equal(aberta, R.Aberta(status));

    [Fact]
    public void Alertas_vem_do_relogio_do_servidor_e_do_mais_urgente_para_o_menos()
    {
        var m = new R.SituacaoMesa("ocupada", Agora.AddMinutes(-130), null, null, ChamadoAberto: true, AutorizacaoPendente: false);
        var a = R.Alertas(m, Agora, Padrao, new[] { "pedido_pronto" });
        Assert.Equal(new[] { "chamado", "pedido_pronto", "sem_pedido", "tempo_excedido" }, a);
        Assert.Equal("chamou", R.Icone(a));
        Assert.Equal(130, R.Minutos(m.AbertaEm, Agora));
    }

    [Fact]
    public void Sem_pedido_conta_do_ultimo_pedido_e_some_com_a_conta_pedida()
    {
        var recente = new R.SituacaoMesa("em_atendimento", Agora.AddMinutes(-60), null, Agora.AddMinutes(-10), false, false);
        Assert.DoesNotContain("sem_pedido", R.Alertas(recente, Agora, Padrao));

        var conta = new R.SituacaoMesa("aguardando_pagamento", Agora.AddMinutes(-60), Agora.AddMinutes(-2), null, false, false);
        var a = R.Alertas(conta, Agora, Padrao);
        Assert.Equal(new[] { "conta_pedida" }, a);
        Assert.Equal("conta", R.Icone(a));
    }

    [Fact]
    public void Limite_zero_desliga_e_mesa_livre_nao_tem_alerta_de_tempo()
    {
        var velha = new R.SituacaoMesa("ocupada", Agora.AddHours(-5), null, null, false, false);
        Assert.Empty(R.Alertas(velha, Agora, new R.LimitesAlerta(0, 0)));
        var livre = new R.SituacaoMesa("livre", null, null, null, false, false);
        Assert.Empty(R.Alertas(livre, Agora, Padrao));
        Assert.Equal(0, R.Minutos(null, Agora));
        Assert.Equal(new[] { "reserva_chegando" }, R.Alertas(livre with { Status = "reservada" }, Agora, Padrao));
    }

    [Theory]
    [InlineData("(11) 98888-7777", "+5511988887777")]
    [InlineData("1133334444", "+551133334444")]
    [InlineData("+55 11 98888-7777", "+5511988887777")]
    [InlineData("5511988887777", "+5511988887777")]
    [InlineData("123", null)]
    [InlineData("", null)]
    public void Celular_vira_e164(string entrada, string? esperado) => Assert.Equal(esperado, R.Celular(entrada));

    [Theory]
    [InlineData("m-4:c2", "m-9", 4, "m-9:c2")]
    [InlineData("m-4:c6", "m-9", 4, null)]
    [InlineData("qualquer", "m-9", 4, null)]
    [InlineData(null, "m-9", 4, null)]
    public void Cadeira_acompanha_a_transferencia(string? cadeira, string destino, int capacidade, string? esperado) =>
        Assert.Equal(esperado, R.CadeiraNaMesa(cadeira, destino, capacidade));

    [Theory]
    [InlineData("garcom", "sozinho", false, R.Autonomia.Pode)]
    [InlineData("garcom", "motivo", false, R.Autonomia.ExigeMotivo)]
    [InlineData("garcom", "motivo", true, R.Autonomia.Pode)]
    [InlineData("garcom", "gerente", true, R.Autonomia.PedeGerente)]
    [InlineData("gerente", "gerente", false, R.Autonomia.Pode)]
    [InlineData("dono", "motivo", false, R.Autonomia.Pode)]
    public void Autonomia_so_vale_para_o_garcom(string papel, string regra, bool motivo, R.Autonomia esperado) =>
        Assert.Equal(esperado, R.Decidir(papel, regra, motivo));

    [Fact]
    public void Nome_sem_acento_e_texto_da_auditoria()
    {
        Assert.Equal(R.NomeComparavel(" Júlia "), R.NomeComparavel("julia"));
        Assert.Equal("Ana", R.PrimeiroNome("Ana Souza"));
        Assert.Equal("Ana · Transferiu · Mesa 4 · Mesa 4 → Mesa 9", R.TextoEvento("Ana", "transferirMesa", "Mesa 4", "Mesa 4", "Mesa 9"));
        Assert.Equal("Ana · Abriu a mesa · Mesa 4 · 3 pessoas", R.TextoEvento("Ana", "abrirMesa", "Mesa 4", null, "3 pessoas"));
        Assert.Equal("acaoNova", R.RotuloAcao("acaoNova"));
    }

    [Fact]
    public void Cursor_da_auditoria_volta_igual()
    {
        var id = Guid.NewGuid();
        var quando = new DateTime(2026, 11, 17, 21, 3, 4, 567, DateTimeKind.Utc).AddTicks(8);
        Assert.True(AuditoriaService.LerCursor(AuditoriaService.Cursor(quando, id), out var q, out var i));
        Assert.Equal(quando, q);
        Assert.Equal(id, i);
        Assert.False(AuditoriaService.LerCursor("lixo!", out _, out _));
    }

    [Fact]
    public void Hash_do_comando_muda_com_o_corpo()
    {
        var chave = Guid.NewGuid();
        var a = MesasComandos.Hash("abrirMesa", new MesaRequest(chave, Guid.Empty, 1));
        Assert.Equal(a, MesasComandos.Hash("abrirMesa", new MesaRequest(chave, Guid.Empty, 1)));
        Assert.NotEqual(a, MesasComandos.Hash("abrirMesa", new MesaRequest(chave, Guid.Empty, 2)));
        Assert.NotEqual(a, MesasComandos.Hash("encerrar", new MesaRequest(chave, Guid.Empty, 1)));
    }
}
