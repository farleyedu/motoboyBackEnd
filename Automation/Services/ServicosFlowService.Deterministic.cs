using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Models;
using APIBack.Model;

namespace APIBack.Automation.Services
{
    public partial class ServicosFlowService
    {
        private static string? ResolveDeterministicState(string? contextoEstado, string? etapaAtendimento)
        {
            if (IsServiceState(contextoEstado))
            {
                return contextoEstado;
            }

            return IsServiceState(etapaAtendimento) ? etapaAtendimento : null;
        }

        private static bool DeveInterceptarFluxoDeterministico(
            string mensagemTexto,
            IReadOnlyList<ServicoCatalogItem> catalogo,
            ServicoAtendimento? atendimentoAtual)
        {
            if (string.IsNullOrWhiteSpace(mensagemTexto))
            {
                return false;
            }

            if (atendimentoAtual != null && IsOpenStatus(atendimentoAtual.Status) && IsServiceState(atendimentoAtual.EtapaAtual))
            {
                return true;
            }

            return EhPerguntaAmplaDeServicos(mensagemTexto) ||
                   MatchServices(mensagemTexto, catalogo).Count > 0;
        }

        private async Task<AssistantDecision> IniciarFluxoDeterministicoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            IReadOnlyList<ServicoCatalogItem> catalogo,
            ServicoAtendimento? atendimentoAtual,
            ConversationContext? contextoAtual,
            string mensagemTexto,
            bool viaNumeroCentral)
        {
            if (!string.IsNullOrWhiteSpace(cliente?.Nome))
            {
                var extrasServico = CriarSnapshotExtras(ObterDadosServico(contextoAtual, atendimentoAtual), new Dictionary<string, object?>
                {
                    [ChavePerguntaPendente] = PerguntaServico,
                    [ChaveTentativas] = 0
                });

                await PersistirEstadoDeterministicoAsync(
                    idConversa,
                    scope,
                    cliente,
                    contextoAtual,
                    atendimentoAtual,
                    servico: null,
                    estado: EstadoAguardandoServico,
                    status: "em_triagem",
                    reply: null,
                    viaNumeroCentral,
                    extrasServico,
                    intencaoDetalhe: "escolha_servico");

                var contextoServico = new ConversationContext
                {
                    Estado = EstadoAguardandoServico,
                    DadosColetados = extrasServico
                        .Where(item => item.Value != null)
                        .ToDictionary(item => item.Key, item => (object)item.Value!, StringComparer.OrdinalIgnoreCase)
                };

                var atendimentoAtualizado = await ObterAtendimentoAtualAsync(idConversa, scope);
                return await ProcessarAguardandoServicoDeterministicoAsync(
                    idConversa,
                    scope,
                    cliente,
                    catalogo,
                    contextoServico,
                    atendimentoAtualizado,
                    mensagemTexto,
                    viaNumeroCentral);
            }

            var nomeEstabelecimento = await ObterNomeEstabelecimentoServicoAsync(scope.IdEstabelecimento);
            var clienteSemNome = CriarClienteContextual(scope, cliente, null);
            var dadosAtuais = ObterDadosServico(contextoAtual, atendimentoAtual);
            var extras = CriarSnapshotExtras(dadosAtuais, new Dictionary<string, object?>
            {
                [ChaveMensagemPendente] = mensagemTexto,
                [ChavePerguntaPendente] = PerguntaNome,
                [ChaveCandidatosIds] = null,
                [ChaveTentativas] = 0
            });

            var reply = $"Ola! Voce esta falando com a equipe da {nomeEstabelecimento}. Antes de continuar, me fala seu nome.";
            await PersistirEstadoDeterministicoAsync(
                idConversa,
                scope,
                clienteSemNome,
                contextoAtual,
                atendimentoAtual,
                servico: null,
                estado: EstadoAguardandoNome,
                status: "aguardando_cliente",
                reply,
                viaNumeroCentral,
                extras,
                intencaoDetalhe: "coleta_nome");

            return CriarResposta(reply);
        }

        private async Task<AssistantDecision> ProcessarFluxoDeterministicoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            IReadOnlyList<ServicoCatalogItem> catalogo,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            string mensagemTexto,
            bool viaNumeroCentral)
        {
            var estado = ResolveDeterministicState(contextoAtual.Estado, atendimentoAtual?.EtapaAtual) ?? EstadoAguardandoNome;
            contextoAtual.Estado = estado;
            contextoAtual.DadosColetados ??= new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            if (TemTrocaPendente(contextoAtual, atendimentoAtual))
            {
                return await ProcessarConfirmacaoTrocaPendenteAsync(
                    idConversa,
                    scope,
                    cliente,
                    catalogo,
                    contextoAtual,
                    atendimentoAtual,
                    mensagemTexto,
                    viaNumeroCentral,
                    estado);
            }

            return estado switch
            {
                EstadoAguardandoNome => await ProcessarAguardandoNomeDeterministicoAsync(
                    idConversa, scope, cliente, catalogo, contextoAtual, atendimentoAtual, mensagemTexto, viaNumeroCentral),
                EstadoAguardandoServico => await ProcessarAguardandoServicoDeterministicoAsync(
                    idConversa, scope, cliente, catalogo, contextoAtual, atendimentoAtual, mensagemTexto, viaNumeroCentral),
                EstadoAguardandoVeiculo => await ProcessarAguardandoVeiculoDeterministicoAsync(
                    idConversa, scope, cliente, catalogo, contextoAtual, atendimentoAtual, mensagemTexto, viaNumeroCentral),
                EstadoOfertaDetalhes => await ProcessarOfertaDetalhesDeterministicoAsync(
                    idConversa, scope, cliente, catalogo, contextoAtual, atendimentoAtual, mensagemTexto, viaNumeroCentral),
                EstadoAguardandoMarca => await ProcessarAguardandoMarcaDeterministicoAsync(
                    idConversa, scope, cliente, catalogo, contextoAtual, atendimentoAtual, mensagemTexto, viaNumeroCentral),
                EstadoAguardandoConfirmacaoFinal => await ProcessarAguardandoConfirmacaoFinalDeterministicoAsync(
                    idConversa, scope, cliente, catalogo, contextoAtual, atendimentoAtual, mensagemTexto, viaNumeroCentral),
                EstadoProntoAgendamento => await ProcessarProntoAgendamentoDeterministicoAsync(
                    idConversa, scope, cliente, catalogo, contextoAtual, atendimentoAtual, mensagemTexto, viaNumeroCentral),
                EstadoAguardandoConfirmacaoHumano => await ProcessarConfirmacaoHumanoAsync(
                    idConversa, scope, cliente, catalogo, contextoAtual, atendimentoAtual, mensagemTexto, viaNumeroCentral),
                _ => await ProcessarAguardandoNomeDeterministicoAsync(
                    idConversa, scope, cliente, catalogo, contextoAtual, atendimentoAtual, mensagemTexto, viaNumeroCentral)
            };
        }

        private async Task<AssistantDecision> ProcessarAguardandoNomeDeterministicoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            IReadOnlyList<ServicoCatalogItem> catalogo,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            string mensagemTexto,
            bool viaNumeroCentral)
        {
            var dados = ObterDadosServico(contextoAtual, atendimentoAtual);
            if (!TryExtrairNomeValidoServico(mensagemTexto, out var nome))
            {
                var mensagemPendente = DeveInterceptarFluxoDeterministico(mensagemTexto, catalogo, atendimentoAtual)
                    ? mensagemTexto
                    : ObterTexto(dados, ChaveMensagemPendente);

                var extrasFalha = CriarSnapshotExtras(dados, new Dictionary<string, object?>
                {
                    [ChaveMensagemPendente] = mensagemPendente,
                    [ChavePerguntaPendente] = PerguntaNome
                });

                const string replyFalha = "Antes de continuar, me fala seu nome.";
                await PersistirEstadoDeterministicoAsync(
                    idConversa,
                    scope,
                    CriarClienteContextual(scope, cliente, null),
                    contextoAtual,
                    atendimentoAtual,
                    servico: null,
                    estado: EstadoAguardandoNome,
                    status: "aguardando_cliente",
                    replyFalha,
                    viaNumeroCentral,
                    extrasFalha,
                    intencaoDetalhe: "coleta_nome");

                return CriarResposta(replyFalha);
            }

            await _clienteRepository.AtualizarNomeAsync(scope.IdCliente, scope.IdEstabelecimento, nome);
            var clienteAtualizado = CriarClienteContextual(scope, cliente, nome);
            var mensagemGuardada = ObterTexto(dados, ChaveMensagemPendente);

            if (!string.IsNullOrWhiteSpace(mensagemGuardada))
            {
                var extrasRetomada = CriarSnapshotExtras(dados, new Dictionary<string, object?>
                {
                    [ChaveMensagemPendente] = null,
                    [ChavePerguntaPendente] = PerguntaServico,
                    [ChaveTentativas] = 0
                });

                await PersistirEstadoDeterministicoAsync(
                    idConversa,
                    scope,
                    clienteAtualizado,
                    contextoAtual,
                    atendimentoAtual,
                    servico: null,
                    estado: EstadoAguardandoServico,
                    status: "em_triagem",
                    reply: null,
                    viaNumeroCentral,
                    extrasRetomada,
                    intencaoDetalhe: "escolha_servico");

                var contextoRetomada = new ConversationContext
                {
                    Estado = EstadoAguardandoServico,
                    DadosColetados = extrasRetomada
                        .Where(item => item.Value != null)
                        .ToDictionary(item => item.Key, item => (object)item.Value!, StringComparer.OrdinalIgnoreCase)
                };

                var atendimentoAtualizado = await ObterAtendimentoAtualAsync(idConversa, scope);
                return await ProcessarAguardandoServicoDeterministicoAsync(
                    idConversa,
                    scope,
                    clienteAtualizado,
                    catalogo,
                    contextoRetomada,
                    atendimentoAtualizado,
                    mensagemGuardada,
                    viaNumeroCentral);
            }

            var extras = CriarSnapshotExtras(dados, new Dictionary<string, object?>
            {
                [ChavePerguntaPendente] = PerguntaServico,
                [ChaveTentativas] = 0
            });

            var reply = $"Perfeito, {ObterPrimeiroNome(nome)}. Me fala qual servico voce quer ver.";
            await PersistirEstadoDeterministicoAsync(
                idConversa,
                scope,
                clienteAtualizado,
                contextoAtual,
                atendimentoAtual,
                servico: null,
                estado: EstadoAguardandoServico,
                status: "aguardando_cliente",
                reply,
                viaNumeroCentral,
                extras,
                intencaoDetalhe: "escolha_servico");

            return CriarResposta(reply);
        }

        private async Task<AssistantDecision> ProcessarAguardandoServicoDeterministicoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            IReadOnlyList<ServicoCatalogItem> catalogo,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            string mensagemTexto,
            bool viaNumeroCentral)
        {
            if (FoiPedidoHumano(mensagemTexto))
            {
                return await PerguntarConfirmacaoHumanoAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoAtual,
                    contextoAtual,
                    servico: null,
                    mensagemBase: "Se preferir, eu passo isso para a equipe.",
                    mensagemOrigem: mensagemTexto,
                    viaNumeroCentral);
            }

            var dados = ObterDadosServico(contextoAtual, atendimentoAtual);
            var candidatos = ResolverCandidatosDoContexto(dados, catalogo);
            var servicoSelecionado = candidatos.Count > 0
                ? ResolverServicoSelecionado(mensagemTexto, candidatos)
                : null;

            if (servicoSelecionado == null)
            {
                var matches = MatchServices(mensagemTexto, catalogo);
                var melhores = SelecionarMelhoresCandidatos(matches);
                if (melhores.Count == 1)
                {
                    servicoSelecionado = melhores[0].Servico;
                }
                else if (melhores.Count >= 2)
                {
                    var opcoes = melhores.Select(item => item.Servico.Nome).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
                    var ids = melhores.Select(item => item.Servico.Id.ToString()).ToArray();
                    var extrasEscolha = CriarSnapshotExtras(dados, new Dictionary<string, object?>
                    {
                        [ChaveCandidatosIds] = ids,
                        [ChavePerguntaPendente] = PerguntaServico,
                        [ChaveTentativas] = 0
                    });

                    var replyEscolha = $"Achei estes servicos parecidos. Me responde com o numero ou com o nome:\n{FormatarOpcoesEnumeradas(opcoes)}";
                    await PersistirEstadoDeterministicoAsync(
                        idConversa,
                        scope,
                        cliente,
                        contextoAtual,
                        atendimentoAtual,
                        servico: null,
                        estado: EstadoAguardandoServico,
                        status: "aguardando_cliente",
                        replyEscolha,
                        viaNumeroCentral,
                        extrasEscolha,
                        intencaoDetalhe: "escolha_servico");

                    return CriarResposta(replyEscolha);
                }
            }

            if (servicoSelecionado != null)
            {
                return await BloquearServicoEContinuarAsync(
                    idConversa,
                    scope,
                    cliente,
                    contextoAtual,
                    atendimentoAtual,
                    servicoSelecionado,
                    viaNumeroCentral);
            }

            var tentativas = ObterInt(dados, ChaveTentativas) + 1;
            var exemplos = catalogo.Select(item => item.Nome).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
            var reply = tentativas > 1
                ? $"Ainda nao consegui identificar o servico. Me fala o nome do servico que voce quer ver. Se ajudar, posso te orientar nestes exemplos:\n{FormatarOpcoesEnumeradas(exemplos)}"
                : "Me fala o nome do servico que voce quer ver.";

            var extrasFalha = CriarSnapshotExtras(dados, new Dictionary<string, object?>
            {
                [ChaveTentativas] = tentativas,
                [ChavePerguntaPendente] = PerguntaServico,
                [ChaveCandidatosIds] = null
            });

            await PersistirEstadoDeterministicoAsync(
                idConversa,
                scope,
                cliente,
                contextoAtual,
                atendimentoAtual,
                servico: null,
                estado: EstadoAguardandoServico,
                status: "aguardando_cliente",
                reply,
                viaNumeroCentral,
                extrasFalha,
                intencaoDetalhe: "escolha_servico");

            return CriarResposta(reply);
        }

        private async Task<AssistantDecision> ProcessarAguardandoVeiculoDeterministicoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            IReadOnlyList<ServicoCatalogItem> catalogo,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            string mensagemTexto,
            bool viaNumeroCentral)
        {
            var dados = ObterDadosServico(contextoAtual, atendimentoAtual);
            var servico = ResolverServicoDoContextoOuAtendimento(contextoAtual, atendimentoAtual, catalogo);
            if (servico == null)
            {
                return await RedirecionarParaServicoAsync(idConversa, scope, cliente, contextoAtual, atendimentoAtual, viaNumeroCentral);
            }

            var propostaTroca = await TentarCriarTrocaPendenteAsync(
                idConversa, scope, cliente, catalogo, contextoAtual, atendimentoAtual, mensagemTexto, viaNumeroCentral, EstadoAguardandoVeiculo);
            if (propostaTroca != null)
            {
                return propostaTroca;
            }

            if (FoiPedidoHumano(mensagemTexto))
            {
                return await PerguntarConfirmacaoHumanoAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoAtual,
                    contextoAtual,
                    servico,
                    "Se preferir, eu passo isso para a equipe.",
                    mensagemTexto,
                    viaNumeroCentral);
            }

            var veiculo = await ResolverVeiculoSelecionadoAsync(scope.IdEstabelecimento, mensagemTexto, servico, contextoAtual);
            if (veiculo == null)
            {
                var tentativas = ObterInt(dados, ChaveTentativas) + 1;
                var prefixo = tentativas > 1
                    ? BuildResumoDoQueJaSei(cliente, atendimentoAtual, servico, null, null, mencionarPendenciaVeiculo: true)
                    : null;
                var pergunta = string.Join(" ",
                    new[] { prefixo, MontarPerguntaVeiculo(servico) }
                    .Where(item => !string.IsNullOrWhiteSpace(item)));

                var extrasFalha = CriarSnapshotExtras(dados, new Dictionary<string, object?>
                {
                    [ChavePerguntaPendente] = PerguntaVeiculo,
                    [ChaveTentativas] = tentativas
                });

                await PersistirEstadoDeterministicoAsync(
                    idConversa,
                    scope,
                    cliente,
                    contextoAtual,
                    atendimentoAtual,
                    servico,
                    EstadoAguardandoVeiculo,
                    "aguardando_cliente",
                    pergunta,
                    viaNumeroCentral,
                    extrasFalha,
                    intencaoDetalhe: "coleta_veiculo");

                return CriarResposta(pergunta);
            }

            if (!veiculo.Compativel)
            {
                return await PerguntarConfirmacaoHumanoAsync(
                    idConversa,
                    scope,
                    cliente,
                    atendimentoAtual,
                    contextoAtual,
                    servico,
                    $"Entendi o veiculo, mas eu nao encontrei compatibilidade segura para {servico.Nome} nesse modelo.",
                    mensagemTexto,
                    viaNumeroCentral);
            }

            return await AtivarVeiculoEOferecerDetalhesAsync(
                idConversa,
                scope,
                cliente,
                contextoAtual,
                atendimentoAtual,
                servico,
                veiculo,
                viaNumeroCentral);
        }

        private async Task<AssistantDecision> ProcessarOfertaDetalhesDeterministicoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            IReadOnlyList<ServicoCatalogItem> catalogo,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            string mensagemTexto,
            bool viaNumeroCentral)
        {
            var servico = ResolverServicoDoContextoOuAtendimento(contextoAtual, atendimentoAtual, catalogo);
            if (servico == null)
            {
                return await RedirecionarParaServicoAsync(idConversa, scope, cliente, contextoAtual, atendimentoAtual, viaNumeroCentral);
            }

            var veiculo = ObterVeiculoAtual(atendimentoAtual, servico);
            if (veiculo == null || string.IsNullOrWhiteSpace(veiculo.NomeExibicao))
            {
                return await VoltarParaVeiculoAsync(idConversa, scope, cliente, contextoAtual, atendimentoAtual, servico, viaNumeroCentral);
            }

            var propostaTroca = await TentarCriarTrocaPendenteAsync(
                idConversa, scope, cliente, catalogo, contextoAtual, atendimentoAtual, mensagemTexto, viaNumeroCentral, EstadoOfertaDetalhes);
            if (propostaTroca != null)
            {
                return propostaTroca;
            }

            if (FoiPedidoHumano(mensagemTexto))
            {
                return await PerguntarConfirmacaoHumanoAsync(
                    idConversa, scope, cliente, atendimentoAtual, contextoAtual, servico,
                    "Se preferir, eu passo isso para a equipe.", mensagemTexto, viaNumeroCentral);
            }

            if (UsuarioPerguntouMarcasPeca(mensagemTexto))
            {
                return await EntregarMarcasAsync(idConversa, scope, cliente, contextoAtual, atendimentoAtual, servico, veiculo, viaNumeroCentral);
            }

            if (UsuarioPerguntouPreco(mensagemTexto))
            {
                return await EntregarDetalheIndividualAsync(
                    idConversa, scope, cliente, contextoAtual, atendimentoAtual, servico, veiculo, viaNumeroCentral, "valor");
            }

            if (UsuarioPerguntouDuracao(mensagemTexto))
            {
                return await EntregarDetalheIndividualAsync(
                    idConversa, scope, cliente, contextoAtual, atendimentoAtual, servico, veiculo, viaNumeroCentral, "tempo");
            }

            if (EhContinuacaoGenerica(mensagemTexto))
            {
                return await EntregarDetalhesPendentesAsync(
                    idConversa, scope, cliente, contextoAtual, atendimentoAtual, servico, veiculo, viaNumeroCentral);
            }

            var reply = MontarPromptDetalhesRestantes(contextoAtual, atendimentoAtual, servico, veiculo);
            await PersistirEstadoDeterministicoAsync(
                idConversa,
                scope,
                cliente,
                contextoAtual,
                atendimentoAtual,
                servico,
                EstadoOfertaDetalhes,
                "aguardando_cliente",
                reply,
                viaNumeroCentral,
                CriarSnapshotExtras(ObterDadosServico(contextoAtual, atendimentoAtual), new Dictionary<string, object?>
                {
                    [ChavePerguntaPendente] = PerguntaDetalhes
                }),
                intencaoDetalhe: "oferta_detalhes");

            return CriarResposta(reply);
        }

        private async Task<AssistantDecision> ProcessarAguardandoMarcaDeterministicoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            IReadOnlyList<ServicoCatalogItem> catalogo,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            string mensagemTexto,
            bool viaNumeroCentral)
        {
            var servico = ResolverServicoDoContextoOuAtendimento(contextoAtual, atendimentoAtual, catalogo);
            var veiculo = servico == null ? null : ObterVeiculoAtual(atendimentoAtual, servico);
            if (servico == null)
            {
                return await RedirecionarParaServicoAsync(idConversa, scope, cliente, contextoAtual, atendimentoAtual, viaNumeroCentral);
            }

            if (veiculo == null)
            {
                return await VoltarParaVeiculoAsync(idConversa, scope, cliente, contextoAtual, atendimentoAtual, servico, viaNumeroCentral);
            }

            if (UsuarioPerguntouPreco(mensagemTexto))
            {
                return await EntregarDetalheIndividualAsync(
                    idConversa, scope, cliente, contextoAtual, atendimentoAtual, servico, veiculo, viaNumeroCentral, "valor", manterEstadoMarca: true);
            }

            if (UsuarioPerguntouDuracao(mensagemTexto))
            {
                return await EntregarDetalheIndividualAsync(
                    idConversa, scope, cliente, contextoAtual, atendimentoAtual, servico, veiculo, viaNumeroCentral, "tempo", manterEstadoMarca: true);
            }

            if (EhContinuacaoGenerica(mensagemTexto))
            {
                var replyEscolha = "Agora eu so preciso que voce escolha a marca. Me responde com o numero ou com o nome.";
                await PersistirEstadoDeterministicoAsync(
                    idConversa,
                    scope,
                    cliente,
                    contextoAtual,
                    atendimentoAtual,
                    servico,
                    EstadoAguardandoMarca,
                    "aguardando_cliente",
                    replyEscolha,
                    viaNumeroCentral,
                    CriarSnapshotExtras(ObterDadosServico(contextoAtual, atendimentoAtual), new Dictionary<string, object?>
                    {
                        [ChavePerguntaPendente] = PerguntaMarca
                    }),
                    intencaoDetalhe: "escolha_marca");

                return CriarResposta(replyEscolha);
            }

            var marca = ResolverMarcaPecaSelecionada(mensagemTexto, veiculo, atendimentoAtual);
            if (marca == null)
            {
                var propostaTroca = await TentarCriarTrocaPendenteAsync(
                    idConversa, scope, cliente, catalogo, contextoAtual, atendimentoAtual, mensagemTexto, viaNumeroCentral, EstadoAguardandoMarca);
                if (propostaTroca != null)
                {
                    return propostaTroca;
                }

                var replyFalha = $"{BuildBrandsReply(servico, veiculo, null)}\nMe responde com o numero ou com o nome da marca.";
                await PersistirEstadoDeterministicoAsync(
                    idConversa,
                    scope,
                    cliente,
                    contextoAtual,
                    atendimentoAtual,
                    servico,
                    EstadoAguardandoMarca,
                    "aguardando_cliente",
                    replyFalha,
                    viaNumeroCentral,
                    CriarSnapshotExtras(ObterDadosServico(contextoAtual, atendimentoAtual), new Dictionary<string, object?>
                    {
                        [ChavePerguntaPendente] = PerguntaMarca
                    }),
                    intencaoDetalhe: "escolha_marca");

                return CriarResposta(replyFalha);
            }

            return await IrParaConfirmacaoFinalAsync(
                idConversa,
                scope,
                cliente,
                contextoAtual,
                atendimentoAtual,
                servico,
                veiculo,
                marca,
                viaNumeroCentral);
        }

        private async Task<AssistantDecision> ProcessarAguardandoConfirmacaoFinalDeterministicoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            IReadOnlyList<ServicoCatalogItem> catalogo,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            string mensagemTexto,
            bool viaNumeroCentral)
        {
            var servico = ResolverServicoDoContextoOuAtendimento(contextoAtual, atendimentoAtual, catalogo);
            var veiculo = servico == null ? null : ObterVeiculoAtual(atendimentoAtual, servico);
            var marca = veiculo == null ? null : ResolverMarcaPecaSelecionada(ObterTexto(atendimentoAtual?.DadosExtras, ChaveMarcaPecaNome) ?? string.Empty, veiculo, atendimentoAtual);
            if (servico == null)
            {
                return await RedirecionarParaServicoAsync(idConversa, scope, cliente, contextoAtual, atendimentoAtual, viaNumeroCentral);
            }

            var propostaTroca = await TentarCriarTrocaPendenteAsync(
                idConversa, scope, cliente, catalogo, contextoAtual, atendimentoAtual, mensagemTexto, viaNumeroCentral, EstadoAguardandoConfirmacaoFinal);
            if (propostaTroca != null)
            {
                return propostaTroca;
            }

            if (EhConfirmacaoFeliz(mensagemTexto))
            {
                var replyFinal = $"{MontarResumoFinal(cliente, servico, veiculo, marca)} No proximo passo eu sigo com voce para o agendamento.";
                await PersistirEstadoDeterministicoAsync(
                    idConversa,
                    scope,
                    cliente,
                    contextoAtual,
                    atendimentoAtual,
                    servico,
                    EstadoProntoAgendamento,
                    "aguardando_cliente",
                    replyFinal,
                    viaNumeroCentral,
                    CriarSnapshotExtras(ObterDadosServico(contextoAtual, atendimentoAtual), new Dictionary<string, object?>
                    {
                        [ChavePerguntaPendente] = null
                    }),
                    intencaoDetalhe: "pronto_agendamento");

                return CriarResposta(replyFinal);
            }

            if (EhConfirmacaoNegativa(mensagemTexto))
            {
                const string replyNegativa = "Sem problema. Me fala o que voce quer ajustar: servico, veiculo ou marca.";
                await PersistirEstadoDeterministicoAsync(
                    idConversa,
                    scope,
                    cliente,
                    contextoAtual,
                    atendimentoAtual,
                    servico,
                    EstadoAguardandoConfirmacaoFinal,
                    "aguardando_cliente",
                    replyNegativa,
                    viaNumeroCentral,
                    CriarSnapshotExtras(ObterDadosServico(contextoAtual, atendimentoAtual), new Dictionary<string, object?>
                    {
                        [ChavePerguntaPendente] = PerguntaConfirmacaoFinal
                    }),
                    intencaoDetalhe: "confirmacao_final");

                return CriarResposta(replyNegativa);
            }

            var reply = "Se estiver certo, me responde com sim. Se quiser ajustar algo, me fala o que voce quer mudar.";
            await PersistirEstadoDeterministicoAsync(
                idConversa,
                scope,
                cliente,
                contextoAtual,
                atendimentoAtual,
                servico,
                EstadoAguardandoConfirmacaoFinal,
                "aguardando_cliente",
                reply,
                viaNumeroCentral,
                CriarSnapshotExtras(ObterDadosServico(contextoAtual, atendimentoAtual), new Dictionary<string, object?>
                {
                    [ChavePerguntaPendente] = PerguntaConfirmacaoFinal
                }),
                intencaoDetalhe: "confirmacao_final");

            return CriarResposta(reply);
        }

        private async Task<AssistantDecision> ProcessarProntoAgendamentoDeterministicoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            IReadOnlyList<ServicoCatalogItem> catalogo,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            string mensagemTexto,
            bool viaNumeroCentral)
        {
            var propostaTroca = await TentarCriarTrocaPendenteAsync(
                idConversa, scope, cliente, catalogo, contextoAtual, atendimentoAtual, mensagemTexto, viaNumeroCentral, EstadoProntoAgendamento);
            if (propostaTroca != null)
            {
                return propostaTroca;
            }

            const string reply = "Perfeito. No proximo passo eu sigo com voce para o agendamento.";
            var servico = ResolverServicoDoContextoOuAtendimento(contextoAtual, atendimentoAtual, catalogo);

            await PersistirEstadoDeterministicoAsync(
                idConversa,
                scope,
                cliente,
                contextoAtual,
                atendimentoAtual,
                servico,
                EstadoProntoAgendamento,
                "aguardando_cliente",
                reply,
                viaNumeroCentral,
                CriarSnapshotExtras(ObterDadosServico(contextoAtual, atendimentoAtual), new Dictionary<string, object?>()),
                intencaoDetalhe: "pronto_agendamento");

            return CriarResposta(reply);
        }

        private async Task<AssistantDecision> ProcessarConfirmacaoTrocaPendenteAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            IReadOnlyList<ServicoCatalogItem> catalogo,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            string mensagemTexto,
            bool viaNumeroCentral,
            string estadoRetorno)
        {
            var dados = ObterDadosServico(contextoAtual, atendimentoAtual);
            var campo = ObterTexto(dados, ChaveTrocaPendenteCampo);
            var valor = ObterTexto(dados, ChaveTrocaPendenteValor);
            var label = ObterTexto(dados, ChaveTrocaPendenteLabel);

            if (EhConfirmacaoPositiva(mensagemTexto) || EhConfirmacaoFeliz(mensagemTexto))
            {
                if (string.Equals(campo, "servico", StringComparison.OrdinalIgnoreCase) &&
                    Guid.TryParse(valor, out var idServico) &&
                    catalogo.FirstOrDefault(item => item.Id == idServico) is { } novoServico)
                {
                    return await BloquearServicoEContinuarAsync(
                        idConversa, scope, cliente, contextoAtual, atendimentoAtual, novoServico, viaNumeroCentral,
                        prefixo: $"Perfeito. Atualizei o servico para {novoServico.Nome}.");
                }

                var servicoAtual = ResolverServicoDoContextoOuAtendimento(contextoAtual, atendimentoAtual, catalogo);
                if (servicoAtual != null && string.Equals(campo, "veiculo", StringComparison.OrdinalIgnoreCase))
                {
                    var veiculo = servicoAtual.Veiculos.FirstOrDefault(item =>
                        item.CarroId.ToString().Equals(valor, StringComparison.OrdinalIgnoreCase) ||
                        item.NomeExibicao.Equals(label, StringComparison.OrdinalIgnoreCase));
                    if (veiculo != null)
                    {
                        return await AtivarVeiculoEOferecerDetalhesAsync(
                            idConversa, scope, cliente, contextoAtual, atendimentoAtual, servicoAtual, veiculo, viaNumeroCentral,
                            prefixo: $"Perfeito. Atualizei o veiculo para {veiculo.NomeExibicao}.");
                    }
                }

                if (servicoAtual != null && string.Equals(campo, "marca", StringComparison.OrdinalIgnoreCase))
                {
                    var veiculoAtual = ObterVeiculoAtual(atendimentoAtual, servicoAtual);
                    if (veiculoAtual != null)
                    {
                        var marca = veiculoAtual.MarcasPeca.FirstOrDefault(item =>
                            item.Id.Equals(valor, StringComparison.OrdinalIgnoreCase) ||
                            item.Nome.Equals(label, StringComparison.OrdinalIgnoreCase));
                        if (marca != null)
                        {
                            return await IrParaConfirmacaoFinalAsync(
                                idConversa, scope, cliente, contextoAtual, atendimentoAtual, servicoAtual, veiculoAtual, marca, viaNumeroCentral,
                                prefixo: $"Perfeito. Atualizei a marca para {marca.Nome}.");
                        }
                    }
                }
            }

            if (EhConfirmacaoNegativa(mensagemTexto))
            {
                var extrasNegativa = CriarSnapshotExtras(dados, new Dictionary<string, object?>
                {
                    [ChaveTrocaPendenteCampo] = null,
                    [ChaveTrocaPendenteValor] = null,
                    [ChaveTrocaPendenteLabel] = null,
                    [ChavePerguntaPendente] = ResolverPerguntaPorEstado(estadoRetorno)
                });

                var servicoAtual = ResolverServicoDoContextoOuAtendimento(contextoAtual, atendimentoAtual, catalogo);
                var replyNegativa = MontarPromptEstadoAtual(estadoRetorno, cliente, atendimentoAtual, servicoAtual);
                await PersistirEstadoDeterministicoAsync(
                    idConversa, scope, cliente, contextoAtual, atendimentoAtual, servicoAtual, estadoRetorno,
                    "aguardando_cliente", replyNegativa, viaNumeroCentral, extrasNegativa, "ajuste_mantido");

                return CriarResposta(replyNegativa);
            }

            var atual = ObterValorAtualTroca(campo, atendimentoAtual);
            var reply = $"Hoje esta anotado {atual}. Quer trocar para {label}? Me responde com sim ou nao.";
            await PersistirEstadoDeterministicoAsync(
                idConversa,
                scope,
                cliente,
                contextoAtual,
                atendimentoAtual,
                ResolverServicoDoContextoOuAtendimento(contextoAtual, atendimentoAtual, catalogo),
                estadoRetorno,
                "aguardando_cliente",
                reply,
                viaNumeroCentral,
                CriarSnapshotExtras(dados, new Dictionary<string, object?>
                {
                    [ChavePerguntaPendente] = PerguntaConfirmacaoTroca
                }),
                intencaoDetalhe: "confirmacao_troca");

            return CriarResposta(reply);
        }

        private async Task<AssistantDecision?> TentarCriarTrocaPendenteAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            IReadOnlyList<ServicoCatalogItem> catalogo,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            string mensagemTexto,
            bool viaNumeroCentral,
            string estadoAtual)
        {
            if (MensagemEhControleDeFluxo(mensagemTexto))
            {
                return null;
            }

            var servicoAtual = ResolverServicoDoContextoOuAtendimento(contextoAtual, atendimentoAtual, catalogo);
            if (servicoAtual == null)
            {
                return null;
            }

            var veiculoAtual = ObterVeiculoAtual(atendimentoAtual, servicoAtual);
            var marcaAtual = veiculoAtual == null
                ? null
                : ResolverMarcaPecaSelecionada(ObterTexto(atendimentoAtual?.DadosExtras, ChaveMarcaPecaNome) ?? string.Empty, veiculoAtual, atendimentoAtual);

            if (marcaAtual != null)
            {
                var novaMarca = ResolverMarcaPecaSelecionada(mensagemTexto, veiculoAtual!, null);
                if (novaMarca != null && !string.Equals(novaMarca.Nome, marcaAtual.Nome, StringComparison.OrdinalIgnoreCase))
                {
                    return await CriarConfirmacaoTrocaAsync(
                        idConversa, scope, cliente, contextoAtual, atendimentoAtual, servicoAtual, estadoAtual, viaNumeroCentral,
                        "marca", novaMarca.Id, novaMarca.Nome, marcaAtual.Nome);
                }
            }

            if (veiculoAtual != null)
            {
                var novoVeiculo = await ResolverVeiculoSelecionadoAsync(scope.IdEstabelecimento, mensagemTexto, servicoAtual, contextoAtual);
                if (novoVeiculo != null &&
                    !string.Equals(novoVeiculo.NomeExibicao, veiculoAtual.NomeExibicao, StringComparison.OrdinalIgnoreCase) &&
                    novoVeiculo.Compativel)
                {
                    return await CriarConfirmacaoTrocaAsync(
                        idConversa, scope, cliente, contextoAtual, atendimentoAtual, servicoAtual, estadoAtual, viaNumeroCentral,
                        "veiculo", novoVeiculo.CarroId.ToString(), novoVeiculo.NomeExibicao, veiculoAtual.NomeExibicao);
                }
            }

            var matches = SelecionarMelhoresCandidatos(MatchServices(mensagemTexto, catalogo));
            if (matches.Count == 1 && matches[0].Servico.Id != servicoAtual.Id)
            {
                return await CriarConfirmacaoTrocaAsync(
                    idConversa, scope, cliente, contextoAtual, atendimentoAtual, servicoAtual, estadoAtual, viaNumeroCentral,
                    "servico", matches[0].Servico.Id.ToString(), matches[0].Servico.Nome, servicoAtual.Nome);
            }

            return null;
        }

        private async Task<AssistantDecision> CriarConfirmacaoTrocaAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            ServicoCatalogItem servicoAtual,
            string estadoAtual,
            bool viaNumeroCentral,
            string campo,
            string valor,
            string label,
            string atual)
        {
            var extras = CriarSnapshotExtras(ObterDadosServico(contextoAtual, atendimentoAtual), new Dictionary<string, object?>
            {
                [ChaveTrocaPendenteCampo] = campo,
                [ChaveTrocaPendenteValor] = valor,
                [ChaveTrocaPendenteLabel] = label,
                [ChavePerguntaPendente] = PerguntaConfirmacaoTroca
            });

            var reply = $"Hoje esta anotado {atual}. Quer trocar para {label}? Me responde com sim ou nao.";
            await PersistirEstadoDeterministicoAsync(
                idConversa, scope, cliente, contextoAtual, atendimentoAtual, servicoAtual, estadoAtual,
                "aguardando_cliente", reply, viaNumeroCentral, extras, "confirmacao_troca");

            return CriarResposta(reply);
        }

        private async Task<ServicoAtendimento?> PersistirEstadoDeterministicoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            ConversationContext? contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            ServicoCatalogItem? servico,
            string estado,
            string status,
            string? reply,
            bool viaNumeroCentral,
            IReadOnlyDictionary<string, object?> extras,
            string intencaoDetalhe)
        {
            var atendimento = await ObterOuCriarAtendimentoAsync(
                idConversa,
                scope,
                cliente,
                atendimentoAtual,
                "catalogo_servicos",
                intencaoDetalhe,
                servico,
                status,
                estado,
                reply,
                viaNumeroCentral,
                CriarExtras(extras));

            await SalvarContextoServicoAsync(idConversa, contextoAtual, estado, atendimento?.Id, extras);
            return atendimento;
        }

        private async Task<AssistantDecision> BloquearServicoEContinuarAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            ServicoCatalogItem servico,
            bool viaNumeroCentral,
            string? prefixo = null)
        {
            var dados = ObterDadosServico(contextoAtual, atendimentoAtual);
            var extras = CriarSnapshotExtras(dados, new Dictionary<string, object?>
            {
                [ChaveServicoId] = servico.Id.ToString(),
                [ChaveServicoNome] = servico.Nome,
                [ChaveCandidatosIds] = null,
                [ChaveTentativas] = 0,
                [ChavePerguntaPendente] = PerguntaVeiculo,
                [ChaveVehicleId] = null,
                [ChaveVehicleNome] = null,
                [ChaveMarcaPecaId] = null,
                [ChaveMarcaPecaNome] = null,
                [ChaveItensDisponiveis] = null,
                [ChaveItensEntregues] = null,
                [ChaveVehicleOptions] = null,
                [ChaveVehiclePromptReason] = null,
                [ChaveTrocaPendenteCampo] = null,
                [ChaveTrocaPendenteValor] = null,
                [ChaveTrocaPendenteLabel] = null
            });

            var reply = string.Join(" ",
                new[]
                {
                    prefixo,
                    $"Perfeito{TratarNomeCurto(cliente)}. Entendi que voce quer {servico.Nome}.",
                    MontarPerguntaVeiculo(servico)
                }.Where(item => !string.IsNullOrWhiteSpace(item)));

            await PersistirEstadoDeterministicoAsync(
                idConversa, scope, cliente, contextoAtual, atendimentoAtual, servico,
                EstadoAguardandoVeiculo, "aguardando_cliente", reply, viaNumeroCentral, extras, "coleta_veiculo");

            return CriarResposta(reply);
        }

        private async Task<AssistantDecision> AtivarVeiculoEOferecerDetalhesAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            ServicoCatalogItem servico,
            ServicoCatalogVehicleItem veiculo,
            bool viaNumeroCentral,
            string? prefixo = null)
        {
            var dados = ObterDadosServico(contextoAtual, atendimentoAtual);
            var itensDisponiveis = ObterItensDisponiveis(servico, veiculo);
            if (itensDisponiveis.Length == 0)
            {
                return await IrParaConfirmacaoFinalAsync(
                    idConversa, scope, cliente, contextoAtual, atendimentoAtual, servico, veiculo, null, viaNumeroCentral, prefixo);
            }

            var extras = CriarSnapshotExtras(dados, new Dictionary<string, object?>
            {
                [ChaveVehicleId] = veiculo.CarroId.ToString(),
                [ChaveVehicleNome] = veiculo.NomeExibicao,
                [ChaveMarcaPecaId] = null,
                [ChaveMarcaPecaNome] = null,
                [ChaveItensDisponiveis] = itensDisponiveis,
                [ChaveItensEntregues] = Array.Empty<string>(),
                [ChavePerguntaPendente] = PerguntaDetalhes,
                [ChaveTentativas] = 0,
                [ChaveVehicleOptions] = null,
                [ChaveVehiclePromptReason] = null,
                [ChaveTrocaPendenteCampo] = null,
                [ChaveTrocaPendenteValor] = null,
                [ChaveTrocaPendenteLabel] = null
            });

            var reply = string.Join(" ",
                new[]
                {
                    prefixo,
                    $"Perfeito. Para {veiculo.NomeExibicao}, fazemos {servico.Nome}.",
                    $"Se quiser, eu posso te passar {FormatarListaCurta(NomesItensDetalhe(itensDisponiveis))}."
                }.Where(item => !string.IsNullOrWhiteSpace(item)));

            await PersistirEstadoDeterministicoAsync(
                idConversa, scope, cliente, contextoAtual, atendimentoAtual, servico,
                EstadoOfertaDetalhes, "aguardando_cliente", reply, viaNumeroCentral, extras, "oferta_detalhes");

            return CriarResposta(reply);
        }

        private async Task<AssistantDecision> EntregarDetalhesPendentesAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            ServicoCatalogItem servico,
            ServicoCatalogVehicleItem veiculo,
            bool viaNumeroCentral)
        {
            var dados = ObterDadosServico(contextoAtual, atendimentoAtual);
            var disponiveis = ObterListaTexto(dados, ChaveItensDisponiveis);
            var entregues = ObterListaTexto(dados, ChaveItensEntregues);
            var pendentes = disponiveis.Except(entregues, StringComparer.OrdinalIgnoreCase).ToArray();
            if (pendentes.Length == 0)
            {
                return await IrParaConfirmacaoFinalAsync(
                    idConversa, scope, cliente, contextoAtual, atendimentoAtual, servico, veiculo, null, viaNumeroCentral);
            }

            var blocos = new List<string>();
            foreach (var item in pendentes)
            {
                var texto = MontarTextoDetalhe(item, servico, veiculo, marca: null);
                if (!string.IsNullOrWhiteSpace(texto))
                {
                    blocos.Add(texto);
                }
            }

            var novosEntregues = entregues.Concat(pendentes).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var precisaEscolherMarca = pendentes.Contains("marcas", StringComparer.OrdinalIgnoreCase) && veiculo.MarcasPeca.Count > 0;
            var reply = string.Join("\n\n", blocos);
            if (precisaEscolherMarca)
            {
                reply = $"{reply}\n\nAgora eu so preciso que voce escolha a marca. Me responde com o numero ou com o nome.";
            }

            var extras = CriarSnapshotExtras(dados, new Dictionary<string, object?>
            {
                [ChaveItensEntregues] = novosEntregues,
                [ChavePerguntaPendente] = precisaEscolherMarca ? PerguntaMarca : PerguntaConfirmacaoFinal
            });

            var estadoDestino = precisaEscolherMarca ? EstadoAguardandoMarca : EstadoAguardandoConfirmacaoFinal;
            await PersistirEstadoDeterministicoAsync(
                idConversa, scope, cliente, contextoAtual, atendimentoAtual, servico, estadoDestino,
                "aguardando_cliente", reply, viaNumeroCentral, extras, precisaEscolherMarca ? "escolha_marca" : "confirmacao_final");

            return CriarResposta(reply);
        }

        private async Task<AssistantDecision> EntregarDetalheIndividualAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            ServicoCatalogItem servico,
            ServicoCatalogVehicleItem veiculo,
            bool viaNumeroCentral,
            string item,
            bool manterEstadoMarca = false)
        {
            var dados = ObterDadosServico(contextoAtual, atendimentoAtual);
            var entregues = ObterListaTexto(dados, ChaveItensEntregues);
            var replyBase = MontarTextoDetalhe(item, servico, veiculo, ResolverMarcaPecaSelecionada(ObterTexto(atendimentoAtual?.DadosExtras, ChaveMarcaPecaNome) ?? string.Empty, veiculo, atendimentoAtual));
            if (string.IsNullOrWhiteSpace(replyBase))
            {
                return await PerguntarConfirmacaoHumanoAsync(
                    idConversa, scope, cliente, atendimentoAtual, contextoAtual, servico,
                    "Eu nao tenho esse detalhe fechado no catalogo.", item, viaNumeroCentral);
            }

            var novosEntregues = entregues.Append(item).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var estadoDestino = manterEstadoMarca ? EstadoAguardandoMarca : EstadoOfertaDetalhes;
            var extras = CriarSnapshotExtras(dados, new Dictionary<string, object?>
            {
                [ChaveItensEntregues] = novosEntregues,
                [ChavePerguntaPendente] = manterEstadoMarca ? PerguntaMarca : PerguntaDetalhes
            });

            var restantes = ObterListaTexto(dados, ChaveItensDisponiveis)
                .Except(novosEntregues, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var reply = restantes.Length > 0 && !manterEstadoMarca
                ? $"{replyBase} Se quiser, eu tambem posso te passar {FormatarListaCurta(NomesItensDetalhe(restantes))}."
                : replyBase;

            await PersistirEstadoDeterministicoAsync(
                idConversa, scope, cliente, contextoAtual, atendimentoAtual, servico, estadoDestino,
                "aguardando_cliente", reply, viaNumeroCentral, extras, manterEstadoMarca ? "escolha_marca" : "oferta_detalhes");

            return CriarResposta(reply);
        }

        private async Task<AssistantDecision> EntregarMarcasAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            ServicoCatalogItem servico,
            ServicoCatalogVehicleItem veiculo,
            bool viaNumeroCentral)
        {
            var reply = BuildBrandsReply(servico, veiculo, null);
            if (veiculo.MarcasPeca.Count > 0)
            {
                reply = $"{reply}\nAgora eu so preciso que voce escolha a marca. Me responde com o numero ou com o nome.";
            }

            var dados = ObterDadosServico(contextoAtual, atendimentoAtual);
            var entregues = ObterListaTexto(dados, ChaveItensEntregues).Append("marcas").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var estado = veiculo.MarcasPeca.Count > 0 ? EstadoAguardandoMarca : EstadoOfertaDetalhes;
            var pergunta = veiculo.MarcasPeca.Count > 0 ? PerguntaMarca : PerguntaDetalhes;

            await PersistirEstadoDeterministicoAsync(
                idConversa, scope, cliente, contextoAtual, atendimentoAtual, servico, estado,
                "aguardando_cliente", reply, viaNumeroCentral,
                CriarSnapshotExtras(dados, new Dictionary<string, object?>
                {
                    [ChaveItensEntregues] = entregues,
                    [ChavePerguntaPendente] = pergunta
                }),
                veiculo.MarcasPeca.Count > 0 ? "escolha_marca" : "oferta_detalhes");

            return CriarResposta(reply);
        }

        private async Task<AssistantDecision> IrParaConfirmacaoFinalAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            ServicoCatalogItem servico,
            ServicoCatalogVehicleItem? veiculo,
            ServicoCatalogPieceItem? marca,
            bool viaNumeroCentral,
            string? prefixo = null)
        {
            var dados = ObterDadosServico(contextoAtual, atendimentoAtual);
            var extras = CriarSnapshotExtras(dados, new Dictionary<string, object?>
            {
                [ChaveMarcaPecaId] = marca?.Id,
                [ChaveMarcaPecaNome] = marca?.Nome,
                [ChavePerguntaPendente] = PerguntaConfirmacaoFinal,
                [ChaveVehicleOptions] = null,
                [ChaveVehiclePromptReason] = null,
                [ChaveTrocaPendenteCampo] = null,
                [ChaveTrocaPendenteValor] = null,
                [ChaveTrocaPendenteLabel] = null
            });

            var resumo = MontarResumoFinal(cliente, servico, veiculo, marca);
            var reply = string.Join(" ", new[] { prefixo, $"{resumo} Se estiver certo, me responde com sim que eu sigo para o agendamento." }.Where(item => !string.IsNullOrWhiteSpace(item)));
            await PersistirEstadoDeterministicoAsync(
                idConversa, scope, cliente, contextoAtual, atendimentoAtual, servico,
                EstadoAguardandoConfirmacaoFinal, "aguardando_cliente", reply, viaNumeroCentral, extras, "confirmacao_final");

            return CriarResposta(reply);
        }

        private async Task<AssistantDecision> RedirecionarParaServicoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            bool viaNumeroCentral)
        {
            const string reply = "Me fala qual servico voce quer ver.";
            await PersistirEstadoDeterministicoAsync(
                idConversa, scope, cliente, contextoAtual, atendimentoAtual, null,
                EstadoAguardandoServico, "aguardando_cliente", reply, viaNumeroCentral,
                CriarSnapshotExtras(ObterDadosServico(contextoAtual, atendimentoAtual), new Dictionary<string, object?>
                {
                    [ChavePerguntaPendente] = PerguntaServico
                }),
                "escolha_servico");

            return CriarResposta(reply);
        }

        private async Task<AssistantDecision> VoltarParaVeiculoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            ServicoCatalogItem servico,
            bool viaNumeroCentral)
        {
            var reply = MontarPerguntaVeiculo(servico);
            await PersistirEstadoDeterministicoAsync(
                idConversa, scope, cliente, contextoAtual, atendimentoAtual, servico,
                EstadoAguardandoVeiculo, "aguardando_cliente", reply, viaNumeroCentral,
                CriarSnapshotExtras(ObterDadosServico(contextoAtual, atendimentoAtual), new Dictionary<string, object?>
                {
                    [ChavePerguntaPendente] = PerguntaVeiculo
                }),
                "coleta_veiculo");

            return CriarResposta(reply);
        }

        private async Task<string> ObterNomeEstabelecimentoServicoAsync(Guid idEstabelecimento)
        {
            var nome = await _estabelecimentoRepository.ObterNomeFantasiaAsync(idEstabelecimento);
            return string.IsNullOrWhiteSpace(nome) ? "Citrocar" : nome.Trim();
        }

        private static Dictionary<string, object?> ObterDadosServico(ConversationContext? contextoAtual, ServicoAtendimento? atendimentoAtual)
        {
            var dados = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (atendimentoAtual?.DadosExtras != null)
            {
                foreach (var item in atendimentoAtual.DadosExtras)
                {
                    dados[item.Key] = item.Value;
                }
            }

            if (contextoAtual?.DadosColetados != null)
            {
                foreach (var item in contextoAtual.DadosColetados)
                {
                    dados[item.Key] = item.Value;
                }
            }

            return dados;
        }

        private static Dictionary<string, object?> CriarSnapshotExtras(
            IReadOnlyDictionary<string, object?> dadosAtuais,
            IReadOnlyDictionary<string, object?> alteracoes)
        {
            var snapshot = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in dadosAtuais)
            {
                snapshot[item.Key] = item.Value;
            }

            foreach (var alteracao in alteracoes)
            {
                snapshot[alteracao.Key] = alteracao.Value;
            }

            return snapshot;
        }

        private static Cliente CriarClienteContextual(EffectiveConversationScope scope, Cliente? cliente, string? nome)
        {
            return new Cliente
            {
                Id = scope.IdCliente,
                IdEstabelecimento = scope.IdEstabelecimento,
                Nome = nome,
                TelefoneE164 = scope.TelefoneCliente ?? cliente?.TelefoneE164
            };
        }

        private static IReadOnlyList<ServicoCatalogItem> ResolverCandidatosDoContexto(IReadOnlyDictionary<string, object?> dados, IReadOnlyList<ServicoCatalogItem> catalogo)
        {
            return ObterListaTexto(dados, ChaveCandidatosIds)
                .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Select(id => catalogo.FirstOrDefault(item => item.Id == id))
                .Where(item => item != null)
                .Cast<ServicoCatalogItem>()
                .ToArray();
        }

        private static string[] ObterItensDisponiveis(ServicoCatalogItem servico, ServicoCatalogVehicleItem veiculo)
        {
            var itens = new List<string>();
            if (!string.IsNullOrWhiteSpace(BuildPriceText(servico, veiculo)))
            {
                itens.Add("valor");
            }

            if (servico.DuracaoMinutos > 0)
            {
                itens.Add("tempo");
            }

            if (veiculo.MarcasPeca.Count > 0)
            {
                itens.Add("marcas");
            }

            return itens.ToArray();
        }

        private static string[] NomesItensDetalhe(IReadOnlyCollection<string> itens)
        {
            return itens.Select(item => item switch
            {
                "valor" => "valor",
                "tempo" => "tempo",
                "marcas" => "marcas disponiveis",
                _ => item
            }).ToArray();
        }

        private static string MontarTextoDetalhe(string item, ServicoCatalogItem servico, ServicoCatalogVehicleItem veiculo, ServicoCatalogPieceItem? marca)
        {
            return item switch
            {
                "valor" when marca != null && !string.IsNullOrWhiteSpace(BuildPiecePriceText(servico, veiculo, marca)) => BuildPiecePriceText(servico, veiculo, marca)!,
                "valor" => BuildPriceText(servico, veiculo) ?? string.Empty,
                "tempo" => $"O tempo estimado desse servico fica em {FormatDuration(servico.DuracaoMinutos)}.",
                "marcas" => BuildBrandsReply(servico, veiculo, null),
                _ => string.Empty
            };
        }

        private static string MontarPerguntaVeiculo(ServicoCatalogItem servico)
        {
            var exemplos = servico.Veiculos
                .Select(item => item.NomeExibicao)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToArray();

            return exemplos.Length == 0
                ? $"Agora me fala a marca e o modelo do veiculo para eu conferir {servico.Nome}."
                : $"Agora me fala a marca e o modelo do veiculo para eu conferir {servico.Nome}. Exemplo: {FormatarListaCurta(exemplos)}.";
        }

        private static string MontarResumoFinal(Cliente? cliente, ServicoCatalogItem servico, ServicoCatalogVehicleItem? veiculo, ServicoCatalogPieceItem? marca)
        {
            var primeiroNome = ObterPrimeiroNome(cliente?.Nome);
            var cabecalho = string.IsNullOrWhiteSpace(primeiroNome)
                ? "Ficou assim"
                : $"Ficou assim, {primeiroNome}";

            var partes = new List<string>
            {
                servico.Nome
            };

            if (veiculo != null && !string.IsNullOrWhiteSpace(veiculo.NomeExibicao))
            {
                partes.Add($"para {veiculo.NomeExibicao}");
            }

            if (marca != null && !string.IsNullOrWhiteSpace(marca.Nome))
            {
                partes.Add($"com a marca {marca.Nome}");
            }

            var resumo = $"{cabecalho}: {string.Join(", ", partes)}";
            var textoPreco = marca != null && veiculo != null
                ? BuildPiecePriceText(servico, veiculo, marca)
                : veiculo != null
                    ? BuildPriceText(servico, veiculo)
                    : BuildPriceText(servico, null);

            return string.IsNullOrWhiteSpace(textoPreco)
                ? $"{resumo}."
                : $"{resumo}. {textoPreco}";
        }

        private static string MontarPromptDetalhesRestantes(
            ConversationContext contextoAtual,
            ServicoAtendimento? atendimentoAtual,
            ServicoCatalogItem servico,
            ServicoCatalogVehicleItem veiculo)
        {
            var dados = ObterDadosServico(contextoAtual, atendimentoAtual);
            var disponiveis = ObterListaTexto(dados, ChaveItensDisponiveis);
            var entregues = ObterListaTexto(dados, ChaveItensEntregues);
            var restantes = disponiveis.Except(entregues, StringComparer.OrdinalIgnoreCase).ToArray();

            return restantes.Length == 0
                ? $"Perfeito. Para {veiculo.NomeExibicao}, fazemos {servico.Nome}."
                : $"Para {veiculo.NomeExibicao}, eu ainda posso te passar {FormatarListaCurta(NomesItensDetalhe(restantes))}.";
        }

        private static string MontarPromptEstadoAtual(string estado, Cliente? cliente, ServicoAtendimento? atendimentoAtual, ServicoCatalogItem? servico)
        {
            return estado switch
            {
                EstadoAguardandoServico => "Me fala qual servico voce quer ver.",
                EstadoAguardandoVeiculo when servico != null => $"{BuildResumoDoQueJaSei(cliente, atendimentoAtual, servico, null, null, mencionarPendenciaVeiculo: true)} {MontarPerguntaVeiculo(servico)}".Trim(),
                EstadoOfertaDetalhes when servico != null => $"Perfeito. Se quiser, eu posso te passar valor, tempo ou marcas sobre {servico.Nome}.",
                EstadoAguardandoMarca => "Agora eu so preciso que voce escolha a marca. Me responde com o numero ou com o nome.",
                EstadoAguardandoConfirmacaoFinal => "Se estiver certo, me responde com sim. Se quiser ajustar algo, me fala o que voce quer mudar.",
                EstadoProntoAgendamento => "Perfeito. No proximo passo eu sigo com voce para o agendamento.",
                _ => "Me fala o que voce quer ajustar."
            };
        }

        private static bool TryExtrairNomeValidoServico(string? mensagemTexto, out string nome)
        {
            nome = string.Empty;
            if (string.IsNullOrWhiteSpace(mensagemTexto))
            {
                return false;
            }

            var texto = EspacosRegex.Replace(mensagemTexto.Trim(), " ");
            texto = PrefixoNomeRegex.Replace(texto, string.Empty).Trim();
            texto = texto.Trim(' ', '.', ',', ';', ':', '!', '?', '"', '\'', '-', '_');

            if (texto.Length < 2 || texto.Length > 60 || texto.Any(char.IsDigit) || !texto.Any(char.IsLetter))
            {
                return false;
            }

            var partes = texto.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (partes.Length == 0 || partes.Length > 4)
            {
                return false;
            }

            var culture = CultureInfo.GetCultureInfo("pt-BR");
            nome = culture.TextInfo.ToTitleCase(texto.ToLower(culture));
            return true;
        }

        private static bool TemTrocaPendente(ConversationContext? contextoAtual, ServicoAtendimento? atendimentoAtual)
        {
            var dados = ObterDadosServico(contextoAtual, atendimentoAtual);
            return !string.IsNullOrWhiteSpace(ObterTexto(dados, ChaveTrocaPendenteCampo));
        }

        private static bool EhContinuacaoGenerica(string texto)
        {
            var normalizado = NormalizeText(texto);
            return normalizado is "sim" or "ok" or "pode" or "manda" or "pode me passar" or "me passa" or "pode passar" or "isso" or "fechou" or "confirmo" or "pode ser";
        }

        private static bool EhConfirmacaoFeliz(string texto)
        {
            var normalizado = NormalizeText(texto);
            return normalizado is "sim" or "isso" or "confirmo" or "fechou" or "pode ser";
        }

        private static bool MensagemEhControleDeFluxo(string texto)
        {
            return UsuarioPerguntouPreco(texto) ||
                   UsuarioPerguntouDuracao(texto) ||
                   UsuarioPerguntouMarcasPeca(texto) ||
                   UsuarioPerguntouDetalhesServico(texto) ||
                   EhContinuacaoGenerica(texto) ||
                   EhConfirmacaoPositiva(texto) ||
                   EhConfirmacaoNegativa(texto) ||
                   FoiPedidoHumano(texto) ||
                   EhMensagemEncerramento(texto);
        }

        private static string TratarNomeCurto(Cliente? cliente)
        {
            var primeiroNome = ObterPrimeiroNome(cliente?.Nome);
            return string.IsNullOrWhiteSpace(primeiroNome) ? string.Empty : $", {primeiroNome}";
        }

        private static string ResolverPerguntaPorEstado(string estado)
        {
            return estado switch
            {
                EstadoAguardandoNome => PerguntaNome,
                EstadoAguardandoServico => PerguntaServico,
                EstadoAguardandoVeiculo => PerguntaVeiculo,
                EstadoOfertaDetalhes => PerguntaDetalhes,
                EstadoAguardandoMarca => PerguntaMarca,
                EstadoAguardandoConfirmacaoFinal => PerguntaConfirmacaoFinal,
                _ => PerguntaServico
            };
        }

        private static string ObterValorAtualTroca(string? campo, ServicoAtendimento? atendimentoAtual)
        {
            return campo?.ToLowerInvariant() switch
            {
                "servico" => atendimentoAtual?.NomeServico ?? "o servico atual",
                "veiculo" => ObterTexto(atendimentoAtual?.DadosExtras, ChaveVehicleNome) ?? "o veiculo atual",
                "marca" => ObterTexto(atendimentoAtual?.DadosExtras, ChaveMarcaPecaNome) ?? "a marca atual",
                _ => "o item atual"
            };
        }

        private static AssistantDecision CriarResposta(string reply)
            => new(reply, "none", null, false, null);
    }
}
