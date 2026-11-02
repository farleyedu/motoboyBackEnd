using System.Reflection;
using APIBack.Acompanhamento.Saude;
using APIBack.Service;
using Xunit;
using F = APIBack.Acompanhamento.Fases.FasesAcompanhamento;
using Fx = APIBack.Acompanhamento.Fases.FaixasAcompanhamento;

namespace APIBack.Tests.Unit;

/// <summary>
/// Saude (regras e fim do teste): o pior sinal decide a faixa, cada limite tem sua fronteira (dia de calendario em Sao Paulo),
/// pausa declarada elimina falso positivo, fim do teste nunca corta acesso e o escalonamento so existe depois que N e definido.
/// </summary>
public class AcompanhamentoSaudeTests
{
    private static readonly TimeSpan Brt = TimeSpan.FromHours(-3);
    // 02/11/2026 09:00 em Sao Paulo.
    private static readonly DateTimeOffset Agora = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly LimitesSaude Padrao = new();
    private static readonly DateOnly Hoje = new(2026, 11, 2);

    private static DateTimeOffset Dia(int deslocamento, int hora = 15) => new DateTimeOffset(2026, 11, 2, hora, 0, 0, Brt).AddDays(deslocamento);

    /// <summary>Loja operando e saudavel; cada teste muda so o que quer provar.</summary>
    private static SaudeEntrada Loja(Func<SaudeEntrada, SaudeEntrada>? ajuste = null)
    {
        var e = new SaudeEntrada
        {
            LojaId = Guid.NewGuid(), AcompanhamentoId = Guid.NewGuid(), Nome = "Loja", Fase = F.Operando, FaseDesde = Dia(-60), Situacao = SituacoesComerciais.Ativo,
            CriadaEm = Dia(-90), OperacaoIniciadaEm = Dia(-60), Pedidos7d = 20, Pedidos28d = 80, DiasComPedido28d = 20, UltimoPedidoEm = Dia(0, 8),
            PlanoContratado = "rota", PagamentoConfirmado = true, FaixaGravada = Fx.Bem,
        };
        return ajuste == null ? e : ajuste(e);
    }

    private static SaudeEntrada Teste(int faltam, bool pagou = false, Func<SaudeEntrada, SaudeEntrada>? ajuste = null) => Loja(e =>
    {
        e = e with
        {
            Fase = F.Teste, FaseDesde = Dia(-10), Situacao = SituacoesComerciais.Teste, OperacaoIniciadaEm = Dia(-10), UltimoPassoEm = Dia(-1),
            TesteTerminaEm = new DateTimeOffset(2026, 11, 2, 23, 59, 0, Brt).AddDays(faltam),
            PlanoContratado = pagou ? "rota" : null, PagamentoConfirmado = pagou, Pedidos7d = 20, Pedidos28d = 50,
        };
        return ajuste == null ? e : ajuste(e);
    });

    private static SaudeAvaliacao Avaliar(SaudeEntrada e, LimitesSaude? l = null, DateTimeOffset? agora = null) => SaudeCalculador.Avaliar(e, l ?? Padrao, agora ?? Agora);

    private static string Codigos(SaudeAvaliacao a) => string.Join(",", a.Motivos.Select(m => m.Codigo));

    [Fact]
    public void Loja_saudavel_fica_bem_sem_motivos()
    {
        var a = Avaliar(Loja());
        Assert.Equal(Fx.Bem, a.Faixa);
        Assert.Empty(a.Motivos);
        Assert.False(a.Neutra);
        Assert.Equal("tudo certo", a.Resumo);
        Assert.Equal(SaudeCalculador.Versao, a.Calculo);
    }

    // ---- fim do teste: 4/3 dias em Atencao, 2/1/0 em Precisa de ajuda, nunca suspende

    [Theory]
    [InlineData(5, Fx.Bem)]
    [InlineData(4, Fx.Atencao)]
    [InlineData(3, Fx.Atencao)]
    [InlineData(2, Fx.PrecisaDeAjuda)]
    [InlineData(1, Fx.PrecisaDeAjuda)]
    [InlineData(0, Fx.PrecisaDeAjuda)]
    public void Teste_sem_pagamento_escalona_pelos_dias_que_faltam(int faltam, string esperada)
    {
        var a = Avaliar(Teste(faltam));
        Assert.Equal(esperada, a.Faixa);
        if (esperada != Fx.Bem) Assert.Equal("teste_fim_proximo", a.Principal!.Codigo);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(2)]
    [InlineData(0)]
    public void Teste_com_plano_e_pagamento_nao_alerta_ate_o_fim(int faltam) =>
        Assert.Equal(Fx.Bem, Avaliar(Teste(faltam, pagou: true)).Faixa);

    [Fact]
    public void Plano_escolhido_sem_pagamento_nao_conta_como_pago() =>
        Assert.Equal(Fx.PrecisaDeAjuda, Avaliar(Teste(1, pagou: false, e => e with { PlanoContratado = "rota", PagamentoConfirmado = false })).Faixa);

    [Fact]
    public void Pagamento_sem_plano_nao_conta_como_pago() =>
        Assert.Equal(Fx.PrecisaDeAjuda, Avaliar(Teste(1, pagou: false, e => e with { PlanoContratado = null, PagamentoConfirmado = true })).Faixa);

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(40)]
    public void Teste_terminado_sem_pagamento_continua_operando_em_vermelho_com_contador(int dias)
    {
        var a = Avaliar(Teste(-dias));
        Assert.Equal(Fx.PrecisaDeAjuda, a.Faixa);
        Assert.Equal("teste_sem_pagamento", a.Principal!.Codigo);
        Assert.Equal(dias, a.Principal.Contador);
        Assert.Contains($"{dias} dia", a.Principal.Texto);
        // Nada de bloqueio por prazo: nenhum motivo manda cortar acesso.
        Assert.DoesNotContain(a.Motivos, m => m.Codigo.Contains("bloque", StringComparison.OrdinalIgnoreCase) || m.Codigo.Contains("suspens", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Teste_terminado_com_pagamento_mas_ainda_sem_ativar_fica_em_atencao()
    {
        var a = Avaliar(Teste(-2, pagou: true));
        Assert.Equal(Fx.Atencao, a.Faixa);
        Assert.Equal("ativacao_pendente", a.Principal!.Codigo);
    }

    [Fact]
    public void Fronteira_da_meia_noite_de_Sao_Paulo_vira_o_dia_do_teste()
    {
        var fim = new DateTimeOffset(2026, 11, 2, 23, 30, 0, Brt);
        var antes = new DateTimeOffset(2026, 11, 3, 2, 30, 0, TimeSpan.Zero); // 23:30 de 02/11 em SP
        var depois = new DateTimeOffset(2026, 11, 3, 3, 30, 0, TimeSpan.Zero); // 00:30 de 03/11 em SP

        var a = Avaliar(Teste(0, ajuste: e => e with { TesteTerminaEm = fim, UltimoPedidoEm = antes.AddHours(-1) }), agora: antes);
        Assert.Equal("teste_fim_proximo", a.Principal!.Codigo);
        Assert.Equal(0, a.Principal.Contador);
        Assert.Equal("teste acaba hoje", a.Principal.Texto);

        var d = Avaliar(Teste(0, ajuste: e => e with { TesteTerminaEm = fim, UltimoPedidoEm = depois.AddHours(-1) }), agora: depois);
        Assert.Equal("teste_sem_pagamento", d.Principal!.Codigo);
        Assert.Equal(1, d.Principal.Contador);
    }

    [Fact]
    public void Teste_sem_data_de_termino_vira_sinal_indisponivel_e_nao_alerta()
    {
        var a = Avaliar(Teste(0, ajuste: e => e with { TesteTerminaEm = null }));
        Assert.Equal(Fx.Bem, a.Faixa);
        Assert.Contains("teste_termina_em", a.Indisponiveis);
    }

    // ---- escalonamento N: desligado ate o proprietario definir

    [Fact]
    public void Escalonamento_nasce_desligado_e_nao_assume_valor()
    {
        Assert.Null(Padrao.EscalonarSemPagamentoDias);
        var a = Avaliar(Teste(-90));
        Assert.False(a.EscalarAoGestor);
        Assert.DoesNotContain(a.Motivos, m => m.Codigo == "escalonar_sem_pagamento");
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(11, true)]
    public void Escalonamento_ligado_dispara_no_dia_N(int dias, bool escala)
    {
        var a = Avaliar(Teste(-dias), Padrao with { EscalonarSemPagamentoDias = 10 });
        Assert.Equal(escala, a.EscalarAoGestor);
        Assert.Equal(escala, a.Motivos.Any(m => m.Codigo == "escalonar_sem_pagamento"));
    }

    // ---- uso: historico da propria loja

    [Theory]
    [InlineData(19, 109, Fx.Bem)]       // media 30/semana, caiu 36,7%
    [InlineData(18, 108, Fx.Atencao)]   // media 30/semana, caiu exatamente 40%
    [InlineData(12, 102, Fx.Atencao)]
    public void Queda_de_40_por_cento_da_propria_media_vira_atencao(int p7, int p28, string esperada)
    {
        var a = Avaliar(Loja(e => e with { Pedidos7d = p7, Pedidos28d = p28 }));
        Assert.Equal(esperada, a.Faixa);
        if (esperada == Fx.Atencao) Assert.Equal("queda_de_pedidos", a.Principal!.Codigo);
    }

    [Fact]
    public void Loja_pequena_abaixo_da_base_minima_nao_gera_queda()
    {
        // media 6/semana (< 10): 6 -> 1 nao e queda, e ruido.
        Assert.Equal(Fx.Bem, Avaliar(Loja(e => e with { Pedidos7d = 1, Pedidos28d = 19 })).Faixa);
    }

    [Theory]
    [InlineData(6, Fx.Bem)]
    [InlineData(7, Fx.PrecisaDeAjuda)]
    [InlineData(8, Fx.PrecisaDeAjuda)]
    public void Sete_dias_sem_pedido_vira_precisa_de_ajuda(int diasSemPedido, string esperada)
    {
        var a = Avaliar(Loja(e => e with { Pedidos7d = diasSemPedido >= 7 ? 0 : 20, Pedidos28d = diasSemPedido >= 7 ? 60 : 80, UltimoPedidoEm = Dia(-diasSemPedido, 20) }));
        Assert.Equal(esperada, a.Faixa);
        if (esperada == Fx.PrecisaDeAjuda) Assert.Equal("sem_pedidos", a.Principal!.Codigo);
    }

    [Fact]
    public void Loja_que_acabou_de_comecar_nao_e_cobrada_de_pedido()
    {
        var a = Avaliar(Teste(10, ajuste: e => e with { OperacaoIniciadaEm = Dia(-3), UltimoPedidoEm = null, Pedidos7d = 0, Pedidos28d = 0 }));
        Assert.DoesNotContain(a.Motivos, m => m.Codigo == "sem_pedidos");
    }

    [Fact]
    public void Pausa_declarada_elimina_o_falso_positivo_de_inatividade()
    {
        var parada = Loja(e => e with { Pedidos7d = 0, Pedidos28d = 0, UltimoPedidoEm = null });
        Assert.Equal(Fx.PrecisaDeAjuda, Avaliar(parada).Faixa);

        Assert.Equal(Fx.Bem, Avaliar(parada with { Fase = F.Pausa }).Faixa);
        Assert.Equal(Fx.Bem, Avaliar(parada with { PausaAte = Hoje }).Faixa);                // ultimo dia da pausa ainda vale
        Assert.Equal(Fx.PrecisaDeAjuda, Avaliar(parada with { PausaAte = Hoje.AddDays(-1) }).Faixa); // pausa acabou ontem
    }

    [Fact]
    public void Pausa_nao_esconde_cobranca_atrasada()
    {
        var a = Avaliar(Loja(e => e with { Fase = F.Pausa, ValorAtrasado = 149m, AtrasadoDesde = Hoje.AddDays(-3) }));
        Assert.Equal("pagamento_atrasado", a.Principal!.Codigo);
    }

    [Theory]
    [InlineData(6, 5, Fx.Bem)]       // ainda cedo
    [InlineData(7, 9, Fx.Atencao)]   // sete dias e abaixo de 10 pedidos
    [InlineData(7, 10, Fx.Bem)]
    public void Uso_insuficiente_no_teste_so_depois_de_sete_dias(int diasDeTeste, int pedidos, string esperada)
    {
        var a = Avaliar(Teste(8, ajuste: e => e with { OperacaoIniciadaEm = Dia(-diasDeTeste), Pedidos7d = pedidos, Pedidos28d = pedidos, UltimoPedidoEm = Dia(0, 8) }));
        Assert.Equal(esperada, a.Faixa);
        if (esperada == Fx.Atencao) Assert.Equal("teste_uso_insuficiente", a.Principal!.Codigo);
    }

    // ---- vencimentos

    [Theory]
    [InlineData(10, Fx.Atencao)]
    [InlineData(11, Fx.Bem)]
    [InlineData(0, Fx.Atencao)]
    public void Cobranca_que_vence_em_ate_dez_dias_vira_atencao(int dias, string esperada) =>
        Assert.Equal(esperada, Avaliar(Loja(e => e with { ProximoVencimento = Hoje.AddDays(dias) })).Faixa);

    [Fact]
    public void Cobranca_atrasada_vence_a_que_vai_vencer()
    {
        var a = Avaliar(Loja(e => e with { ValorAtrasado = 249m, AtrasadoDesde = Hoje.AddDays(-2), ProximoVencimento = Hoje.AddDays(3) }));
        Assert.Equal(Fx.PrecisaDeAjuda, a.Faixa);
        Assert.Equal("pagamento_atrasado", a.Principal!.Codigo);
        Assert.Equal(2, a.Principal.Contador);
    }

    // ---- pendencia, passos, implantacao, proposta

    [Theory]
    [InlineData(0, false, Fx.Bem)]
    [InlineData(1, false, Fx.Atencao)]
    [InlineData(3, true, Fx.PrecisaDeAjuda)]
    public void Chamado_sem_resposta_ha_um_dia_vira_atencao_e_fora_do_prazo_vira_ajuda(int dias, bool vencido, string esperada) =>
        Assert.Equal(esperada, Avaliar(Loja(e => e with { ChamadoPendenteDesde = Dia(-dias, 9), ChamadoPrazoVencido = vencido })).Faixa);

    [Theory]
    [InlineData(4, Fx.Bem)]
    [InlineData(5, Fx.Atencao)]
    public void Cinco_dias_sem_passo_vira_atencao_onde_a_equipe_deve_algo(int dias, string esperada) =>
        Assert.Equal(esperada, Avaliar(Teste(10, ajuste: e => e with { UltimoPassoEm = Dia(-dias, 9) })).Faixa);

    [Fact]
    public void Loja_operando_nao_cobra_passo_da_equipe() =>
        Assert.Equal(Fx.Bem, Avaliar(Loja(e => e with { UltimoPassoEm = Dia(-30) })).Faixa);

    [Fact]
    public void Sem_acompanhamento_gravado_o_passo_vira_sinal_indisponivel()
    {
        var a = Avaliar(Teste(10, ajuste: e => e with { AcompanhamentoId = null, Fase = null }));
        Assert.Equal(Fx.Bem, a.Faixa);
        Assert.Contains("acompanhamento", a.Indisponiveis);
    }

    [Theory]
    [InlineData(13, Fx.Bem)]
    [InlineData(14, Fx.Atencao)]
    public void Implantacao_travada_a_partir_de_catorze_dias(int dias, string esperada)
    {
        var e = Loja(x => x with { Fase = F.Implantacao, Situacao = SituacoesComerciais.Implantacao, FaseDesde = Dia(-dias, 9), UltimoPassoEm = Dia(0, 8), OperacaoIniciadaEm = null });
        var a = Avaliar(e);
        Assert.Equal(esperada, a.Faixa);
        Assert.Equal(esperada == Fx.Bem, a.Neutra);
    }

    [Theory]
    [InlineData(4, Fx.Bem)]
    [InlineData(3, Fx.Atencao)]
    [InlineData(0, Fx.Atencao)]
    [InlineData(-1, Fx.PrecisaDeAjuda)]
    public void Proposta_perto_do_vencimento(int faltam, string esperada)
    {
        var e = new SaudeEntrada
        {
            AcompanhamentoId = Guid.NewGuid(), OportunidadeId = Guid.NewGuid(), Nome = "Lead", Fase = F.Proposta, FaseDesde = Dia(-2), UltimoPassoEm = Dia(-1),
            PropostaValidadeAte = Hoje.AddDays(faltam),
        };
        Assert.Equal(esperada, Avaliar(e).Faixa);
    }

    [Fact]
    public void Proposta_sem_validade_registrada_e_sinal_indisponivel()
    {
        var a = Avaliar(new SaudeEntrada { AcompanhamentoId = Guid.NewGuid(), Nome = "Lead", Fase = F.Proposta, FaseDesde = Dia(-1), UltimoPassoEm = Dia(0, 8) });
        Assert.Contains("validade_da_proposta", a.Indisponiveis);
    }

    // ---- decisao

    [Fact]
    public void Pior_sinal_decide_a_faixa_e_o_primeiro_da_pior_faixa_e_o_principal()
    {
        var a = Avaliar(Loja(e => e with { ProximoVencimento = Hoje.AddDays(5), ChamadoPendenteDesde = Dia(-2), Pedidos7d = 0, Pedidos28d = 60, UltimoPedidoEm = Dia(-9) }));
        Assert.Equal(Fx.PrecisaDeAjuda, a.Faixa);
        Assert.Equal("sem_pedidos", a.Principal!.Codigo);
        Assert.Equal(Fx.PrecisaDeAjuda, a.Motivos[0].Faixa);
        Assert.Equal(3, a.Motivos.Count);
    }

    [Fact]
    public void Todo_motivo_diz_o_que_viu_de_onde_veio_e_como_sair()
    {
        var a = Avaliar(Teste(-3, ajuste: e => e with
        {
            ValorAtrasado = 100m, AtrasadoDesde = Hoje.AddDays(-1), ChamadoPendenteDesde = Dia(-2), ChamadoPrazoVencido = false,
            UltimoPassoEm = Dia(-8), Pedidos7d = 0, Pedidos28d = 4, UltimoPedidoEm = Dia(-8),
        }), Padrao with { EscalonarSemPagamentoDias = 2 });
        Assert.True(a.Motivos.Count >= 5);
        foreach (var m in a.Motivos)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Texto), m.Codigo);
            Assert.False(string.IsNullOrWhiteSpace(m.Origem), m.Codigo);
            Assert.False(string.IsNullOrWhiteSpace(m.Saida), m.Codigo);
            Assert.True(Fx.Existe(m.Faixa), m.Codigo);
        }
    }

    [Theory]
    [InlineData(SituacoesComerciais.Bloqueado)]
    public void Loja_bloqueada_fica_fora_da_avaliacao(string situacao)
    {
        var a = Avaliar(Loja(e => e with { Situacao = situacao, Pedidos7d = 0, Pedidos28d = 0, UltimoPedidoEm = null, ValorAtrasado = 500m, AtrasadoDesde = Hoje.AddDays(-30) }));
        Assert.True(a.Neutra);
        Assert.Empty(a.Motivos);
    }

    [Fact]
    public void Avaliacao_e_deterministica_para_o_mesmo_dado()
    {
        var e = Teste(2);
        Assert.Equal(Avaliar(e).Faixa, Avaliar(e).Faixa);
        Assert.Equal(Codigos(Avaliar(e)), Codigos(Avaliar(e)));
    }

    [Fact]
    public void Hoje_e_Quadro_leem_a_mesma_decisao_do_mesmo_calculador()
    {
        // A carteira (fila Hoje) carrega a saude do servidor no mesmo tipo que o Quadro devolve nos cartoes.
        var carteira = typeof(CarteiraLojaDto).GetProperty(nameof(CarteiraLojaDto.Saude))!;
        var cartao = typeof(CartaoDto).GetProperty(nameof(CartaoDto.Saude))!;
        Assert.Equal(typeof(SaudeAvaliacao), Nullable.GetUnderlyingType(carteira.PropertyType) ?? carteira.PropertyType);
        Assert.Equal(cartao.PropertyType, carteira.PropertyType);
    }

    // ---- limites e previa

    [Fact]
    public void Limites_validam_faixa_e_chaves()
    {
        var (_, erros) = Padrao.Aplicar(new Dictionary<string, int?>
        {
            [ChavesSaude.QuedaPercentual] = 0, [ChavesSaude.DiasSemPedido] = null, ["inexistente"] = 3,
        });
        Assert.Equal(3, erros.Count);
        Assert.Contains(erros, e => e.Contains("queda_percentual"));
        Assert.Contains(erros, e => e.Contains("dias_sem_pedido"));
        Assert.Contains(erros, e => e.Contains("inexistente"));
    }

    [Fact]
    public void Limite_de_ajuda_do_teste_precisa_ser_menor_que_o_de_atencao()
    {
        var (_, erros) = Padrao.Aplicar(new Dictionary<string, int?> { [ChavesSaude.TesteAjudaAteDias] = 4 });
        Assert.Single(erros);
    }

    [Fact]
    public void Escalonamento_aceita_nulo_e_numero()
    {
        var (ligado, e1) = Padrao.Aplicar(new Dictionary<string, int?> { [ChavesSaude.EscalonarSemPagamentoDias] = 15 });
        Assert.Empty(e1);
        Assert.Equal(15, ligado.EscalonarSemPagamentoDias);
        var (desligado, e2) = ligado.Aplicar(new Dictionary<string, int?> { [ChavesSaude.EscalonarSemPagamentoDias] = null });
        Assert.Empty(e2);
        Assert.Null(desligado.EscalonarSemPagamentoDias);
    }

    [Fact]
    public void Previa_mostra_quem_muda_de_faixa_ao_mudar_o_limite()
    {
        var lojas = new[]
        {
            Loja(e => e with { Nome = "A", Pedidos7d = 0, Pedidos28d = 60, UltimoPedidoEm = Dia(-8) }),   // 8 dias sem pedido
            Loja(e => e with { Nome = "B", Pedidos7d = 0, Pedidos28d = 60, UltimoPedidoEm = Dia(-12) }),  // 12 dias
            Loja(e => e with { Nome = "C" }),
        };
        var (novos, erros) = Padrao.Aplicar(new Dictionary<string, int?> { [ChavesSaude.DiasSemPedido] = 10 });
        Assert.Empty(erros);
        var mudam = lojas.Where(l => Avaliar(l).Faixa != Avaliar(l, novos).Faixa).Select(l => l.Nome).ToList();
        Assert.Equal(new[] { "A" }, mudam);
    }

    [Fact]
    public void LerMudancas_aceita_json_e_recusa_o_resto()
    {
        var m = SaudeService.LerMudancas("{\"dias_sem_pedido\":10,\"escalonar_sem_pagamento_dias\":null}");
        Assert.Equal(10, m["dias_sem_pedido"]);
        Assert.Null(m["escalonar_sem_pagamento_dias"]);
        var ex = Assert.Throws<SaudeException>(() => SaudeService.LerMudancas("nao e json"));
        Assert.Equal(422, ex.Status);
        Assert.Equal(422, Assert.Throws<SaudeException>(() => SaudeService.LerMudancas("{\"dias_sem_pedido\":\"dez\"}")).Status);
    }

    // ---- migracao

    [Fact]
    public void Migracao_semeia_todas_as_regras_e_deixa_o_escalonamento_desligado()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261127_02_acompanhamento_saude.sql");
        Assert.True(File.Exists(path), $"Migration ausente no output de teste: {path}");
        var sql = File.ReadAllText(path);
        foreach (var def in LimitesSaude.Definicoes)
            Assert.Contains($"'{def.Chave}'", sql, StringComparison.Ordinal);
        Assert.Contains("('escalonar_sem_pagamento_dias', NULL)", sql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (chave) DO NOTHING", sql, StringComparison.Ordinal);
        foreach (var destrutivo in new[] { "DROP TABLE", "DROP COLUMN", "TRUNCATE", "DELETE FROM" })
            Assert.DoesNotContain(destrutivo, sql, StringComparison.OrdinalIgnoreCase);
        // Pagamento fica de fora.
        Assert.DoesNotContain("comercial_cobranca", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("assinatura_ciclo", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Padroes_das_definicoes_batem_com_os_limites_iniciais()
    {
        var valores = Padrao.ComoDicionario();
        foreach (var def in LimitesSaude.Definicoes)
            Assert.Equal(def.Padrao, valores[def.Chave]);
        Assert.Equal(LimitesSaude.Definicoes.Count, valores.Count);
        Assert.NotNull(typeof(SaudeCalculador).GetMethod(nameof(SaudeCalculador.Avaliar), BindingFlags.Public | BindingFlags.Static));
    }
}
