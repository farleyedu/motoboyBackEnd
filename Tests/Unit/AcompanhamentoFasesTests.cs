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
        Assert.Equal(new[] { "quemDecide", "aceite", "plano", "documento", "valor" }, def.Requisitos.Select(r => r.Chave));
        Assert.DoesNotContain(def.Requisitos, r => r.Chave == "proposta"); // fecha sem proposta formal

        var avaliacao = PassagemMotor.Avaliar(Guid.NewGuid(), 1, new LojaFatos { Fase = F.Conversa }, F.Implantacao,
            new DadosPassagem { QuemDecide = "Ana", Aceite = "por telefone", Plano = "rota", Documento = "123", ValorMensal = 199 }, Agora);
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
        var dados = new DadosPassagem { ChaveIdempotencia = "duplo-clique", De = F.Conversa, Documento = Cnpj };

        var primeira = await svc.ExecutarAsync(op, F.Teste, dados, 7, AutoresPassagem.Usuario);
        var segunda = await svc.ExecutarAsync(op, F.Teste, dados, 7, AutoresPassagem.Usuario);

        Assert.False(primeira.Repetida);
        Assert.True(segunda.Repetida);
        Assert.Equal(primeira.Passagem.Id, segunda.Passagem.Id);
        Assert.Equal(primeira.Versao, segunda.Versao);
        Assert.Single(store.Passagens(op));
        Assert.Single(segunda.Historico, h => h.Para == F.Implantacao);

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


    // ---- jornada comercial: Proposta so com envio registrado

    private const string Whatsapp = "whatsapp";

    private static PropostaRequest RascunhoPronto(decimal? valor = 249m) => new()
    {
        QuemDecide = "Ana", PedidosPorDia = 40, DorPrincipal = "motoboy atrasa", ValorMensal = valor, ValorImplantacao = 100, Plano = "rota"
    };

    private static EnviarPropostaRequest Envio(PropostaDto v, string chave = "envio-1", string canal = Whatsapp) => new()
    {
        Canal = canal, Destinatario = "Ana (dona)", ChaveIdempotencia = chave, VersaoEsperada = v.Edicao
    };

    private static async Task<(AcompanhamentoService Svc, MemoriaStore Store, Guid Op, PropostaDto Enviada)> EmProposta(decimal valor = 249m)
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();
        var rascunho = (await svc.CriarPropostaAsync(op, RascunhoPronto(valor), 7)).Proposta;
        var enviada = await svc.EnviarPropostaAsync(op, rascunho.Id, Envio(rascunho), 7);
        return (svc, store, op, enviada.Proposta);
    }

    [Fact]
    public async Task Post_direto_para_proposta_e_recusado_apontando_registrar_envio()
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();

        // Nem com tudo preenchido: o envio nao vem do corpo da passagem.
        var ex = await Assert.ThrowsAsync<AcompanhamentoException>(() => svc.ExecutarAsync(op, F.Proposta,
            new DadosPassagem { ChaveIdempotencia = "k", De = F.Conversa, QuemDecide = "Ana", PedidosPorDia = 40, DorPrincipal = "motoboy" }, 7, AutoresPassagem.Usuario));

        Assert.Equal(422, ex.Status);
        var detalhes = Assert.IsType<RecusaDetalhesDto>(ex.Details);
        Assert.Equal("Registrar envio", detalhes.Caminho);
        Assert.Equal("propostaEnviada", Assert.Single(detalhes.RequisitosPendentes).Chave);
        Assert.Equal(F.Conversa, store.Estado(op).Fase);
        Assert.Empty(store.Passagens(op));
        Assert.Equal("Enviar a proposta", PassagensCatalogo.Obter(F.Conversa, F.Proposta)!.Nome);
    }

    [Fact]
    public async Task Rascunho_nao_muda_a_fase_e_nao_conta_para_aceitar()
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();

        var criado = await svc.CriarPropostaAsync(op, RascunhoPronto(), 7);

        Assert.Equal(StatusProposta.Rascunho, criado.Proposta.Status);
        Assert.True(criado.Proposta.Pronta);
        Assert.Equal(F.Conversa, criado.FaseAtual);
        Assert.False((await svc.AvaliarAsync(op, F.Implantacao, DadosCompletos())).Requisitos.Any(r => r.Chave == "proposta"));

        // Duas abas: o rascunho aberto continua sendo o mesmo.
        var denovo = await svc.CriarPropostaAsync(op, new PropostaRequest(), 7);
        Assert.True(denovo.Repetida);
        Assert.Equal(criado.Proposta.Id, denovo.Proposta.Id);
        Assert.Single(store.Propostas(op));
    }

    [Fact]
    public async Task Rascunho_aproveita_decisor_contato_e_valores_ja_conhecidos()
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();
        store.DefinirContexto(op, new PropostaContexto("Ana", "+5534991480112", "ana@loja.com", "Ana (dona)", 199m, 50m));

        var p = (await svc.CriarPropostaAsync(op, new PropostaRequest { DorPrincipal = "motoboy" }, 7)).Proposta;

        Assert.Equal("Ana (dona)", p.Dados.QuemDecide);
        Assert.Equal("+5534991480112", p.Dados.Telefone);
        Assert.Equal(199m, p.ValorMensal);
        Assert.Equal(50m, p.ValorImplantacao);
        Assert.Equal(new[] { "pedidosPorDia" }, p.Pendencias.Select(x => x.Chave));
        Assert.False(p.Pronta);
    }

    [Fact]
    public async Task Enviar_em_conversa_passa_para_proposta_na_mesma_transacao_com_validade_de_7_dias()
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();
        var rascunho = (await svc.CriarPropostaAsync(op, RascunhoPronto(), 7)).Proposta;

        var r = await svc.EnviarPropostaAsync(op, rascunho.Id, Envio(rascunho), 7);

        Assert.Equal(F.Proposta, r.FaseAtual);
        Assert.Equal(StatusProposta.Enviada, r.Proposta.Status);
        Assert.Equal(new DateOnly(2026, 11, 9), r.Proposta.ValidadeAte);
        Assert.Equal(7, r.Proposta.ApresentadaPor);
        Assert.Equal(Whatsapp, r.Proposta.Canal);
        Assert.NotNull(r.Passagem);
        Assert.Equal("Enviar a proposta", r.Passagem!.Nome);
        Assert.Equal(new[] { "conversa>proposta" }, store.Historico(op).Where(h => h.De != null).Select(h => $"{h.De}>{h.Para}"));
        Assert.Equal(StatusProposta.Enviada, store.Espelho(op)?.Status);
        Assert.Contains(store.Eventos(op), e => e.Tipo == "proposta_enviada" && e.Detalhe.Contains("\"canal\":\"whatsapp\""));
    }

    [Fact]
    public async Task Enviar_de_novo_com_a_mesma_chave_nao_faz_de_novo_e_outra_versao_nao_reaproveita_a_chave()
    {
        var (svc, store, op, enviada) = await EmProposta();
        var rascunho = store.Propostas(op).Single();

        var repetida = await svc.EnviarPropostaAsync(op, enviada.Id, Envio(enviada), 7);
        Assert.True(repetida.Repetida);
        Assert.Single(store.Passagens(op));
        Assert.Single(store.Eventos(op), e => e.Tipo == "proposta_enviada");

        var revisao = (await svc.CriarPropostaAsync(op, new PropostaRequest { ValorMensal = 199 }, 7)).Proposta;
        var ex = await Assert.ThrowsAsync<AcompanhamentoException>(() => svc.EnviarPropostaAsync(op, revisao.Id, Envio(revisao), 7));
        Assert.Equal(CodigosAcompanhamento.ChaveIdempotenciaReutilizada, ex.Code);
        Assert.Equal(StatusProposta.Enviada, store.Propostas(op).Single(p => p.Id == rascunho.Id).Status);
    }

    [Fact]
    public async Task Revisao_em_proposta_substitui_a_anterior_sem_mudar_a_fase_e_preserva_as_duas()
    {
        var (svc, store, op, v1) = await EmProposta();

        var rascunho = (await svc.CriarPropostaAsync(op, new PropostaRequest { ValorMensal = 199 }, 7)).Proposta;
        Assert.Equal(2, rascunho.Versao);
        Assert.Equal(1, rascunho.RevisaVersao);
        Assert.Equal(100m, rascunho.ValorImplantacao); // copiou a anterior
        Assert.Equal("Ana", rascunho.Dados.QuemDecide);

        var r = await svc.EnviarPropostaAsync(op, rascunho.Id, Envio(rascunho, "envio-2", "email"), 7);

        Assert.Equal(F.Proposta, r.FaseAtual);
        Assert.Null(r.Passagem);
        Assert.Single(store.Passagens(op));
        Assert.Equal(StatusProposta.Substituida, store.Propostas(op).Single(p => p.Id == v1.Id).Status);
        Assert.Equal(StatusProposta.Enviada, store.Propostas(op).Single(p => p.Id == rascunho.Id).Status);

        // Na lista, a atual e a vigente e as duas continuam legiveis.
        var lista = await svc.PropostasAsync(op);
        Assert.Equal(rascunho.Id, lista.Atual!.Id);
        Assert.Equal(new[] { 2, 1 }, lista.Versoes.Select(x => x.Versao));
    }

    [Theory]
    [InlineData(null, "valor")]
    [InlineData(0, "valor")]
    public async Task Proposta_sem_valor_ou_com_zero_sem_consciencia_nao_vai_para_envio(int? valor, string pendencia)
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();
        var rascunho = (await svc.CriarPropostaAsync(op, RascunhoPronto(valor), 7)).Proposta;

        var ex = await Assert.ThrowsAsync<AcompanhamentoException>(() => svc.EnviarPropostaAsync(op, rascunho.Id, Envio(rascunho), 7));

        Assert.Equal(CodigosAcompanhamento.RequisitosPendentes, ex.Code);
        Assert.Contains(((RecusaDetalhesDto)ex.Details!).RequisitosPendentes, r => r.Chave == pendencia);
        Assert.Equal(F.Conversa, store.Estado(op).Fase);
        Assert.Equal(StatusProposta.Rascunho, store.Propostas(op).Single().Status);
    }

    [Fact]
    public async Task Zero_consciente_e_aceito_e_fica_no_historico()
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();
        var pedido = RascunhoPronto(0);
        pedido.ValorZeroConsciente = true;
        var rascunho = (await svc.CriarPropostaAsync(op, pedido, 7)).Proposta;

        var r = await svc.EnviarPropostaAsync(op, rascunho.Id, Envio(rascunho), 7);

        Assert.True(r.Proposta.ValorZeroConsciente);
        Assert.Contains("R$ 0 consciente", store.Historico(op).Last().Motivo);
        Assert.Contains(store.Eventos(op), e => e.Tipo == "proposta_enviada" && e.Detalhe.Contains("\"valorZeroConsciente\":true"));

        // Na revisao o R$ 0 nao vem junto: cada versao confirma o seu.
        var revisao = (await svc.CriarPropostaAsync(op, new PropostaRequest(), 7)).Proposta;
        Assert.False(revisao.ValorZeroConsciente);
        Assert.False(revisao.Pronta);
    }

    [Fact]
    public async Task Mudar_o_valor_desfaz_a_confirmacao_de_zero()
    {
        var v = new PropostaVersao { ValorMensal = 0, ValorZeroConsciente = true };
        PropostaRegras.Sobrepor(v, new PropostaRequest { ValorMensal = 150 });
        Assert.False(v.ValorZeroConsciente);

        PropostaRegras.Sobrepor(v, new PropostaRequest { ValorMensal = 0 });
        Assert.False(v.ValorZeroConsciente);
        Assert.False(PropostaRegras.ValorDefinido(v.ValorMensal, v.ValorZeroConsciente));
    }

    [Theory]
    [InlineData("nao_informado", CodigosAcompanhamento.CanalInvalido)]
    [InlineData("telepatia", CodigosAcompanhamento.CanalInvalido)]
    public async Task Canal_que_nao_foi_declarado_e_recusado(string canal, string codigo)
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();
        var r = (await svc.CriarPropostaAsync(op, RascunhoPronto(), 7)).Proposta;
        var ex = await Assert.ThrowsAsync<AcompanhamentoException>(() => svc.EnviarPropostaAsync(op, r.Id, Envio(r, canal: canal), 7));
        Assert.Equal(codigo, ex.Code);
    }

    [Fact]
    public async Task Envio_sem_destinatario_sem_chave_ou_no_futuro_nao_entra()
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();
        var r = (await svc.CriarPropostaAsync(op, RascunhoPronto(), 7)).Proposta;

        async Task<string> Falha(EnviarPropostaRequest e) => (await Assert.ThrowsAsync<AcompanhamentoException>(() => svc.EnviarPropostaAsync(op, r.Id, e, 7))).Code;

        var semDestinatario = Envio(r); semDestinatario.Destinatario = " ";
        Assert.Equal(CodigosAcompanhamento.DestinatarioObrigatorio, await Falha(semDestinatario));
        var semChave = Envio(r); semChave.ChaveIdempotencia = null;
        Assert.Equal(CodigosAcompanhamento.ChaveIdempotenciaObrigatoria, await Falha(semChave));
        var futuro = Envio(r); futuro.EnviadaEm = Agora.AddDays(1);
        Assert.Equal(CodigosAcompanhamento.EnvioNoFuturo, await Falha(futuro));
        var semVersao = Envio(r); semVersao.VersaoEsperada = null;
        Assert.Equal(CodigosAcompanhamento.VersaoObrigatoria, await Falha(semVersao));
        Assert.Equal(StatusProposta.Rascunho, store.Propostas(op).Single().Status);
    }

    [Fact]
    public async Task Evidencia_tem_de_ser_mensagem_real_desta_oportunidade()
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();
        var real = Guid.NewGuid();
        store.Mensagens.Add(real);
        var r = (await svc.CriarPropostaAsync(op, RascunhoPronto(), 7)).Proposta;

        var alheia = Envio(r); alheia.MensagemId = Guid.NewGuid();
        var ex = await Assert.ThrowsAsync<AcompanhamentoException>(() => svc.EnviarPropostaAsync(op, r.Id, alheia, 7));
        Assert.Equal(CodigosAcompanhamento.EvidenciaInvalida, ex.Code);

        var certa = Envio(r); certa.MensagemId = real;
        Assert.Equal(real, (await svc.EnviarPropostaAsync(op, r.Id, certa, 7)).Proposta.EvidenciaMensagemId);
    }

    [Fact]
    public async Task Edicao_e_envio_conferem_a_versao_que_a_tela_viu()
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();
        var r = (await svc.CriarPropostaAsync(op, RascunhoPronto(), 7)).Proposta;

        var editado = (await svc.EditarPropostaAsync(op, r.Id, new PropostaRequest { ValorMensal = 300, VersaoEsperada = r.Edicao }, 7)).Proposta;
        Assert.Equal(300m, editado.ValorMensal);
        Assert.True(editado.Edicao > r.Edicao);

        var edicao = await Assert.ThrowsAsync<AcompanhamentoException>(() =>
            svc.EditarPropostaAsync(op, r.Id, new PropostaRequest { ValorMensal = 1, VersaoEsperada = r.Edicao }, 8));
        Assert.Equal(409, edicao.Status);
        Assert.Equal(CodigosAcompanhamento.PropostaMudou, edicao.Code);
        var envio = await Assert.ThrowsAsync<AcompanhamentoException>(() => svc.EnviarPropostaAsync(op, r.Id, Envio(r), 8));
        Assert.Equal(CodigosAcompanhamento.PropostaMudou, envio.Code);
        Assert.Equal(300m, store.Propostas(op).Single().ValorMensal);
    }

    [Fact]
    public async Task Proposta_enviada_nao_se_edita_so_se_revisa()
    {
        var (svc, _, op, enviada) = await EmProposta();
        var ex = await Assert.ThrowsAsync<AcompanhamentoException>(() =>
            svc.EditarPropostaAsync(op, enviada.Id, new PropostaRequest { ValorMensal = 1, VersaoEsperada = enviada.Edicao }, 7));
        Assert.Equal(CodigosAcompanhamento.PropostaNaoEditavel, ex.Code);
    }

    [Fact]
    public async Task Dois_envios_ao_mesmo_tempo_so_um_passa()
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();
        var r = (await svc.CriarPropostaAsync(op, RascunhoPronto(), 7)).Proposta;

        var status = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(async () =>
        {
            try { await svc.EnviarPropostaAsync(op, r.Id, Envio(r, "k" + i), 7); return 200; }
            catch (AcompanhamentoException ex) { return ex.Status; }
        })));

        Assert.Equal(1, status.Count(s => s == 200));
        Assert.All(status.Where(s => s != 200), s => Assert.Equal(409, s));
        Assert.Single(store.Passagens(op));
        Assert.Single(store.Eventos(op), e => e.Tipo == "proposta_enviada");
    }

    [Fact]
    public async Task Mesmo_envio_em_paralelo_grava_uma_vez()
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();
        var r = (await svc.CriarPropostaAsync(op, RascunhoPronto(), 7)).Proposta;

        var resultados = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => svc.EnviarPropostaAsync(op, r.Id, Envio(r), 7))));

        Assert.Equal(9, resultados.Count(x => x.Repetida));
        Assert.Single(store.Passagens(op));
    }

    [Fact]
    public async Task Resposta_ajuste_vira_negociacao_e_recusa_nao_muda_a_fase()
    {
        var (svc, store, op, v1) = await EmProposta();

        var ajuste = await svc.ResponderPropostaAsync(op, v1.Id, new ResponderPropostaRequest { Resposta = "pediu_ajuste", Nota = "quer parcelar" }, 7);
        Assert.Equal(StatusProposta.EmNegociacao, ajuste.Proposta.Status);
        Assert.Equal("quer parcelar", ajuste.Proposta.RespostaNota);

        var repetida = await svc.ResponderPropostaAsync(op, v1.Id, new ResponderPropostaRequest { Resposta = "pediu_ajuste", Nota = "quer parcelar" }, 7);
        Assert.True(repetida.Repetida);
        Assert.Single(store.Eventos(op), e => e.Tipo == "proposta_resposta");

        var recusa = await svc.ResponderPropostaAsync(op, v1.Id, new ResponderPropostaRequest { Resposta = "recusou" }, 7);
        Assert.Equal(StatusProposta.Recusada, recusa.Proposta.Status);
        Assert.Equal(F.Proposta, recusa.FaseAtual);

        // Recusada nao se aceita; a saida e voltar a conversar ou perder.
        var aceite = await svc.AvaliarAsync(op, F.Implantacao, new DadosPassagem { Aceite = "verbal", Documento = Cnpj });
        Assert.Contains(aceite.Requisitos, r => r.Chave == "proposta" && !r.Pronto);
        var volta = await svc.ExecutarAsync(op, F.Conversa, new DadosPassagem { ChaveIdempotencia = "v", Motivo = "esfriou" }, 7, AutoresPassagem.Usuario);
        Assert.Equal(F.Conversa, volta.FaseAtual);
        Assert.Equal(StatusProposta.Recusada, store.Propostas(op).Single().Status); // recusada fica como foi
    }

    [Theory]
    [InlineData("interessado")]
    [InlineData("sem_resposta")]
    public async Task Resposta_sem_desfecho_mantem_a_proposta_vigente(string resposta)
    {
        var (svc, _, op, v1) = await EmProposta();
        var r = await svc.ResponderPropostaAsync(op, v1.Id, new ResponderPropostaRequest { Resposta = resposta }, 7);
        Assert.Equal(StatusProposta.Enviada, r.Proposta.Status);
        Assert.Equal(resposta, r.Proposta.Resposta);
    }

    [Fact]
    public async Task Resposta_invalida_ou_de_rascunho_e_recusada_e_a_chave_nao_serve_para_outra_resposta()
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();
        var r = (await svc.CriarPropostaAsync(op, RascunhoPronto(), 7)).Proposta;

        Assert.Equal(CodigosAcompanhamento.RespostaInvalida, (await Assert.ThrowsAsync<AcompanhamentoException>(() =>
            svc.ResponderPropostaAsync(op, r.Id, new ResponderPropostaRequest { Resposta = "talvez" }, 7))).Code);
        Assert.Equal(CodigosAcompanhamento.PropostaNaoEditavel, (await Assert.ThrowsAsync<AcompanhamentoException>(() =>
            svc.ResponderPropostaAsync(op, r.Id, new ResponderPropostaRequest { Resposta = "interessado" }, 7))).Code);

        var e = (await svc.EnviarPropostaAsync(op, r.Id, Envio(r), 7)).Proposta;
        await svc.ResponderPropostaAsync(op, e.Id, new ResponderPropostaRequest { Resposta = "interessado", ChaveIdempotencia = "r1" }, 7);
        var outra = await Assert.ThrowsAsync<AcompanhamentoException>(() =>
            svc.ResponderPropostaAsync(op, e.Id, new ResponderPropostaRequest { Resposta = "recusou", ChaveIdempotencia = "r1" }, 7));
        Assert.Equal(CodigosAcompanhamento.ChaveIdempotenciaReutilizada, outra.Code);
    }

    [Fact]
    public async Task Aceite_exige_versao_enviada_e_usa_o_valor_dela()
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();
        await svc.CriarPropostaAsync(op, RascunhoPronto(), 7);
        store.ForcarFase(op, F.Proposta); // rascunho antigo em Proposta: a migracao devolve, mas o portao segura sozinho

        var ex = await Assert.ThrowsAsync<AcompanhamentoException>(() => svc.ExecutarAsync(op, F.Implantacao,
            new DadosPassagem { ChaveIdempotencia = "a", Aceite = "verbal", Documento = Cnpj }, 7, AutoresPassagem.Usuario));
        Assert.Contains(((RecusaDetalhesDto)ex.Details!).RequisitosPendentes, r => r.Chave == "proposta");
        Assert.Empty(store.Passagens(op));

        var (svc2, store2, op2, enviada) = await EmProposta(315m);
        var aceita = await svc2.ExecutarAsync(op2, F.Implantacao,
            new DadosPassagem { ChaveIdempotencia = "a", Aceite = "assinado", Documento = Cnpj, ValorMensal = 1 }, 7, AutoresPassagem.Usuario);

        Assert.Equal(F.Implantacao, aceita.FaseAtual);
        Assert.Equal(StatusProposta.Aceita, store2.Propostas(op2).Single(p => p.Id == enviada.Id).Status);
        Assert.Equal(315m, store2.ValorDoContrato(op2)); // o da proposta, nao o do corpo
    }

    [Fact]
    public async Task Proposta_vencida_pode_ser_aceita_e_o_evento_registra()
    {
        var (svc, store, op, v) = await EmProposta();
        store.Relogio.Instante = Agora.AddDays(10);

        Assert.True((await svc.PropostasAsync(op)).Atual!.Vencida);
        var r = await svc.ExecutarAsync(op, F.Implantacao, new DadosPassagem { ChaveIdempotencia = "a", Aceite = "whatsapp", Documento = Cnpj }, 7, AutoresPassagem.Usuario);

        Assert.Equal(F.Implantacao, r.FaseAtual);
        Assert.Contains(store.Eventos(op), e => e.Tipo == "proposta_aceita" && e.Detalhe.Contains("\"aceitaAposValidade\":true"));
        Assert.Equal(v.Id, store.Propostas(op).Single(p => p.Status == StatusProposta.Aceita).Id);
    }

    [Fact]
    public async Task Proposta_legada_sem_mensalidade_nao_aceita_ate_revisar()
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();
        store.AdicionarProposta(op, new PropostaVersao { OportunidadeId = store.Estado(op).OportunidadeId!.Value, Versao = 1, Status = StatusProposta.Enviada, Canal = CanaisProposta.NaoInformado, Origem = OrigensProposta.CrmLegado });
        store.ForcarFase(op, F.Proposta);

        var ex = await Assert.ThrowsAsync<AcompanhamentoException>(() => svc.ExecutarAsync(op, F.Implantacao,
            new DadosPassagem { ChaveIdempotencia = "a", Aceite = "verbal", Documento = Cnpj }, 7, AutoresPassagem.Usuario));
        Assert.Equal("valor", Assert.Single(((RecusaDetalhesDto)ex.Details!).RequisitosPendentes).Chave);
    }

    [Fact]
    public async Task Renegociar_revisa_sem_mudar_a_fase_e_voltar_guarda_a_versao()
    {
        var (svc, store, op, v) = await EmProposta();

        var ex = await Assert.ThrowsAsync<AcompanhamentoException>(() =>
            svc.ExecutarAsync(op, F.Conversa, new DadosPassagem { ChaveIdempotencia = "r", Motivo = "renegociar" }, 7, AutoresPassagem.Usuario));
        Assert.Contains("revisão", Assert.Single(((RecusaDetalhesDto)ex.Details!).RequisitosPendentes).Pendencia);
        Assert.Equal(F.Proposta, store.Estado(op).Fase);

        await svc.ExecutarAsync(op, F.Conversa, new DadosPassagem { ChaveIdempotencia = "v", Motivo = "mudou_quem_decide" }, 7, AutoresPassagem.Usuario);
        Assert.Equal(StatusProposta.Guardada, store.Propostas(op).Single(p => p.Id == v.Id).Status);
    }

    [Theory]
    [InlineData("perdido")]
    [InlineData("teste")]
    public async Task Sair_guarda_a_versao_aberta_cancela_o_passo_e_retomar_nao_a_conta(string destino)
    {
        var (svc, store, op, v) = await EmProposta();
        store.AbrirPasso(op);

        await svc.ExecutarAsync(op, destino, new DadosPassagem { ChaveIdempotencia = "s", Motivo = "sem verba", TentarDeNovo = "nao", Documento = Cnpj }, 7, AutoresPassagem.Usuario);

        Assert.Equal(StatusProposta.Guardada, store.Propostas(op).Single(p => p.Id == v.Id).Status);
        Assert.False(store.TemPassoAberto(op));
        if (destino == F.Perdido)
        {
            await svc.ExecutarAsync(op, F.Conversa, new DadosPassagem { ChaveIdempotencia = "c" }, 7, AutoresPassagem.Usuario);
            var aceite = await svc.AvaliarAsync(op, F.Proposta, new DadosPassagem());
            Assert.Contains(aceite.Requisitos, r => r.Chave == "propostaEnviada" && !r.Pronto);
        }
    }

    [Fact]
    public async Task Fechar_na_conversa_exige_mensalidade_e_zero_so_consciente()
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();
        DadosPassagem Dados(string chave) => new() { ChaveIdempotencia = chave, De = F.Conversa, QuemDecide = "Ana", Aceite = "verbal", Plano = "rota", Documento = Cnpj };

        var sem = await Assert.ThrowsAsync<AcompanhamentoException>(() => svc.ExecutarAsync(op, F.Implantacao, Dados("a"), 7, AutoresPassagem.Usuario));
        Assert.Equal("valor", Assert.Single(((RecusaDetalhesDto)sem.Details!).RequisitosPendentes).Chave);
        var zeroSemCiencia = Dados("b"); zeroSemCiencia.ValorMensal = 0;
        await Assert.ThrowsAsync<AcompanhamentoException>(() => svc.ExecutarAsync(op, F.Implantacao, zeroSemCiencia, 7, AutoresPassagem.Usuario));
        Assert.Empty(store.Passagens(op));

        var zero = Dados("c"); zero.ValorMensal = 0; zero.ValorZeroConsciente = true;
        var r = await svc.ExecutarAsync(op, F.Implantacao, zero, 7, AutoresPassagem.Usuario);
        Assert.Contains("valor R$ 0 consciente", r.Historico.Single(h => h.Para == F.Implantacao).Motivo);
    }

    [Fact]
    public async Task Teste_sem_compromisso_segue_sem_valor()
    {
        var (svc, store) = Servico();
        var op = store.NovaOportunidade();
        var r = await svc.ExecutarAsync(op, F.Teste, new DadosPassagem { ChaveIdempotencia = "t", Documento = Cpf }, 7, AutoresPassagem.Usuario);
        Assert.Equal(F.Implantacao, r.FaseAtual);
    }

    [Fact]
    public async Task Proposta_em_teste_nao_muda_a_fase_e_sem_oportunidade_explica()
    {
        var (svc, store) = Servico();
        var loja = store.NovaLoja(SituacoesComerciais.Teste, oportunidade: true);
        var r = (await svc.CriarPropostaAsync(loja, RascunhoPronto(), 7)).Proposta;
        var enviada = await svc.EnviarPropostaAsync(loja, r.Id, Envio(r), 7);
        Assert.Equal(F.Teste, enviada.FaseAtual);
        Assert.Null(enviada.Passagem);
        Assert.Equal(StatusProposta.Enviada, enviada.Proposta.Status);

        var sem = store.NovaLoja(SituacoesComerciais.Teste);
        var ex = await Assert.ThrowsAsync<AcompanhamentoException>(() => svc.CriarPropostaAsync(sem, RascunhoPronto(), 7));
        Assert.Equal(CodigosAcompanhamento.PropostaSemOportunidade, ex.Code);
        Assert.False((await svc.PropostasAsync(sem)).PodeRegistrar);

        var operando = store.NovaLoja(SituacoesComerciais.Ativo, testeConcluido: true, oportunidade: true);
        Assert.Equal(CodigosAcompanhamento.PropostaForaDaFase, (await Assert.ThrowsAsync<AcompanhamentoException>(() =>
            svc.CriarPropostaAsync(operando, RascunhoPronto(), 7))).Code);
    }

    [Fact]
    public void Recusa_de_teste_para_conquista_aponta_o_que_existe()
    {
        var recusa = PassagensCatalogo.Recusa(F.Teste, F.Proposta, new LojaFatos { Fase = F.Teste })!;
        Assert.Equal("Registrar a proposta durante o teste, sem mudar a fase", recusa.Caminho);
    }

    [Theory]
    [InlineData("ativo", "proposta", false, "conversa")]
    [InlineData("ativo", "quase", false, "conversa")]
    [InlineData("ativo", "aguardando", true, "proposta")]
    [InlineData("ativo", "proposta", true, "proposta")]
    [InlineData("perdido", "proposta", true, "perdido")]
    [InlineData("fechado", "proposta", true, null)]
    public void Crm_legado_so_leva_a_proposta_com_envio_registrado(string status, string coluna, bool envio, string? fase) =>
        Assert.Equal(fase, F.FaseDaOportunidade(status, coluna, envio));

    [Fact]
    public void Espelho_para_o_crm_legado_traz_mensalidade_e_data_de_envio_no_fuso_comercial()
    {
        var v = new PropostaVersao
        {
            ValorMensal = 300, ValorImplantacao = 50, EnviadaEm = new DateTimeOffset(2026, 11, 3, 1, 30, 0, TimeSpan.Zero),
            Dados = new PropostaDados { MarketingMensalidade = 100 }
        };
        var json = System.Text.Json.JsonDocument.Parse(PropostaRegras.Espelho(v)).RootElement;
        Assert.Equal(200m, json.GetProperty("saaSMensalidade").GetDecimal());
        Assert.Equal(100m, json.GetProperty("marketingMensalidade").GetDecimal());
        Assert.Equal(50m, json.GetProperty("implantacao").GetDecimal());
        Assert.StartsWith("2026-11-02", json.GetProperty("enviadaEm").GetString());
    }

    // ---- datas no fuso comercial e textos honestos

    [Fact]
    public void Depois_das_21h_a_data_ainda_e_a_de_hoje()
    {
        var noite = new DateTimeOffset(2026, 11, 3, 1, 30, 0, TimeSpan.Zero); // 22:30 de 02/11 em Sao Paulo
        var loja = LojaPronta(F.Operando);
        var pausa = PassagemMotor.Avaliar(Guid.NewGuid(), 1, loja, F.Pausa,
            new DadosPassagem { Motivo = "férias", DataVolta = new DateOnly(2026, 11, 3) }, noite);
        Assert.True(pausa.PodeExecutar); // amanha no fuso, embora em UTC ja seja "hoje"

        var perder = PassagemMotor.Planejar(LojaPronta(F.Conversa), F.Perdido, new DadosPassagem { Motivo = "x", TentarDeNovo = "30" }, "k", 7, AutoresPassagem.Usuario, noite);
        Assert.Equal(new DateOnly(2026, 12, 2), perder.RetomarEm);
        var ativar = PassagemMotor.Planejar(LojaPronta(F.Teste), F.Operando, DadosCompletos(), "k", 7, AutoresPassagem.Usuario, noite);
        Assert.Equal(new DateOnly(2026, 11, 3), ativar.CobrancaIniciaEm);
        Assert.Equal(new DateOnly(2026, 11, 9), PropostaRegras.Validade(noite));
    }

    [Fact]
    public void Automacao_sem_worker_nao_e_prometida()
    {
        // So "Comecar o teste" acontece sem a equipe: a loja clica em Comecar a operar e a fase concilia.
        var sozinhas = PassagensCatalogo.Todas.Where(p => p.Quem == QuemDecide.SozinhoOuVoce).Select(p => $"{p.De}>{p.Para}");
        Assert.Equal(new[] { "implantacao>teste" }, sozinhas);
        Assert.All(PassagensCatalogo.Todas, p =>
        {
            Assert.DoesNotContain("Volta sozinha", p.Volta);
            Assert.DoesNotContain("começa sozinho", p.OQueAcontece);
            Assert.DoesNotContain("Suspende a cobrança", p.OQueAcontece);
        });
    }

    [Fact]
    public void Migracao_da_jornada_trava_a_devolucao_dos_dados_e_tem_rollback()
    {
        var pasta = Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery");
        var sql = File.ReadAllText(Path.Combine(pasta, "20261127_17_crm_jornada_comercial.sql"));
        Assert.Contains("IF NOT (SELECT primeira FROM crm_jornada_17_execucao)", sql); // atencao legitima nao vira precisa_ajuda a cada boot
        Assert.Contains("'Sem proposta enviada registrada: voltou para Conversa.'", sql);
        Assert.Contains("'nao_informado', 'crm_legado'", sql);
        Assert.True(File.Exists(Path.Combine(pasta, "rollback", "20261127_17_crm_jornada_comercial.down.sql")));
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

    [Fact]
    public async Task Modal_do_crm_legado_grava_versao_enviada_sem_mudar_o_contrato()
    {
        var repo = RepoLegado(out var id);
        var crm = new CrmService(repo.Object, Mock.Of<IReservaRepository>(), new ConfigurationBuilder().Build(), NullLogger<CrmService>.Instance);
        repo.Setup(r => r.AtualizarOportunidadeAsync(It.IsAny<CrmOportunidadeRow>())).Returns(Task.CompletedTask);

        try
        {
            await crm.AtualizarOportunidadeAsync(7, id, new AtualizarCrmOportunidadeRequest { Proposta = new CrmPropostaDto { SaaSMensalidade = 249, MarketingMensalidade = 50, Implantacao = 100 } });
        }
        catch (NullReferenceException)
        {
            // O resumo final depende de mais leituras do repositorio; aqui so importa o que foi gravado.
        }

        repo.Verify(r => r.RegistrarPropostaLegadaAsync(id, 7, It.IsAny<string>(), 299m, 100m, It.IsAny<DateTimeOffset?>()), Times.Once);
    }

    [Fact]
    public async Task Mover_para_proposta_no_crm_legado_grava_versao_enviada()
    {
        var repo = RepoLegado(out var id);
        var crm = new CrmService(repo.Object, Mock.Of<IReservaRepository>(), new ConfigurationBuilder().Build(), NullLogger<CrmService>.Instance);

        try
        {
            await crm.MoverOportunidadeAsync(7, id, new MoverCrmOportunidadeRequest { ColunaDestino = "proposta", Proposta = new CrmPropostaDto { SaaSMensalidade = 199 } });
        }
        catch (NullReferenceException)
        {
        }

        repo.Verify(r => r.RegistrarPropostaLegadaAsync(id, 7, It.IsAny<string>(), 199m, It.IsAny<decimal?>(), It.IsAny<DateTimeOffset?>()), Times.Once);
    }

    [Fact]
    public async Task Troca_de_responsavel_no_crm_legado_chega_na_ficha()
    {
        var repo = RepoLegado(out var id);
        repo.Setup(r => r.ListarEquipeZippyAsync()).ReturnsAsync(new[] { new CrmResponsavelRow { Id = 7 }, new CrmResponsavelRow { Id = 9 } });
        var crm = new CrmService(repo.Object, Mock.Of<IReservaRepository>(), new ConfigurationBuilder().Build(), NullLogger<CrmService>.Instance);

        try
        {
            await crm.AtualizarOportunidadeAsync(7, id, new AtualizarCrmOportunidadeRequest { ResponsavelUsuarioId = 9 });
        }
        catch (NullReferenceException)
        {
        }

        repo.Verify(r => r.EspelharResponsavelLegadoAsync(id, 9, 7, 7), Times.Once);
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

    // ---- jornada comercial no banco real: migracao, rollback e o SQL dos stores

    private const string SqlCrm = @"
CREATE TYPE crm_oportunidade_status AS ENUM ('ativo', 'perdido', 'fechado');
CREATE TYPE crm_kanban_col AS ENUM ('conversa', 'reuniao', 'proposta', 'quase', 'aguardando');
CREATE TABLE usuario (id int PRIMARY KEY, nome text, email text, is_super_admin boolean, deleted_at timestamptz);
INSERT INTO usuario (id, nome, is_super_admin) VALUES (7, 'Farley', TRUE), (8, 'Socio', TRUE);
CREATE TABLE crm_oportunidade (id uuid PRIMARY KEY DEFAULT gen_random_uuid(), nome_empresa text NOT NULL DEFAULT 'Bistrô', nome_contato text NOT NULL DEFAULT 'Ana', telefone text NOT NULL DEFAULT '+5534991480112',
  email text, cnpj text, tipo text DEFAULT 'saas', responsavel_usuario_id int, coluna crm_kanban_col NOT NULL DEFAULT 'conversa', status crm_oportunidade_status NOT NULL DEFAULT 'ativo',
  proposta_json jsonb, proposta_enviada_em timestamptz, valor_mensal_est numeric, valor_impl_est numeric, deleted_at timestamptz, closed_at timestamptz, updated_at timestamptz DEFAULT NOW());
CREATE TABLE crm_oportunidade_historico (id bigserial PRIMARY KEY, id_oportunidade uuid, id_usuario int NOT NULL, acao text, detalhe text);
CREATE TABLE crm_contrato (id uuid PRIMARY KEY DEFAULT gen_random_uuid(), id_oportunidade uuid, estabelecimento_id uuid, responsavel_usuario_id int, created_at timestamptz DEFAULT NOW());";

    private static string Migracao(string nome) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", nome));
    private const string M17 = "20261127_17_crm_jornada_comercial.sql";

    /// <summary>Banco com CRM, ate a migracao 16 (a 17 e aplicada por quem chama, para semear dados antigos antes).</summary>
    private static async Task<Banco> BancoComCrm()
    {
        var db = await Banco.Criar(SituacoesComerciais.Ativo, antesDaMigracao: SqlCrm, ate17: false);
        return db;
    }

    private static async Task<Guid> Oportunidade(Banco db, string coluna, string? propostaJson = null, string? fase = "proposta")
    {
        await using var cx = await db.Source.OpenConnectionAsync();
        var id = await cx.ExecuteScalarAsync<Guid>(@"
INSERT INTO crm_oportunidade (coluna, proposta_json, proposta_enviada_em) VALUES (@C::crm_kanban_col, @J::jsonb, CASE WHEN @J::text IS NULL THEN NULL ELSE TIMESTAMPTZ '2026-11-01 15:00:00+00' END) RETURNING id;",
            new { C = coluna, J = propostaJson });
        if (fase != null) await cx.ExecuteAsync("INSERT INTO acompanhamento (oportunidade_id, fase) VALUES (@O, @F);", new { O = id, F = fase });
        return id;
    }

    [DeliveryDatabaseFact]
    public async Task Banco_migracao_devolve_para_conversa_converte_o_legado_e_e_idempotente()
    {
        await using var db = await BancoComCrm();
        var semEnvio = await Oportunidade(db, "proposta");
        await db.Execute(@"INSERT INTO acompanhamento_proposta_versao (oportunidade_id, versao, status, valor_mensal) VALUES (@O, 1, 'rascunho', 150);", new { O = semEnvio });
        var legado = await Oportunidade(db, "quase", "{\"saaSMensalidade\": 249, \"marketingMensalidade\": 50, \"implantacao\": 100}");
        var legadoSemAcompanhamento = await Oportunidade(db, "aguardando", "{\"saaSMensalidade\": 99}", fase: null);
        var soColuna = await Oportunidade(db, "proposta", fase: null);
        var perdida = await Oportunidade(db, "conversa", fase: "perdido");
        await db.Execute(@"
INSERT INTO acompanhamento_proposta_versao (oportunidade_id, versao, status) VALUES (@O, 1, 'rascunho');
INSERT INTO acompanhamento_proximo_passo (acompanhamento_id, tipo, data) SELECT id, 'ligacao', DATE '2026-01-01' FROM acompanhamento WHERE oportunidade_id = @O;
INSERT INTO acompanhamento_marcacao (acompanhamento_id, tipo, motivo, por_usuario_id) SELECT id, 'atencao', 'pedidos cairam', 7 FROM acompanhamento WHERE oportunidade_id = @O;
INSERT INTO acompanhamento_marcacao (acompanhamento_id, tipo, motivo, por_usuario_id, removida_em) SELECT id, 'atencao', 'antiga', 7, NOW() FROM acompanhamento WHERE oportunidade_id = @O;", new { O = perdida });

        await db.Execute(Migracao(M17));

        // Sem envio: Conversa, rascunho guardado, coluna do CRM e historico do sistema.
        Assert.Equal("conversa", await db.Scalar<string>($"SELECT fase FROM acompanhamento WHERE oportunidade_id = '{semEnvio}'"));
        Assert.Equal("guardada", await db.Scalar<string>($"SELECT status FROM acompanhamento_proposta_versao WHERE oportunidade_id = '{semEnvio}'"));
        Assert.Equal("conversa", await db.Scalar<string>($"SELECT coluna::text FROM crm_oportunidade WHERE id = '{semEnvio}'"));
        Assert.Equal(1, await db.Scalar<long>($"SELECT count(*) FROM acompanhamento_historico h JOIN acompanhamento a ON a.id = h.acompanhamento_id WHERE a.oportunidade_id = '{semEnvio}' AND h.de = 'proposta' AND h.para = 'conversa' AND h.autor_tipo = 'sistema'"));

        // Com proposta_json: versao enviada, canal nao informado, valores e validade pela data de envio; a fase fica.
        Assert.Equal("proposta", await db.Scalar<string>($"SELECT fase FROM acompanhamento WHERE oportunidade_id = '{legado}'"));
        Assert.Equal("enviada|nao_informado|crm_legado|299|100|2026-11-08", await db.Scalar<string>(
            $"SELECT concat_ws('|', status, canal, origem, trim_scale(valor_mensal), trim_scale(valor_implantacao), validade_ate) FROM acompanhamento_proposta_versao WHERE oportunidade_id = '{legado}'"));
        Assert.Equal(1, await db.Scalar<long>("SELECT count(*) FROM acompanhamento_relacionamento_evento WHERE tipo = 'proposta_enviada'"));
        Assert.Equal("aguardando", await db.Scalar<string>($"SELECT coluna::text FROM crm_oportunidade WHERE id = '{legadoSemAcompanhamento}'"));
        Assert.Equal("enviada", await db.Scalar<string>($"SELECT status FROM acompanhamento_proposta_versao WHERE oportunidade_id = '{legadoSemAcompanhamento}'"));
        Assert.Equal("conversa", await db.Scalar<string>($"SELECT coluna::text FROM crm_oportunidade WHERE id = '{soColuna}'"));

        // Saiu do quadro: versao e passo nao voltam a contar. Marcacao ativa convertida; a removida fica como foi gravada.
        Assert.Equal("guardada", await db.Scalar<string>($"SELECT status FROM acompanhamento_proposta_versao WHERE oportunidade_id = '{perdida}'"));
        Assert.Equal("cancelado", await db.Scalar<string>("SELECT estado FROM acompanhamento_proximo_passo"));
        Assert.Equal("precisa_ajuda", await db.Scalar<string>("SELECT tipo FROM acompanhamento_marcacao WHERE removida_em IS NULL"));
        Assert.Equal("atencao", await db.Scalar<string>("SELECT tipo FROM acompanhamento_marcacao WHERE removida_em IS NOT NULL"));

        // Roda a cada boot: nada duplica e uma "atencao" gravada depois continua "atencao".
        await db.Execute("UPDATE acompanhamento_marcacao SET removida_em = NOW() WHERE removida_em IS NULL; " +
            "INSERT INTO acompanhamento_marcacao (acompanhamento_id, tipo, motivo, por_usuario_id) SELECT id, 'atencao', 'nova', 7 FROM acompanhamento WHERE fase = 'perdido';");
        var versoes = await db.Scalar<long>("SELECT count(*) FROM acompanhamento_proposta_versao");
        await db.Execute(Migracao(M17));
        Assert.Equal(versoes, await db.Scalar<long>("SELECT count(*) FROM acompanhamento_proposta_versao"));
        Assert.Equal("atencao", await db.Scalar<string>("SELECT tipo FROM acompanhamento_marcacao WHERE removida_em IS NULL"));
        Assert.Equal(1, await db.Scalar<long>("SELECT count(*) FROM acompanhamento_historico WHERE de = 'proposta'"));
    }

    [DeliveryDatabaseFact]
    public async Task Banco_rollback_da_jornada_recusa_com_dados_e_devolve_o_esquema()
    {
        await using var db = await BancoComCrm();
        await db.Execute(Migracao(M17));
        var op = await Oportunidade(db, "proposta", fase: "conversa");
        var svc = db.Servico();
        var r = (await svc.CriarPropostaAsync(op, RascunhoPronto(), 7)).Proposta;
        await svc.EnviarPropostaAsync(op, r.Id, Envio(r), 7);

        var down = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "rollback", "20261127_17_crm_jornada_comercial.down.sql"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute(down));
        Assert.True(await db.Scalar<bool>("SELECT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'acompanhamento_proposta_versao'::regclass AND attname = 'edicao')"));

        await using (var cx = await db.Source.OpenConnectionAsync())
        {
            await cx.ExecuteAsync("SET zippy.rollback_confirmado = 'sim';");
            await cx.ExecuteAsync(down);
        }
        Assert.False(await db.Scalar<bool>("SELECT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'acompanhamento_proposta_versao'::regclass AND attname = 'edicao' AND NOT attisdropped)"));
        Assert.Equal("enviada", await db.Scalar<string>("SELECT status FROM acompanhamento_proposta_versao"));

        // O rollback nao bloqueia a migracao de voltar depois.
        await db.Execute(Migracao(M17));
    }

    [DeliveryDatabaseFact]
    public async Task Banco_ciclo_da_proposta_grava_espelho_passagem_evento_e_encerra_ao_perder()
    {
        await using var db = await BancoComCrm();
        await db.Execute(Migracao(M17));
        var op = await Oportunidade(db, "conversa", fase: null);
        await db.Execute($"UPDATE crm_oportunidade SET valor_mensal_est = 199, decisor = 'Ana' WHERE id = '{op}'");
        var svc = db.Servico();

        var r = (await svc.CriarPropostaAsync(op, new PropostaRequest { PedidosPorDia = 40, DorPrincipal = "atraso", ValorMensal = 249, ValorImplantacao = 100 }, 7)).Proposta;
        Assert.Equal("Ana", r.Dados.QuemDecide);
        Assert.Equal("conversa", await db.Scalar<string>("SELECT fase FROM acompanhamento"));
        Assert.True((await svc.CriarPropostaAsync(op, new PropostaRequest(), 7)).Repetida);

        var editado = (await svc.EditarPropostaAsync(op, r.Id, new PropostaRequest { ValorMensal = 300, Plano = "Rota", VersaoEsperada = r.Edicao }, 7)).Proposta;
        var enviado = await svc.EnviarPropostaAsync(op, r.Id, Envio(editado), 7);
        Assert.Equal(F.Proposta, enviado.FaseAtual);
        Assert.True((await svc.EnviarPropostaAsync(op, r.Id, Envio(editado), 7)).Repetida);
        Assert.Equal("proposta", await db.Scalar<string>($"SELECT coluna::text FROM crm_oportunidade WHERE id = '{op}'"));
        Assert.Equal(300m, await db.Scalar<decimal>($"SELECT (proposta_json ->> 'saaSMensalidade')::numeric FROM crm_oportunidade WHERE id = '{op}'"));
        Assert.Equal(1, await db.Scalar<long>("SELECT count(*) FROM crm_oportunidade_historico WHERE acao = 'Proposta registrada'"));
        Assert.Equal(enviado.Passagem!.Id, await db.Scalar<Guid>("SELECT passagem_id FROM acompanhamento_proposta_versao"));

        var ajuste = await svc.ResponderPropostaAsync(op, r.Id, new ResponderPropostaRequest { Resposta = "pediu_ajuste", Nota = "parcelar" }, 7);
        Assert.Equal(StatusProposta.EmNegociacao, ajuste.Proposta.Status);

        var v2 = (await svc.CriarPropostaAsync(op, new PropostaRequest { ValorMensal = 280 }, 7)).Proposta;
        Assert.Equal(2, v2.Versao);
        var e2 = await svc.EnviarPropostaAsync(op, v2.Id, Envio(v2, "envio-2", "email"), 7);
        Assert.Null(e2.Passagem);
        Assert.Equal("substituida,enviada", await db.Scalar<string>("SELECT string_agg(status, ',' ORDER BY versao) FROM acompanhamento_proposta_versao"));

        // O indice unico segura rascunho duplicado mesmo fora do servico.
        await svc.CriarPropostaAsync(op, new PropostaRequest { ValorMensal = 270 }, 7);
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute($"INSERT INTO acompanhamento_proposta_versao (oportunidade_id, versao, status) VALUES ('{op}', 9, 'rascunho')"));

        await db.Execute(@"INSERT INTO acompanhamento_proximo_passo (acompanhamento_id, tipo, data) SELECT id, 'ligacao', CURRENT_DATE FROM acompanhamento;
INSERT INTO acompanhamento_relacionamento (acompanhamento_id) SELECT id FROM acompanhamento;");
        await svc.ExecutarAsync(op, F.Perdido, new DadosPassagem { ChaveIdempotencia = "p", Motivo = "sem verba", TentarDeNovo = "nao" }, 7, AutoresPassagem.Usuario);
        Assert.Equal("substituida,guardada,guardada", await db.Scalar<string>("SELECT string_agg(status, ',' ORDER BY versao) FROM acompanhamento_proposta_versao"));
        Assert.Equal("cancelado", await db.Scalar<string>("SELECT estado FROM acompanhamento_proximo_passo"));
        Assert.Equal(2, await db.Scalar<long>("SELECT versao FROM acompanhamento_relacionamento"));
        Assert.Equal(1, await db.Scalar<long>("SELECT count(*) FROM acompanhamento_relacionamento_evento WHERE tipo = 'atividade_cancelada'"));
    }

    [DeliveryDatabaseFact]
    public async Task Banco_ciclo_da_atividade_fila_e_jornada_rodam_no_sql_real()
    {
        await using var db = await BancoComCrm();
        await db.Execute(Migracao(M17));
        var op = await Oportunidade(db, "conversa", fase: null);
        await db.Execute($"UPDATE crm_oportunidade SET responsavel_usuario_id = 7 WHERE id = '{op}'");
        var acompanhamento = new AcompanhamentoPostgresStore(db.Source, new MemoryCache(new MemoryCacheOptions()));
        var rel = new APIBack.Acompanhamento.Relacionamento.RelacionamentoService(new APIBack.Acompanhamento.Relacionamento.RelacionamentoPostgresStore(db.Source, acompanhamento));
        var hoje = APIBack.Acompanhamento.Relacionamento.RelacionamentoRegras.Hoje(DateTimeOffset.UtcNow);

        var r1 = await rel.DefinirProximoPassoAsync(op, new() { Tipo = "ligacao", Data = hoje, Nota = "ligar cedo" }, 7);
        var r2 = await rel.DefinirProximoPassoAsync(op, new() { Tipo = "ligacao", Data = hoje, Nota = "ligar tarde" }, 7);
        Assert.Equal("ligar tarde", r2.ProximoPasso!.Nota);
        Assert.Equal("Farley", r2.Responsavel!.Nome);

        var fila = await rel.AtividadesAsync("hoje", 7);
        var item = Assert.Single(fila.Itens);
        Assert.Equal("ligar tarde", item.Nota);
        Assert.Equal(r2.Versao, item.VersaoRelacionamento);
        var lembrete = Assert.Single((await rel.EquipeAsync(7)).Lembretes);
        Assert.Equal(("ligar tarde", "Farley"), (lembrete.Nota, lembrete.Responsavel!.Nome));

        var r3 = await rel.ReagendarPassoAsync(op, r2.ProximoPasso.Id, new() { Data = hoje.AddDays(2), Motivo = "viajou", VersaoEsperada = r2.Versao }, 7);
        Assert.Equal("ligar tarde", r3.ProximoPasso!.Nota);
        Assert.Single((await rel.AtividadesAsync("proximas", null)).Itens);

        var r4 = await rel.ConcluirPassoAsync(op, r3.ProximoPasso.Id, new()
        {
            Resultado = "interessado", Nota = "pediu proposta", VersaoEsperada = r3.Versao, Proximo = new() { Tipo = "proposta", Data = hoje.AddDays(1) }
        }, 7);
        Assert.Equal("proposta", r4.ProximoPasso!.Tipo);
        Assert.Equal(r3.ProximoPasso.Id, await db.Scalar<Guid>("SELECT origem_passo_id FROM acompanhamento_proximo_passo WHERE estado = 'aberto'"));
        Assert.True((await rel.ConcluirPassoAsync(op, r3.ProximoPasso.Id, new() { Resultado = "interessado" }, 7)).Repetida);
        var feita = Assert.Single((await rel.AtividadesAsync("feitas", 7)).Itens);
        Assert.Equal(("feito", "interessado", "pediu proposta"), (feita.Estado, feita.Resultado, feita.ResultadoNota));

        // Saude: atividade realizada conta como ultimo passo; agendar sozinho nao contaria.
        await using (var cx = await db.Source.OpenConnectionAsync())
        {
            var entrada = Assert.Single(await APIBack.Acompanhamento.Saude.SaudeSql.EntradasDasOportunidadesAsync(cx, CancellationToken.None));
            Assert.Equal(feita.RealizadoEm!.Value.ToUnixTimeSeconds(), entrada.UltimoPassoEm!.Value.ToUnixTimeSeconds());
        }

        await rel.CancelarPassoAsync(op, r4.ProximoPasso.Id, new() { Motivo = "loja fechou no feriado" }, 7);
        Assert.Equal(new[] { "cancelado", "feito", "reagendado", "substituido" },
            (await QueryStrings(db, "SELECT estado FROM acompanhamento_proximo_passo ORDER BY estado")).ToArray());

        var marcada = await rel.MarcarAsync(op, new() { Tipo = "precisa_ajuda", Motivo = "pedidos cairam", Categoria = "uso", AcaoEsperada = "ligar" }, 7);
        Assert.Equal("uso", marcada.Marcacao!.Categoria);
        Assert.Equal("marcada", marcada.Eventos.First().Tipo);
        var sem = await rel.DesmarcarAsync(op, "resolvido com a Ana", 7, marcada.Versao);
        Assert.Null(sem.Marcacao);
        Assert.Equal("resolvido com a Ana", sem.Eventos.First().Detalhe!.Value.GetProperty("resolucao").GetString());

        // Evento antigo em PascalCase e com o nome antigo continua legivel.
        await db.Execute("INSERT INTO acompanhamento_relacionamento_evento (acompanhamento_id, tipo, detalhe, em) SELECT id, 'marcou', '{\"Tipo\":\"atencao\",\"Motivo\":\"antigo\"}', NOW() - INTERVAL '10 days' FROM acompanhamento;");
        await db.Execute("INSERT INTO acompanhamento_contato_marco (acompanhamento_id, direcao, em, referencia) SELECT id, 'recebida', NOW() - INTERVAL '1 hour', 'wamid.1' FROM acompanhamento;");

        var itens = new List<APIBack.Acompanhamento.Relacionamento.JornadaItemDto>();
        string? cursor = null;
        do
        {
            var pagina = await rel.JornadaAsync(op, cursor, 3);
            itens.AddRange(pagina.Itens);
            cursor = pagina.ProximoCursor;
        } while (cursor != null);
        Assert.Equal(itens.Count, itens.Select(i => i.Id).Distinct().Count());
        Assert.Equal(itens.OrderByDescending(i => i.Em).Select(i => i.Id), itens.Select(i => i.Id));
        Assert.Contains(itens, i => i.Categoria == "atividade_executada" && i.Resultado == "interessado" && i.Detalhe == "pediu proposta" && i.Autor.Nome == "Farley");
        Assert.Contains(itens, i => i.Categoria == "contato" && i.Autor.Nome == "Sistema");
        Assert.Contains(itens, i => i.Categoria == "sinalizacao" && i.Detalhe == "antigo");
        Assert.Contains(itens, i => i.Categoria == "fase");
    }

    [DeliveryDatabaseFact]
    public async Task Banco_saida_registrada_muda_a_fase_com_passagem_e_passo_concorre_com_passagem_sem_travar()
    {
        await using var db = await BancoComCrm();
        await db.Execute(Migracao(M17));
        var acompanhamento = new AcompanhamentoPostgresStore(db.Source, new MemoryCache(new MemoryCacheOptions()));
        var rel = new APIBack.Acompanhamento.Relacionamento.RelacionamentoService(new APIBack.Acompanhamento.Relacionamento.RelacionamentoPostgresStore(db.Source, acompanhamento));
        var hoje = APIBack.Acompanhamento.Relacionamento.RelacionamentoRegras.Hoje(DateTimeOffset.UtcNow);

        var saida = await rel.RegistrarCancelamentoAsync(db.Loja, new()
        {
            ChaveIdempotencia = "s", Pedido = "quer sair", Leitura = "preco", Canal = "whatsapp", Resultado = "saiu", SaidaQuando = "aviso_30_dias"
        }, 7);
        Assert.Equal("saindo", saida.FaseAtual);
        Assert.Equal("saindo", await db.Scalar<string>("SELECT fase FROM acompanhamento"));
        Assert.Equal("aviso_30_dias", await db.Scalar<string>("SELECT saida_quando FROM acompanhamento"));
        Assert.Equal("Pediu para cancelar", await db.Scalar<string>("SELECT nome FROM acompanhamento_passagem"));
        Assert.Equal(1, await db.Scalar<long>("SELECT count(*) FROM acompanhamento_historico WHERE para = 'saindo' AND passagem_id IS NOT NULL"));

        // Passagem e relacionamento na mesma loja: a ordem das travas e a mesma, sem deadlock nem erro cru.
        var svc = db.Servico();
        await rel.DefinirProximoPassoAsync(db.Loja, new() { Tipo = "ligacao", Data = hoje }, 7);
        var r = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            try
            {
                if (i % 2 == 0) await svc.ExecutarAsync(db.Loja, F.Encerrado, new DadosPassagem { ChaveIdempotencia = "e" + i, De = F.Saindo }, 7, AutoresPassagem.Usuario);
                else await rel.DefinirProximoPassoAsync(db.Loja, new() { Tipo = "visita", Data = hoje.AddDays(i) }, 7);
                return "ok";
            }
            catch (AcompanhamentoException) { return "recusa"; }
            catch (PostgresException ex) { return ex.SqlState; }
        })));
        Assert.DoesNotContain(PostgresErrorCodes.DeadlockDetected, r);
    }

    private static async Task<IReadOnlyList<string>> QueryStrings(Banco db, string sql)
    {
        await using var cx = await db.Source.OpenConnectionAsync();
        return (await cx.QueryAsync<string>(sql)).ToList();
    }

    private sealed class Banco : IAsyncDisposable
    {
        public Guid Loja { get; } = Guid.NewGuid();
        public NpgsqlDataSource Source { get; private set; } = null!;
        private readonly string _schema = "acompanhamento_" + Guid.NewGuid().ToString("N");
        private string _base = "";

        public static async Task<Banco> Criar(string situacao, string? antesDaMigracao = null, bool ate17 = true)
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
            if (antesDaMigracao != null) await db.Execute(antesDaMigracao);
            foreach (var migracao in new[] { "20261127_01_acompanhamento_fases.sql", "20261127_04_crm_relacionamento.sql" })
                await db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", migracao)));
            if (ate17) await db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261127_17_crm_jornada_comercial.sql")));
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
        TemProposta = fase == F.Proposta,
        PropostaComValor = fase == F.Proposta,
        EnvioRegistrado = fase == F.Conversa
    };

    private static DadosPassagem DadosCompletos() => new()
    {
        QuemDecide = "Ana", PedidosPorDia = 40, DorPrincipal = "motoboy atrasa", Motivo = "recomeco", Aceite = "assinado", Plano = "turbo",
        Documento = Cnpj, FormaPagamento = "pix", Dias = 7, Quando = "aviso_30_dias", OQueFez = "desconto no próximo ciclo", LojaAvisada = true,
        DataVolta = new DateOnly(2026, 12, 1), TentarDeNovo = "30", ValorMensal = 249
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
        return (new AcompanhamentoService(store, prontidao.Object, crm.Object, store.Relogio), store);
    }

    private sealed class Relogio : TimeProvider
    {
        public DateTimeOffset Instante { get; set; } = Agora;
        public override DateTimeOffset GetUtcNow() => Instante;
    }

    /// <summary>
    /// Store em memoria com a mesma semantica do PostgreSQL: uma trava por loja (FOR UPDATE), tudo-ou-nada no commit e a situacao
    /// da loja mudando pelo mesmo portao (<see cref="SituacaoComercialRules.PodeAtivar"/>).
    /// </summary>
    private sealed class MemoriaStore : IAcompanhamentoStore
    {
        private readonly ConcurrentDictionary<Guid, Registro> _registros = new();
        public ConcurrentBag<string> Efeitos { get; } = new();
        public Relogio Relogio { get; } = new();
        /// <summary>Mensagens reais em gestao_mensagem desta oportunidade.</summary>
        public HashSet<Guid> Mensagens { get; } = new();

        private sealed class Registro
        {
            public AcompanhamentoEstado Estado = new();
            public List<PassagemRegistroDto> Passagens = new();
            public List<HistoricoDto> Historico = new();
            public SemaphoreSlim Trava = new(1, 1);
            public List<PropostaVersao> Propostas = new();
            public List<(string Tipo, string Detalhe)> Eventos = new();
            public PropostaContexto Contexto = new(null, null, null, null, null, null);
            public PropostaVersao? Espelho;
            public decimal? ValorDoContrato;
            public bool PassoAberto;
            public DateTimeOffset? OperacaoIniciadaEm;
            public DateTimeOffset? TesteTerminaEm;
        }

        public Guid NovaOportunidade() => Novo(new AcompanhamentoEstado { OportunidadeId = Guid.NewGuid(), Fase = F.Conversa }, null);

        public Guid NovaLoja(string situacao, bool testeConcluido = false, bool oportunidade = false)
        {
            var id = Novo(new AcompanhamentoEstado
            {
                OportunidadeId = oportunidade ? Guid.NewGuid() : null,
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
        public IReadOnlyList<PropostaVersao> Propostas(Guid id) => _registros[id].Propostas;
        public IReadOnlyList<(string Tipo, string Detalhe)> Eventos(Guid id) => _registros[id].Eventos;
        public void DefinirContexto(Guid id, PropostaContexto contexto) => _registros[id].Contexto = contexto;
        public PropostaVersao? Espelho(Guid id) => _registros[id].Espelho;
        public decimal? ValorDoContrato(Guid id) => _registros[id].ValorDoContrato;
        public void ForcarFase(Guid id, string fase) => _registros[id].Estado.Fase = fase;
        public void AbrirPasso(Guid id) => _registros[id].PassoAberto = true;
        public bool TemPassoAberto(Guid id) => _registros[id].PassoAberto;

        public void AdicionarProposta(Guid id, PropostaVersao v)
        {
            v.Id = Guid.NewGuid();
            _registros[id].Propostas.Add(v);
        }

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
            private List<PropostaVersao> _propostas;
            private readonly List<(string Tipo, string Detalhe)> _eventos = new();
            private PropostaVersao? _espelho;
            private decimal? _valorContrato;
            private bool _passoAberto;
            private DateTimeOffset? _operacao, _fimTeste;

            public Transacao(MemoriaStore store, Registro r)
            {
                _store = store;
                _r = r;
                Estado = Copia(r.Estado);
                _propostas = r.Propostas.Select(Clone).ToList();
                _espelho = r.Espelho;
                _valorContrato = r.ValorDoContrato;
                _passoAberto = r.PassoAberto;
                _operacao = r.OperacaoIniciadaEm;
                _fimTeste = r.TesteTerminaEm;
            }

            public AcompanhamentoEstado Estado { get; }

            private static PropostaVersao Clone(PropostaVersao v) => (PropostaVersao)typeof(PropostaVersao)
                .GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(v, null)!;

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
            public Task<PropostaVersao?> PropostaVigenteAsync(CancellationToken ct) =>
                Task.FromResult(_propostas.FirstOrDefault(v => StatusProposta.Vigente(v.Status)));
            public Task<IReadOnlyList<PropostaVersao>> PropostasAsync(CancellationToken ct) =>
                Task.FromResult<IReadOnlyList<PropostaVersao>>(_propostas.Select(Clone).ToList());
            public Task<PropostaContexto> ContextoPropostaAsync(CancellationToken ct) => Task.FromResult(_r.Contexto);

            public Task<PropostaVersao> CriarPropostaAsync(PropostaVersao nova, CancellationToken ct)
            {
                nova.Id = Guid.NewGuid();
                nova.CriadaEm = nova.AtualizadaEm = Agora;
                _propostas.Add(nova);
                return Task.FromResult(Clone(nova));
            }

            public Task<PropostaVersao> SalvarPropostaAsync(PropostaVersao versao, CancellationToken ct)
            {
                var salva = Clone(versao);
                salva.Edicao++;
                _propostas[_propostas.FindIndex(v => v.Id == versao.Id)] = salva;
                return Task.FromResult(Clone(salva));
            }

            public Task EspelharPropostaAsync(PropostaVersao versao, int? usuarioId, CancellationToken ct)
            {
                _espelho = Clone(versao);
                return Task.CompletedTask;
            }

            public Task<bool> MensagemConfereAsync(Guid mensagemId, CancellationToken ct) => Task.FromResult(_store.Mensagens.Contains(mensagemId));

            public Task EventoAsync(string tipo, object detalhe, int? usuarioId, CancellationToken ct)
            {
                _eventos.Add((tipo, APIBack.Acompanhamento.Relacionamento.RelacionamentoRegras.SerializarDetalhe(detalhe)));
                return Task.CompletedTask;
            }

            private void Guardar(bool passo)
            {
                foreach (var v in _propostas.Where(v => v.Status is StatusProposta.Rascunho || StatusProposta.Vigente(v.Status)))
                    v.Status = StatusProposta.Guardada;
                if (passo) _passoAberto = false;
            }

            public Task<PassagemRegistroDto> AplicarAsync(PassagemAplicacao a, CancellationToken ct)
            {
                var registro = new PassagemRegistroDto(Guid.NewGuid(), a.De, a.Definicao.Para, a.FaseResultante, a.Definicao.Nome, a.Status,
                    a.UsuarioId, a.AutorTipo, a.Motivo, a.Chave, a.Agora);
                _passagens.Add(registro);
                switch (a.Efeito)
                {
                    case EfeitoPassagem.VoltarAConversar: Guardar(passo: false); break;
                    case EfeitoPassagem.FecharNaConversa or EfeitoPassagem.PropostaAceita or EfeitoPassagem.TesteSemCompromisso:
                        if (a.Efeito == EfeitoPassagem.PropostaAceita)
                        {
                            var vigente = _propostas.Single(v => StatusProposta.Vigente(v.Status));
                            var vencida = PropostaRegras.Vencida(vigente, APIBack.Payments.Assinaturas.FusoComercial.Data(a.Agora));
                            vigente.Status = StatusProposta.Aceita;
                            Guardar(passo: false);
                            _valorContrato = vigente.ValorMensal;
                            _eventos.Add(("proposta_aceita", APIBack.Acompanhamento.Relacionamento.RelacionamentoRegras.SerializarDetalhe(new { vigente.Versao, aceitaAposValidade = vencida })));
                        }
                        else
                        {
                            Guardar(passo: true);
                        }
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
                    case EfeitoPassagem.Encerrar or EfeitoPassagem.PararLoja:
                        Guardar(passo: true);
                        Estado.SituacaoLoja = SituacoesComerciais.Bloqueado;
                        break;
                    case EfeitoPassagem.Perder:
                        Guardar(passo: true);
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
                _r.Propostas = _propostas.Select(Clone).ToList();
                _r.Eventos.AddRange(_eventos);
                _eventos.Clear();
                _r.Espelho = _espelho;
                _r.ValorDoContrato = _valorContrato;
                _r.PassoAberto = _passoAberto;
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
