using System.Collections.Concurrent;
using APIBack.Acompanhamento.Fases;
using APIBack.DTOs.CRM;
using APIBack.Model.CRM;
using APIBack.Repository.Interface;
using APIBack.Service;
using APIBack.Service.Interface;
using APIBack.Tests.Integration;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;
using F = APIBack.Acompanhamento.Fases.FasesAcompanhamento;

namespace APIBack.Tests.Unit;

/// <summary>
/// Fases e passagens (docs/crm-inteligente/01-backend-base-crm.md, zippy-admin, itens 1 e 2): cada linha das duas tabelas,
/// salto proibido, loja criada em implantacao, fechamento direto da conversa, portao do teste (inclusive API legada),
/// repeticao pela chave de idempotencia e concorrencia.
/// </summary>
public class AcompanhamentoFasesTests
{
    private const string Cnpj = "11.222.333/0001-81";
    private const string Cpf = "529.982.247-25";
    private static readonly DateTimeOffset Agora = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    // ---- tabela 1: as passagens que existem

    public static IEnumerable<object[]> Passagens() =>
        PassagensCatalogo.Todas.Select(p => new object[] { p.De, p.Para });

    [Fact]
    public void Matriz_tem_todas_as_passagens_do_documento()
    {
        var pares = PassagensCatalogo.Todas.Select(p => $"{p.De}>{p.Para}").ToHashSet();
        var esperadas = new[]
        {
            "conversa>proposta", "proposta>conversa", "conversa>implantacao", "proposta>implantacao", "conversa>teste", "proposta>teste",
            "implantacao>teste", "teste>teste", "teste>operando", "operando>saindo", "saindo>operando", "saindo>encerrado",
            "operando>encerrado", "operando>pausa", "pausa>operando", "operando>implantacao", "implantacao>operando",
            "conversa>perdido", "proposta>perdido", "implantacao>perdido", "teste>perdido", "perdido>conversa", "encerrado>conversa"
        };
        Assert.Equal(esperadas.OrderBy(x => x), pares.OrderBy(x => x));
        Assert.Equal(pares.Count, PassagensCatalogo.Todas.Count); // sem duplicata
    }

    [Theory]
    [MemberData(nameof(Passagens))]
    public void Cada_passagem_executa_com_os_requisitos_prontos(string de, string para)
    {
        var loja = LojaPronta(de, para);
        var dados = DadosCompletos();
        // O motivo e escolha fechada em duas passagens; nas outras e texto livre.
        dados.Motivo = (de, para) == (F.Proposta, F.Conversa) ? "esfriou" : "recomeco";
        var avaliacao = PassagemMotor.Avaliar(Guid.NewGuid(), 1, loja, para, dados, Agora);

        Assert.True(avaliacao.Existe);
        Assert.True(avaliacao.PodeExecutar, string.Join("; ", avaliacao.Requisitos.Where(r => !r.Pronto).Select(r => r.Pendencia)));
        Assert.Null(PassagemMotor.Recusar(avaliacao));
        var plano = PassagemMotor.Planejar(loja, para, dados, "k", 7, AutoresPassagem.Usuario, Agora);
        Assert.Equal(de, plano.De);
        Assert.Equal(para == F.Teste && F.EmConquista(de) ? F.Implantacao : para, plano.FaseResultante);
    }

    [Theory]
    [MemberData(nameof(Passagens))]
    public void Cada_passagem_com_requisito_faltando_responde_422_com_os_pendentes(string de, string para)
    {
        var def = PassagensCatalogo.Obter(de, para)!;
        var loja = new LojaFatos { Fase = de, TemLoja = !F.EmConquista(de), TesteConcluido = de == F.Implantacao && para == F.Operando, CobrancaEmAberto = true };
        var avaliacao = PassagemMotor.Avaliar(Guid.NewGuid(), 1, loja, para, new DadosPassagem(), Agora);
        var recusa = PassagemMotor.Recusar(avaliacao);

        // Sem requisito (pausa -> operando, retomar, quer voltar) nao ha o que faltar.
        if (def.Requisitos.Count == 0)
        {
            Assert.Null(recusa);
            return;
        }

        Assert.NotNull(recusa);
        Assert.Equal(422, recusa!.Status);
        Assert.Equal(CodigosAcompanhamento.RequisitosPendentes, recusa.Code);
        var detalhes = Assert.IsType<RecusaDetalhesDto>(recusa.Details);
        Assert.NotEmpty(detalhes.RequisitosPendentes);
        Assert.All(detalhes.RequisitosPendentes, r => Assert.False(string.IsNullOrWhiteSpace(r.Pendencia)));
        Assert.Equal(new[] { de, para }, detalhes.Sequencia);
    }

    [Fact]
    public void Requisitos_de_fechar_na_conversa_sao_quem_decide_aceite_plano_e_documento()
    {
        var def = PassagensCatalogo.Obter(F.Conversa, F.Implantacao)!;
        Assert.Equal(new[] { "quemDecide", "aceite", "plano", "documento" }, def.Requisitos.Select(r => r.Chave));
        Assert.DoesNotContain(def.Requisitos, r => r.Chave == "proposta"); // fecha sem proposta formal

        var avaliacao = PassagemMotor.Avaliar(Guid.NewGuid(), 1, new LojaFatos { Fase = F.Conversa }, F.Implantacao,
            new DadosPassagem { QuemDecide = "Ana", Aceite = "por telefone", Plano = "rota", Documento = "123" }, Agora);
        Assert.Equal(new[] { "aceite", "documento" }, avaliacao.Requisitos.Where(r => !r.Pronto).Select(r => r.Chave));
    }

    // ---- tabela 2: as passagens que nao existem

    [Theory]
    [InlineData("implantacao", "operando", "Toda loja passa pelo teste antes de operar.", "implantacao,teste,operando")]
    [InlineData("conversa", "operando", "A loja precisa existir e passar pelo teste.", "conversa,implantacao,teste,operando")]
    [InlineData("proposta", "operando", "A loja precisa existir e passar pelo teste.", "proposta,implantacao,teste,operando")]
    [InlineData("teste", "implantacao", "A loja continua configurável no teste.", "teste,teste")]
    [InlineData("teste", "saindo", "No teste não há contrato para cancelar.", "teste,perdido")]
    [InlineData("conversa", "saindo", "Quem não é cliente não cancela.", "conversa,perdido")]
    [InlineData("proposta", "saindo", "Quem não é cliente não cancela.", "proposta,perdido")]
    [InlineData("implantacao", "saindo", "Quem não é cliente não cancela.", "implantacao,perdido")]
    [InlineData("operando", "teste", "O teste é só na entrada.", "")]
    [InlineData("operando", "conversa", "Cliente não volta para a conquista.", "")]
    [InlineData("operando", "proposta", "Cliente não volta para a conquista.", "")]
    [InlineData("implantacao", "conversa", "A loja já foi criada.", "implantacao,perdido,conversa")]
    [InlineData("implantacao", "proposta", "A loja já foi criada.", "implantacao,perdido,conversa")]
    [InlineData("teste", "conversa", "A loja já está testando.", "")]
    [InlineData("teste", "proposta", "A loja já está testando.", "")]
    [InlineData("saindo", "conversa", "Saindo só vai para Operando (ficou) ou Encerrado.", "")]
    [InlineData("saindo", "teste", "Saindo só vai para Operando (ficou) ou Encerrado.", "")]
    [InlineData("saindo", "pausa", "Saindo só vai para Operando (ficou) ou Encerrado.", "")]
    [InlineData("saindo", "perdido", "Saindo só vai para Operando (ficou) ou Encerrado.", "")]
    public void Passagem_que_nao_existe_responde_422_com_motivo_e_caminho(string de, string para, string motivo, string sequencia)
    {
        var avaliacao = PassagemMotor.Avaliar(Guid.NewGuid(), 1, LojaPronta(de) with { TesteConcluido = false }, para, DadosCompletos(), Agora);
        var recusa = PassagemMotor.Recusar(avaliacao);

        Assert.False(avaliacao.Existe);
        Assert.Equal(422, recusa!.Status);
        Assert.Equal(CodigosAcompanhamento.PassagemInexistente, recusa.Code);
        var detalhes = Assert.IsType<RecusaDetalhesDto>(recusa.Details);
        Assert.Equal(motivo, detalhes.Motivo);
        Assert.False(string.IsNullOrWhiteSpace(detalhes.Caminho));
        Assert.Equal(sequencia, string.Join(",", detalhes.Sequencia));
    }

    [Fact]
    public void Todo_par_de_fases_ou_existe_ou_tem_motivo_e_nenhum_chega_a_operando_sem_teste()
    {
        foreach (var de in F.Todas)
        foreach (var para in F.Todas)
        {
            var recusa = PassagensCatalogo.Recusa(de, para, new LojaFatos { Fase = de });
            Assert.True(recusa != null || PassagensCatalogo.Obter(de, para) != null, $"{de} -> {para}");
            if (recusa != null) Assert.False(string.IsNullOrWhiteSpace(recusa.Motivo));
            // O unico portao: quem nunca testou so chega a Operando vindo do Teste (ou da pausa/saida, que ja operavam).
            if (recusa == null && para == F.Operando) Assert.Contains(de, new[] { F.Teste, F.Pausa, F.Saindo });
        }
    }

    [Fact]
    public void Sem_tabela_propria_o_caminho_e_a_menor_sequencia_possivel()
    {
        var recusa = PassagensCatalogo.Recusa(F.Perdido, F.Teste, new LojaFatos { Fase = F.Perdido })!;
        Assert.Equal("Essa passagem não existe.", recusa.Motivo);
        Assert.Equal(new[] { F.Perdido, F.Conversa, F.Teste }, recusa.Sequencia);
        Assert.Equal("Perdido → Conversa → Teste", recusa.Caminho);
    }

    [Fact]
    public void Reimplantacao_volta_a_operar_sem_novo_teste_e_nao_reabre_o_teste()
    {
        var reimplantada = new LojaFatos { Fase = F.Implantacao, TemLoja = true, TesteConcluido = true, PendenciasBloqueantes = 0 };
        Assert.True(PassagemMotor.Avaliar(Guid.NewGuid(), 1, reimplantada, F.Operando, new DadosPassagem(), Agora).PodeExecutar);
        var teste = PassagemMotor.Recusar(PassagemMotor.Avaliar(Guid.NewGuid(), 1, reimplantada, F.Teste, new DadosPassagem(), Agora))!;
        Assert.Equal(CodigosAcompanhamento.PassagemInexistente, teste.Code);
        Assert.Equal(new[] { F.Implantacao, F.Operando }, ((RecusaDetalhesDto)teste.Details!).Sequencia);
    }

    [Fact]
    public void Catalogo_publica_as_duas_tabelas()
    {
        var catalogo = PassagemMotor.Catalogo();
        Assert.Equal(PassagensCatalogo.Todas.Count, catalogo.Passagens.Count);
        Assert.Equal(F.NoQuadro, catalogo.FasesNoQuadro);
        Assert.Contains(catalogo.Recusas, r => r.De == F.Implantacao && r.Para == F.Operando);
        Assert.Contains(catalogo.Recusas, r => r.De == F.Implantacao && r.Para == F.Teste); // reimplantacao
        Assert.Contains(catalogo.Recusas, r => r.De == F.Saindo && r.Para == F.Pausa);
        Assert.All(catalogo.Passagens, p => Assert.False(string.IsNullOrWhiteSpace(p.OQueAcontece)));
    }

    // ---- regras soltas

    [Theory]
    [InlineData("implantacao", false, false)]
    [InlineData("implantacao", true, true)] // volta da reimplantacao
    [InlineData("teste", false, true)]
    [InlineData("ativo", true, true)]
    [InlineData("bloqueado", true, false)]
    public void Ativar_so_depois_do_teste_inclusive_pela_api_legada(string situacao, bool operacaoIniciada, bool pode)
    {
        Assert.Equal(pode, SituacaoComercialRules.PodeAtivar(situacao, operacaoIniciada ? Agora : null));
    }

    [Theory]
    [InlineData("teste", "ativo", "operando")]        // pagamento ou API legada ativou
    [InlineData("implantacao", "teste", "teste")]     // a loja clicou "Comecar a operar"
    [InlineData("operando", "bloqueado", "encerrado")]
    [InlineData("saindo", "ativo", null)]
    [InlineData("pausa", "ativo", null)]
    [InlineData("perdido", "bloqueado", null)]
    [InlineData("conversa", "bloqueado", null)]       // encerrada que quer voltar
    public void Fase_acompanha_a_situacao_da_gestao(string fase, string situacao, string? esperada)
    {
        Assert.Equal(esperada, F.Reconciliar(fase, situacao));
    }

    [Theory]
    [InlineData(Cnpj, true)]
    [InlineData(Cpf, true)]
    [InlineData("11.222.333/0001-82", false)]
    [InlineData("111.111.111-11", false)]
    [InlineData("123", false)]
    [InlineData(null, false)]
    public void Documento_e_cpf_ou_cnpj_com_digito(string? documento, bool valido)
    {
        Assert.Equal(valido, DocumentoRegras.Valido(documento));
    }

    [Fact]
    public void Fim_do_teste_nao_bloqueia_sozinho_e_saida_tem_data()
    {
        Assert.Null(F.Reconciliar(F.Teste, SituacoesComerciais.Teste));
        Assert.Equal(new DateOnly(2026, 12, 2), PassagemMotor.SaidaEm("aviso_30_dias", new DateOnly(2026, 11, 2)));
        Assert.Null(PassagemMotor.SaidaEm("fim_do_ciclo", new DateOnly(2026, 11, 2)));
    }

    // ---- servico: ciclo completo, idempotencia e concorrencia

    [Fact]
    public async Task Fechamento_direto_da_conversa_cria_loja_em_implantacao_e_so_opera_depois_do_teste()
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();

        var fechou = await svc.ExecutarAsync(op, F.Implantacao, new DadosPassagem
        {
            ChaveIdempotencia = "f1", De = F.Conversa, QuemDecide = "Ana (dona)", Aceite = "whatsapp", Plano = "rota", Documento = Cnpj, ValorMensal = 249
        }, 7, AutoresPassagem.Usuario);

        Assert.Equal(F.Implantacao, fechou.FaseAtual);
        Assert.NotNull(fechou.EstabelecimentoId);
        Assert.Equal(SituacoesComerciais.Implantacao, store.Situacao(op)); // nunca nasce ativa
        Assert.Contains("loja_criada_implantacao", store.Efeitos);

        // Pular o teste e proibido, pelo acompanhamento e pela API legada.
        var pulo = await Assert.ThrowsAsync<AcompanhamentoException>(() =>
            svc.ExecutarAsync(op, F.Operando, new DadosPassagem { ChaveIdempotencia = "p1", Plano = "rota", FormaPagamento = "pix" }, 7, AutoresPassagem.Usuario));
        Assert.Equal(422, pulo.Status);
        Assert.Equal(new[] { F.Implantacao, F.Teste, F.Operando }, ((RecusaDetalhesDto)pulo.Details!).Sequencia);
        Assert.False(store.PodeAtivarPelaApiLegada(op));

        var teste = await svc.ExecutarAsync(op, F.Teste, new DadosPassagem { ChaveIdempotencia = "t1" }, 7, AutoresPassagem.Usuario);
        Assert.Equal(F.Teste, teste.FaseAtual);
        Assert.Equal(SituacoesComerciais.Teste, store.Situacao(op));

        var ativo = await svc.ExecutarAsync(op, F.Operando, new DadosPassagem { ChaveIdempotencia = "a1", Plano = "rota", FormaPagamento = "pix" },
            7, AutoresPassagem.Usuario);
        Assert.Equal(F.Operando, ativo.FaseAtual);
        Assert.Equal(SituacoesComerciais.Ativo, store.Situacao(op));

        // Historico consistente: cada mudanca com de, para, autor e a passagem que a causou.
        var fases = ativo.Historico.OrderBy(h => h.Id).Select(h => $"{h.De ?? "-"}>{h.Para}").ToList();
        Assert.Equal(new[] { "->conversa", "conversa>implantacao", "implantacao>teste", "teste>operando" }, fases);
        Assert.All(ativo.Historico.Where(h => h.De != null), h =>
        {
            Assert.Equal(7, h.AutorUsuarioId);
            Assert.NotNull(h.PassagemId);
        });
        Assert.Equal("aceite whatsapp", ativo.Historico.Single(h => h.Para == F.Implantacao).Motivo);
    }

    [Fact]
    public async Task Teste_sem_compromisso_cria_a_loja_em_implantacao_e_marca_o_teste_automatico()
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();

        var r = await svc.ExecutarAsync(op, F.Teste, new DadosPassagem { ChaveIdempotencia = "t", Documento = Cpf }, 7, AutoresPassagem.Usuario);

        Assert.Equal(F.Implantacao, r.FaseAtual);
        Assert.Equal(F.Teste, r.Passagem.Para);
        Assert.Equal(SituacoesComerciais.Implantacao, store.Situacao(op));
        Assert.True(store.Estado(op).TesteQuandoPronta);
    }

    [Fact]
    public async Task Implantacao_com_checklist_travado_nao_comeca_o_teste()
    {
        var (svc, store) = Servico(pendencias: 2);
        var loja = store.NovaLoja(SituacoesComerciais.Implantacao);

        var ex = await Assert.ThrowsAsync<AcompanhamentoException>(() =>
            svc.ExecutarAsync(loja, F.Teste, new DadosPassagem { ChaveIdempotencia = "t" }, 7, AutoresPassagem.Usuario));
        var pendente = Assert.Single(((RecusaDetalhesDto)ex.Details!).RequisitosPendentes);
        Assert.Equal("checklist", pendente.Chave);
        Assert.Equal(SituacoesComerciais.Implantacao, store.Situacao(loja));
    }

    [Fact]
    public async Task Repetir_a_mesma_chave_devolve_a_mesma_passagem_sem_fazer_de_novo()
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();
        var dados = new DadosPassagem { ChaveIdempotencia = "duplo-clique", De = F.Conversa, QuemDecide = "Ana", PedidosPorDia = 40, DorPrincipal = "motoboy" };

        var primeira = await svc.ExecutarAsync(op, F.Proposta, dados, 7, AutoresPassagem.Usuario);
        var segunda = await svc.ExecutarAsync(op, F.Proposta, dados, 7, AutoresPassagem.Usuario);

        Assert.False(primeira.Repetida);
        Assert.True(segunda.Repetida);
        Assert.Equal(primeira.Passagem.Id, segunda.Passagem.Id);
        Assert.Equal(primeira.Versao, segunda.Versao);
        Assert.Single(store.Passagens(op));
        Assert.Single(segunda.Historico, h => h.Para == F.Proposta);

        // A mesma chave para outra passagem e erro, nao repeticao.
        var outra = await Assert.ThrowsAsync<AcompanhamentoException>(() =>
            svc.ExecutarAsync(op, F.Perdido, new DadosPassagem { ChaveIdempotencia = "duplo-clique", Motivo = "x", TentarDeNovo = "nao" }, 7, AutoresPassagem.Usuario));
        Assert.Equal(409, outra.Status);
        Assert.Equal(CodigosAcompanhamento.ChaveIdempotenciaReutilizada, outra.Code);
    }

    [Fact]
    public async Task Sem_chave_de_idempotencia_nada_acontece()
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();
        var ex = await Assert.ThrowsAsync<AcompanhamentoException>(() =>
            svc.ExecutarAsync(op, F.Perdido, new DadosPassagem { Motivo = "x", TentarDeNovo = "nao" }, 7, AutoresPassagem.Usuario));
        Assert.Equal(CodigosAcompanhamento.ChaveIdempotenciaObrigatoria, ex.Code);
        Assert.Empty(store.Passagens(op));
    }

    [Fact]
    public async Task Duas_pessoas_ao_mesmo_tempo_so_uma_passa_e_a_outra_recebe_409()
    {
        var (svc, store) = Servico();
        var loja = store.NovaLoja(SituacoesComerciais.Ativo, testeConcluido: true);

        var tentativas = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            try
            {
                var para = i % 2 == 0 ? F.Pausa : F.Saindo;
                await svc.ExecutarAsync(loja, para, new DadosPassagem
                {
                    ChaveIdempotencia = "k" + i, De = F.Operando, Motivo = "ferias", DataVolta = new DateOnly(2026, 12, 1), Quando = "agora"
                }, i, AutoresPassagem.Usuario);
                return 200;
            }
            catch (AcompanhamentoException ex)
            {
                return ex.Status;
            }
        })).ToArray();
        var status = await Task.WhenAll(tentativas);

        Assert.Equal(1, status.Count(s => s == 200));
        Assert.All(status.Where(s => s != 200), s => Assert.Equal(409, s));
        Assert.Single(store.Passagens(loja));
        Assert.Equal(2, store.Estado(loja).Versao); // so a passagem que venceu subiu a versao
    }

    [Fact]
    public async Task Mesma_chave_em_paralelo_grava_uma_vez()
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();
        var dados = new DadosPassagem { ChaveIdempotencia = "igual", Motivo = "sem verba", TentarDeNovo = "30" };

        var resultados = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            Task.Run(() => svc.ExecutarAsync(op, F.Perdido, dados, 7, AutoresPassagem.Usuario))));

        Assert.Single(resultados.Select(r => r.Passagem.Id).Distinct());
        Assert.Equal(9, resultados.Count(r => r.Repetida));
        Assert.Single(store.Passagens(op));
        Assert.Equal(new DateOnly(2026, 12, 2), store.Estado(op).RetomarEm);
    }

    [Fact]
    public async Task Segunda_prorrogacao_fica_pendente_ate_o_gestor_aprovar()
    {
        var (svc, store) = Servico();
        var loja = store.NovaLoja(SituacoesComerciais.Teste);
        var fim = store.FimDoTeste(loja);

        var p1 = await svc.ExecutarAsync(loja, F.Teste, new DadosPassagem { ChaveIdempotencia = "p1", Motivo = "falha nossa", Dias = 7 }, 7, AutoresPassagem.Usuario);
        Assert.Equal(StatusPassagem.Feita, p1.Passagem.Status);
        Assert.Equal(fim!.Value.AddDays(7), store.FimDoTeste(loja));

        var p2 = await svc.ExecutarAsync(loja, F.Teste, new DadosPassagem { ChaveIdempotencia = "p2", Motivo = "pediu mais", Dias = 15 }, 7, AutoresPassagem.Usuario);
        Assert.Equal(StatusPassagem.PendenteAprovacao, p2.Passagem.Status);
        Assert.Equal(fim.Value.AddDays(7), store.FimDoTeste(loja)); // a data nao mudou
        Assert.Equal(F.Teste, p2.FaseAtual);
    }

    [Fact]
    public async Task Reimplantar_nao_reinicia_teste_e_volta_a_operar()
    {
        var (svc, store) = Servico();
        var loja = store.NovaLoja(SituacoesComerciais.Ativo, testeConcluido: true);

        await svc.ExecutarAsync(loja, F.Implantacao, new DadosPassagem { ChaveIdempotencia = "r", Motivo = "troca_de_dono" }, 7, AutoresPassagem.Usuario);
        Assert.Equal(SituacoesComerciais.Implantacao, store.Situacao(loja));
        Assert.True(store.PodeAtivarPelaApiLegada(loja)); // ja testou
        await Assert.ThrowsAsync<AcompanhamentoException>(() =>
            svc.ExecutarAsync(loja, F.Teste, new DadosPassagem { ChaveIdempotencia = "t" }, 7, AutoresPassagem.Usuario));

        var volta = await svc.ExecutarAsync(loja, F.Operando, new DadosPassagem { ChaveIdempotencia = "v" }, 7, AutoresPassagem.Usuario);
        Assert.Equal(F.Operando, volta.FaseAtual);
        Assert.Equal(SituacoesComerciais.Ativo, store.Situacao(loja));
        Assert.DoesNotContain("cobranca_cancelada", store.Efeitos);
    }

    [Fact]
    public async Task Perder_desativa_a_loja_e_retomar_volta_para_a_conversa_com_historico()
    {
        var (svc, store) = Servico();
        var loja = store.NovaLoja(SituacoesComerciais.Teste);

        await svc.ExecutarAsync(loja, F.Perdido, new DadosPassagem { ChaveIdempotencia = "l", Motivo = "preço", TentarDeNovo = "90" }, 7, AutoresPassagem.Usuario);
        Assert.Equal(SituacoesComerciais.Bloqueado, store.Situacao(loja));

        var volta = await svc.ExecutarAsync(loja, F.Conversa, new DadosPassagem { ChaveIdempotencia = "c" }, 7, AutoresPassagem.Usuario);
        Assert.Equal(F.Conversa, volta.FaseAtual);
        Assert.Equal(3, volta.Historico.Count); // inicio, perdido, conversa
    }

    [Fact]
    public async Task Situacao_mudada_fora_do_acompanhamento_concilia_a_fase_como_sistema()
    {
        var (svc, store) = Servico();
        var loja = store.NovaLoja(SituacoesComerciais.Teste);
        store.MudarSituacaoPorFora(loja, SituacoesComerciais.Ativo); // pagamento da assinatura

        var avaliacao = await svc.AvaliarAsync(loja, F.Pausa, null);
        Assert.Equal(F.Operando, avaliacao.FaseAtual);
        var conciliacao = store.Historico(loja).Last();
        Assert.Equal(AutoresPassagem.Sistema, conciliacao.AutorTipo);
        Assert.Equal(F.Operando, conciliacao.Para);
        Assert.True(store.Estado(loja).TesteConcluidoEm.HasValue);
    }

    [Fact]
    public async Task Fase_desconhecida_e_loja_inexistente()
    {
        var (svc, _) = Servico();
        Assert.Equal(CodigosAcompanhamento.FaseInvalida, (await Assert.ThrowsAsync<AcompanhamentoException>(() =>
            svc.AvaliarAsync(Guid.NewGuid(), "ativo", null))).Code);
        Assert.Equal(404, (await Assert.ThrowsAsync<AcompanhamentoException>(() =>
            svc.AvaliarAsync(Guid.NewGuid(), F.Proposta, null))).Status);
    }

    // ---- fechamento legado (/api/crm/oportunidades/{id}/fechar)

    [Fact]
    public async Task Fechamento_legado_aceita_fechar_na_conversa_com_aceite_sem_proposta()
    {
        var repo = RepoLegado(out var oportunidadeId);
        CrmCloseOpportunityCommand? command = null;
        repo.Setup(r => r.FecharOportunidadeAsync(It.IsAny<CrmCloseOpportunityCommand>()))
            .Callback<CrmCloseOpportunityCommand>(c => command = c)
            .ReturnsAsync(new CrmCloseOpportunityResult { ContratoId = Guid.NewGuid() });
        var crm = new CrmService(repo.Object, Mock.Of<IReservaRepository>(), new ConfigurationBuilder().Build(), NullLogger<CrmService>.Instance);

        await crm.FecharOportunidadeAsync(7, oportunidadeId, new FecharCrmOportunidadeRequest
        {
            StatusInicial = "implantacao", DiaVencimento = 10, Aceite = "verbal", ValorMensal = 199
        });

        Assert.NotNull(command);
        Assert.Equal(199m, command!.MensalidadeSaas);
        Assert.Null(command.EmpresaId); // sem confirmacao, nada e ligado por nome
    }

    [Fact]
    public async Task Fechamento_legado_sem_proposta_e_sem_aceite_continua_recusado()
    {
        var repo = RepoLegado(out var oportunidadeId);
        var crm = new CrmService(repo.Object, Mock.Of<IReservaRepository>(), new ConfigurationBuilder().Build(), NullLogger<CrmService>.Instance);

        await Assert.ThrowsAsync<RequestValidationException>(() => crm.FecharOportunidadeAsync(7, oportunidadeId,
            new FecharCrmOportunidadeRequest { StatusInicial = "implantacao", DiaVencimento = 10 }));
        await Assert.ThrowsAsync<RequestValidationException>(() => crm.FecharOportunidadeAsync(7, oportunidadeId,
            new FecharCrmOportunidadeRequest { StatusInicial = "implantacao", DiaVencimento = 10, Aceite = "talvez" }));
        repo.Verify(r => r.FecharOportunidadeAsync(It.IsAny<CrmCloseOpportunityCommand>()), Times.Never);
    }

    private static Mock<ICrmRepository> RepoLegado(out Guid oportunidadeId)
    {
        var id = Guid.NewGuid();
        oportunidadeId = id;
        var repo = new Mock<ICrmRepository>();
        repo.Setup(r => r.ObterOportunidadeAsync(id)).ReturnsAsync(new CrmOportunidadeRow
        {
            Id = id, NomeEmpresa = "Bistrô Alecrim", NomeContato = "Ana", Telefone = "+5534991480112", Tipo = "saas",
            Coluna = "conversa", Status = "ativo", ResponsavelUsuarioId = 7
        });
        repo.Setup(r => r.ListarEmpresasParaAssociacaoAsync()).ReturnsAsync(Array.Empty<CrmEmpresaExistente>());
        repo.Setup(r => r.ListarEstabelecimentosDasEmpresasAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync(Array.Empty<CrmEstabelecimentoDaEmpresaRow>());
        return repo;
    }

    // ---- banco real (TEST_DELIVERY_DATABASE): trava, chave unica e portao da API legada

    [DeliveryDatabaseFact]
    public async Task Banco_passagens_concorrentes_so_uma_grava_e_a_chave_repete()
    {
        await using var db = await Banco.Criar(SituacoesComerciais.Ativo);
        var svc = db.Servico();

        var status = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(async () =>
        {
            try
            {
                await svc.ExecutarAsync(db.Loja, i % 2 == 0 ? F.Pausa : F.Saindo, new DadosPassagem
                {
                    ChaveIdempotencia = "k" + i, De = F.Operando, Motivo = "ferias", DataVolta = new DateOnly(2030, 1, 1), Quando = "agora"
                }, 7, AutoresPassagem.Usuario);
                return 200;
            }
            catch (AcompanhamentoException ex) { return ex.Status; }
        })));

        Assert.Equal(1, status.Count(s => s == 200));
        Assert.All(status.Where(s => s != 200), s => Assert.Equal(409, s));
        Assert.Equal(1, await db.Scalar<long>("SELECT count(*) FROM acompanhamento_passagem"));
        Assert.Equal(2, await db.Scalar<long>("SELECT count(*) FROM acompanhamento_historico WHERE tipo = 'fase'"));

        var fase = await db.Scalar<string>("SELECT fase FROM acompanhamento");
        var chave = await db.Scalar<string>("SELECT chave_idempotencia FROM acompanhamento_passagem");
        var repetida = await svc.ExecutarAsync(db.Loja, fase, new DadosPassagem
        {
            ChaveIdempotencia = chave, Motivo = "ferias", DataVolta = new DateOnly(2030, 1, 1), Quando = "agora"
        }, 7, AutoresPassagem.Usuario);
        Assert.True(repetida.Repetida);
        Assert.Equal(1, await db.Scalar<long>("SELECT count(*) FROM acompanhamento_passagem"));
    }

    [DeliveryDatabaseFact]
    public async Task Banco_api_legada_nao_ativa_loja_em_implantacao_e_nada_bloqueia_no_fim_do_teste()
    {
        await using var db = await Banco.Criar(SituacoesComerciais.Implantacao);
        var situacao = new SituacaoComercialService(db.Source, new MemoryCache(new MemoryCacheOptions()), Mock.Of<IProntidaoService>());

        var ex = await Assert.ThrowsAsync<SituacaoComercialException>(() =>
            situacao.AplicarAsync(db.Loja, new AcaoSituacaoRequest("ativar", null, "personalizado", null), 7));
        Assert.Equal(422, ex.Status);
        Assert.Equal(SituacaoComercialRules.CodigoTesteObrigatorio, ex.Code);
        Assert.Equal("implantacao", await db.Scalar<string>("SELECT situacao_comercial FROM estabelecimentos"));

        await db.Execute("UPDATE estabelecimentos SET situacao_comercial = 'teste', operacao_iniciada_em = NOW() - INTERVAL '20 days', teste_termina_em = NOW() - INTERVAL '5 days'");
        Assert.Single(await situacao.ListarTestesVencidosAsync());
        Assert.Equal("teste", await db.Scalar<string>("SELECT situacao_comercial FROM estabelecimentos")); // continua operando
    }

    private sealed class Banco : IAsyncDisposable
    {
        public Guid Loja { get; } = Guid.NewGuid();
        public NpgsqlDataSource Source { get; private set; } = null!;
        private readonly string _schema = "acompanhamento_" + Guid.NewGuid().ToString("N");
        private string _base = "";

        public static async Task<Banco> Criar(string situacao)
        {
            APIBack.Infrastructure.DapperConfiguration.Configure();
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
            if (options.Host is not ("localhost" or "127.0.0.1")) throw new InvalidOperationException("Use somente PostgreSQL local.");
            var db = new Banco { _base = options.ConnectionString };
            await using (var cx = new NpgsqlConnection(db._base)) { await cx.OpenAsync(); await cx.ExecuteAsync($"CREATE SCHEMA {db._schema}"); }
            options.SearchPath = db._schema;
            db.Source = NpgsqlDataSource.Create(options.ConnectionString);
            await db.Execute(@"
CREATE TYPE modulo_enum AS ENUM ('GERAL','PEDIDOS','MAPA','DELIVERY','WHATSAPP');
CREATE TABLE empresas (id uuid PRIMARY KEY, nome_fantasia text, cnpj text);
CREATE TABLE estabelecimentos (id uuid PRIMARY KEY, id_empresa uuid, nome_fantasia text, situacao_comercial text NOT NULL, plano_comercial text,
  motoboys_base int NOT NULL DEFAULT 0, modulos_ativos modulo_enum[] NOT NULL DEFAULT '{}', operacao_iniciada_em timestamptz, teste_termina_em timestamptz,
  bloqueado_em timestamptz, bloqueado_por int, bloqueado_motivo text);
CREATE TABLE motoboy_pacotes (id bigserial PRIMARY KEY, estabelecimento_id uuid, quantidade int, inicio_utc timestamptz, fim_utc timestamptz, cancelado_em timestamptz);
CREATE TABLE pacotes_comerciais (chave text PRIMARY KEY, modulos_ativos modulo_enum[], motoboys_base int);
CREATE TABLE estabelecimento_situacao_log (id bigserial PRIMARY KEY, estabelecimento_id uuid, acao text, situacao_antes text, situacao_depois text,
  motivo text, usuario_id int, detalhes jsonb, criado_em timestamptz DEFAULT NOW());
INSERT INTO estabelecimentos (id, nome_fantasia, situacao_comercial, operacao_iniciada_em)
VALUES (@L, 'Loja', @S, CASE WHEN @S = 'implantacao' THEN NULL ELSE NOW() - INTERVAL '30 days' END);", new { L = db.Loja, S = situacao });
            await db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261127_01_acompanhamento_fases.sql")));
            return db;
        }

        public IAcompanhamentoService Servico()
        {
            var prontidao = new Mock<IProntidaoService>();
            return new AcompanhamentoService(new AcompanhamentoPostgresStore(Source, new MemoryCache(new MemoryCacheOptions())), prontidao.Object,
                Mock.Of<ICrmService>());
        }

        public async Task Execute(string sql, object? param = null)
        {
            await using var cx = await Source.OpenConnectionAsync();
            await cx.ExecuteAsync(sql, param);
        }

        public async Task<T> Scalar<T>(string sql)
        {
            await using var cx = await Source.OpenConnectionAsync();
            return (await cx.ExecuteScalarAsync<T>(sql))!;
        }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            await using var cx = new NpgsqlConnection(_base);
            await cx.OpenAsync();
            await cx.ExecuteAsync($"DROP SCHEMA IF EXISTS {_schema} CASCADE");
        }
    }

    // ---- apoio

    /// <summary>Loja com tudo pronto para sair de <paramref name="fase"/>. Ja testou quem opera, ou quem volta da reimplantacao.</summary>
    private static LojaFatos LojaPronta(string fase, string? para = null) => new()
    {
        Fase = fase,
        TemOportunidade = true,
        TemLoja = !F.EmConquista(fase),
        Documento = "11222333000181",
        QuemDecide = "Ana",
        PedidosPorDia = 40,
        DorPrincipal = "motoboy atrasa",
        PendenciasBloqueantes = 0,
        TesteConcluido = fase is F.Operando or F.Saindo or F.Pausa or F.Encerrado || (fase == F.Implantacao && para == F.Operando),
        TemProposta = fase == F.Proposta
    };

    private static DadosPassagem DadosCompletos() => new()
    {
        QuemDecide = "Ana", PedidosPorDia = 40, DorPrincipal = "motoboy atrasa", Motivo = "recomeco", Aceite = "assinado", Plano = "turbo",
        Documento = Cnpj, FormaPagamento = "pix", Dias = 7, Quando = "aviso_30_dias", OQueFez = "desconto no próximo ciclo", LojaAvisada = true,
        DataVolta = new DateOnly(2026, 12, 1), TentarDeNovo = "30"
    };

    private static (AcompanhamentoService, MemoriaStore) Servico(int pendencias = 0)
    {
        var store = new MemoriaStore();
        var prontidao = new Mock<IProntidaoService>();
        prontidao.Setup(p => p.ObterAsync(It.IsAny<Guid>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProntidaoDto("implantacao", 100, pendencias == 0, pendencias, null, null, Array.Empty<ProntidaoItemDto>()));
        var crm = new Mock<ICrmService>();
        crm.Setup(c => c.ListarCandidatosAssociacaoAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(Array.Empty<CrmCandidatoAssociacaoDto>());
        return (new AcompanhamentoService(store, prontidao.Object, crm.Object, new Relogio()), store);
    }

    private sealed class Relogio : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Agora;
    }

    /// <summary>
    /// Store em memoria com a mesma semantica do PostgreSQL: uma trava por loja (FOR UPDATE), tudo-ou-nada no commit e a situacao
    /// da loja mudando pelo mesmo portao (<see cref="SituacaoComercialRules.PodeAtivar"/>).
    /// </summary>
    private sealed class MemoriaStore : IAcompanhamentoStore
    {
        private readonly ConcurrentDictionary<Guid, Registro> _registros = new();
        public ConcurrentBag<string> Efeitos { get; } = new();

        private sealed class Registro
        {
            public AcompanhamentoEstado Estado = new();
            public List<PassagemRegistroDto> Passagens = new();
            public List<HistoricoDto> Historico = new();
            public SemaphoreSlim Trava = new(1, 1);
            public bool TemProposta;
            public DateTimeOffset? OperacaoIniciadaEm;
            public DateTimeOffset? TesteTerminaEm;
        }

        public Guid NovaOportunidade() => Novo(new AcompanhamentoEstado { OportunidadeId = Guid.NewGuid(), Fase = F.Conversa }, null);

        public Guid NovaLoja(string situacao, bool testeConcluido = false)
        {
            var id = Novo(new AcompanhamentoEstado
            {
                EstabelecimentoId = Guid.NewGuid(), SituacaoLoja = situacao, Fase = F.PadraoDaSituacao(situacao),
                TesteConcluidoEm = testeConcluido ? Agora.AddDays(-60) : null
            }, situacao == SituacoesComerciais.Implantacao ? null : Agora.AddDays(-5));
            if (situacao == SituacoesComerciais.Teste) _registros[id].TesteTerminaEm = Agora.AddDays(10);
            return id;
        }

        private Guid Novo(AcompanhamentoEstado estado, DateTimeOffset? operacao)
        {
            estado.Id = Guid.NewGuid();
            estado.Versao = 1;
            estado.FaseDesde = Agora;
            var r = new Registro { Estado = estado, OperacaoIniciadaEm = operacao };
            r.Historico.Add(new HistoricoDto(1, "fase", null, estado.Fase, Agora, null, AutoresPassagem.Sistema, "Início do acompanhamento", null));
            _registros[estado.Id] = r;
            return estado.Id;
        }

        public AcompanhamentoEstado Estado(Guid id) => _registros[id].Estado;
        public string? Situacao(Guid id) => _registros[id].Estado.SituacaoLoja;
        public DateTimeOffset? FimDoTeste(Guid id) => _registros[id].TesteTerminaEm;
        public IReadOnlyList<PassagemRegistroDto> Passagens(Guid id) => _registros[id].Passagens;
        public IReadOnlyList<HistoricoDto> Historico(Guid id) => _registros[id].Historico;
        public bool PodeAtivarPelaApiLegada(Guid id) =>
            SituacaoComercialRules.PodeAtivar(_registros[id].Estado.SituacaoLoja!, _registros[id].OperacaoIniciadaEm);
        public void MudarSituacaoPorFora(Guid id, string situacao) => _registros[id].Estado.SituacaoLoja = situacao;

        public Task<AcompanhamentoEstado?> ResolverAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(_registros.TryGetValue(id, out var r) ? Copia(r.Estado) : null);

        public async Task<IAcompanhamentoTransacao> AbrirAsync(Guid acompanhamentoId, CancellationToken ct)
        {
            var r = _registros[acompanhamentoId];
            await r.Trava.WaitAsync(ct);
            var tx = new Transacao(this, r);
            tx.Conciliar();
            return tx;
        }

        public Task<IReadOnlyList<HistoricoDto>> HistoricoAsync(Guid acompanhamentoId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<HistoricoDto>>(_registros[acompanhamentoId].Historico.ToList());

        private static AcompanhamentoEstado Copia(AcompanhamentoEstado e) => (AcompanhamentoEstado)typeof(AcompanhamentoEstado)
            .GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(e, null)!;

        private sealed class Transacao : IAcompanhamentoTransacao
        {
            private readonly MemoriaStore _store;
            private readonly Registro _r;
            private readonly List<PassagemRegistroDto> _passagens = new();
            private readonly List<HistoricoDto> _historico = new();
            private readonly List<string> _efeitos = new();
            private bool _temProposta;
            private DateTimeOffset? _operacao, _fimTeste;

            public Transacao(MemoriaStore store, Registro r)
            {
                _store = store;
                _r = r;
                Estado = Copia(r.Estado);
                _temProposta = r.TemProposta;
                _operacao = r.OperacaoIniciadaEm;
                _fimTeste = r.TesteTerminaEm;
            }

            public AcompanhamentoEstado Estado { get; }

            public void Conciliar()
            {
                if (!Estado.EstabelecimentoId.HasValue) return;
                var nova = F.Reconciliar(Estado.Fase, Estado.SituacaoLoja);
                if (nova != null) MudarFase(nova, null, AutoresPassagem.Sistema, "Situação da loja na Gestão", null);
                // A conciliacao vale mesmo sem passagem (como o commit do GET).
                Gravar();
            }

            public Task<PassagemRegistroDto?> PassagemPorChaveAsync(string chave, CancellationToken ct) =>
                Task.FromResult(_r.Passagens.FirstOrDefault(p => p.ChaveIdempotencia == chave));

            public Task<bool> CobrancaEmAbertoAsync(CancellationToken ct) => Task.FromResult(false);
            public Task<bool> TemPropostaAsync(CancellationToken ct) => Task.FromResult(_temProposta);

            public Task<PassagemRegistroDto> AplicarAsync(PassagemAplicacao a, CancellationToken ct)
            {
                var registro = new PassagemRegistroDto(Guid.NewGuid(), a.De, a.Definicao.Para, a.FaseResultante, a.Definicao.Nome, a.Status,
                    a.UsuarioId, a.AutorTipo, a.Motivo, a.Chave, a.Agora);
                _passagens.Add(registro);
                switch (a.Efeito)
                {
                    case EfeitoPassagem.MontarProposta: _temProposta = true; break;
                    case EfeitoPassagem.VoltarAConversar: _temProposta = false; break;
                    case EfeitoPassagem.FecharNaConversa or EfeitoPassagem.PropostaAceita or EfeitoPassagem.TesteSemCompromisso:
                        if (!Estado.EstabelecimentoId.HasValue)
                        {
                            Estado.EstabelecimentoId = Guid.NewGuid();
                            _efeitos.Add("loja_criada_implantacao");
                        }
                        Estado.SituacaoLoja = SituacoesComerciais.Implantacao;
                        Estado.TesteQuandoPronta = a.TesteQuandoPronta;
                        break;
                    case EfeitoPassagem.ComecarTeste:
                        Estado.SituacaoLoja = SituacoesComerciais.Teste;
                        _operacao = a.Agora;
                        _fimTeste = a.Agora.AddDays(SituacaoComercialRules.DiasTeste);
                        break;
                    case EfeitoPassagem.ProrrogarTeste when a.Status == StatusPassagem.Feita:
                        _fimTeste = _fimTeste!.Value.AddDays(a.DiasProrrogacao!.Value);
                        Estado.ProrrogacoesNoTeste++;
                        break;
                    case EfeitoPassagem.AtivarPlano or EfeitoPassagem.VoltarAOperar:
                        if (!SituacaoComercialRules.PodeAtivar(Estado.SituacaoLoja!, _operacao))
                            throw new SituacaoComercialException(422, SituacaoComercialRules.CodigoTesteObrigatorio, "teste");
                        Estado.SituacaoLoja = SituacoesComerciais.Ativo;
                        Estado.TesteConcluidoEm ??= a.Agora;
                        break;
                    case EfeitoPassagem.Reimplantar: Estado.SituacaoLoja = SituacoesComerciais.Implantacao; break;
                    case EfeitoPassagem.Encerrar or EfeitoPassagem.PararLoja: Estado.SituacaoLoja = SituacoesComerciais.Bloqueado; break;
                    case EfeitoPassagem.Perder:
                        if (Estado.EstabelecimentoId.HasValue) Estado.SituacaoLoja = SituacoesComerciais.Bloqueado;
                        Estado.RetomarEm = a.RetomarEm;
                        break;
                    case EfeitoPassagem.DeclararPausa: Estado.PausaAte = a.PausaAte; break;
                }

                Estado.Versao++;
                if (a.Status == StatusPassagem.Feita && Estado.Fase != a.FaseResultante)
                    MudarFase(a.FaseResultante, a.UsuarioId, a.AutorTipo, a.Motivo, registro.Id);
                return Task.FromResult(registro);
            }

            private void MudarFase(string para, int? usuario, string autor, string? motivo, Guid? passagem)
            {
                if (Estado.Fase == F.Teste && para == F.Operando) Estado.TesteConcluidoEm ??= Agora;
                _historico.Add(new HistoricoDto(_r.Historico.Count + _historico.Count + 1, "fase", Estado.Fase, para, Agora, usuario, autor, motivo, passagem));
                Estado.Fase = para;
                Estado.FaseDesde = Agora;
            }

            public Task CommitAsync(CancellationToken ct)
            {
                Gravar();
                return Task.CompletedTask;
            }

            private void Gravar()
            {
                _r.Estado = Copia(Estado);
                _r.Passagens.AddRange(_passagens);
                _r.Historico.AddRange(_historico);
                _r.TemProposta = _temProposta;
                _r.OperacaoIniciadaEm = _operacao;
                _r.TesteTerminaEm = _fimTeste;
                foreach (var e in _efeitos) _store.Efeitos.Add(e);
                _passagens.Clear();
                _historico.Clear();
                _efeitos.Clear();
            }

            public ValueTask DisposeAsync()
            {
                _r.Trava.Release();
                return ValueTask.CompletedTask;
            }
        }
    }
}
