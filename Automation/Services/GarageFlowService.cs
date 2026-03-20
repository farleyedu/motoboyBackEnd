using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class GarageFlowService
    {
        public const string EstadoQuestionario = "garagem_questionario";
        public const string EstadoConcluido = "garagem_lead_concluido";

        private const string ChaveLeadId = "garagem_lead_id";
        private const string ChaveEstabelecimentoId = "garagem_estabelecimento_id";
        private const string ChaveEtapa = "garagem_etapa";
        private const string ChaveViaNumeroCentral = "garagem_via_numero_central";
        private const string ChaveNomeCliente = "garagem_nome_cliente";
        private const string ChaveObjetivo = "garagem_objetivo";
        private const string ChaveModeloInteresse = "garagem_modelo_interesse";
        private const string ChaveFaixaInvestimento = "garagem_faixa_investimento";
        private const string ChaveFormaPagamento = "garagem_forma_pagamento";
        private const string ChaveValorEntrada = "garagem_valor_entrada_texto";
        private const string ChaveUrgencia = "garagem_urgencia";
        private const string ChaveTrocaModeloAtual = "garagem_troca_modelo_atual";
        private const string ChaveTrocaAnoModelo = "garagem_troca_ano_modelo";
        private const string ChaveTrocaKm = "garagem_troca_km";
        private const string ChaveTrocaQuitado = "garagem_troca_quitado";
        private const string ChaveTrocaModeloDesejado = "garagem_troca_modelo_desejado";
        private const string ChaveTrocaCondicao = "garagem_troca_condicao";
        private const string ChaveVendaModelo = "garagem_venda_modelo";
        private const string ChaveVendaAno = "garagem_venda_ano";
        private const string ChaveVendaKm = "garagem_venda_km";
        private const string ChaveVendaQuitado = "garagem_venda_quitado";
        private const string ChaveCpf = "garagem_cpf";

        private const string EtapaNome = "nome";
        private const string EtapaObjetivo = "objetivo";
        private const string EtapaModelo = "modelo";
        private const string EtapaFaixaInvestimento = "faixa_investimento";
        private const string EtapaFormaPagamento = "forma_pagamento";
        private const string EtapaValorEntrada = "valor_entrada";
        private const string EtapaTrocaModeloAtual = "troca_modelo_atual";
        private const string EtapaTrocaAnoModelo = "troca_ano_modelo";
        private const string EtapaTrocaKm = "troca_km";
        private const string EtapaTrocaQuitado = "troca_quitado";
        private const string EtapaTrocaModeloDesejado = "troca_modelo_desejado";
        private const string EtapaTrocaCondicao = "troca_condicao";
        private const string EtapaVendaModelo = "venda_modelo";
        private const string EtapaVendaAno = "venda_ano";
        private const string EtapaVendaKm = "venda_km";
        private const string EtapaVendaQuitado = "venda_quitado";
        private const string EtapaUrgencia = "urgencia";
        private const string EtapaCpf = "cpf";
        private const string EtapaCatalogoLista = "catalogo_lista";

        private const string ObjetivoComprar = "comprar";
        private const string ObjetivoVender = "vender";
        private const string ObjetivoTrocar = "trocar";
        private const string DefaultGaragePublicBaseUrl = "https://zippy-admin-one.vercel.app";

        private readonly IConversationRepository _conversationRepository;
        private readonly IClienteRepository _clienteRepository;
        private readonly IEstabelecimentoRepository _estabelecimentoRepository;
        private readonly IGaragemLeadRepository _garagemLeadRepository;
        private readonly IGaragemVeiculoRepository _garagemVeiculoRepository;
        private readonly CentralRoutingService _centralRouting;
        private readonly ILogger<GarageFlowService> _logger;
        private readonly string? _garagePublicBaseUrl;

        public GarageFlowService(
            IConversationRepository conversationRepository,
            IClienteRepository clienteRepository,
            IEstabelecimentoRepository estabelecimentoRepository,
            IGaragemLeadRepository garagemLeadRepository,
            IGaragemVeiculoRepository garagemVeiculoRepository,
            CentralRoutingService centralRouting,
            IConfiguration configuration,
            ILogger<GarageFlowService> logger)
        {
            _conversationRepository = conversationRepository;
            _clienteRepository = clienteRepository;
            _estabelecimentoRepository = estabelecimentoRepository;
            _garagemLeadRepository = garagemLeadRepository;
            _garagemVeiculoRepository = garagemVeiculoRepository;
            _centralRouting = centralRouting;
            _garagePublicBaseUrl = NormalizeBaseUrl(
                configuration["Automation:GaragePublicBaseUrl"]
                ?? configuration["Automation:PublicWebBaseUrl"]
                ?? DefaultGaragePublicBaseUrl);
            _logger = logger;
        }

        public async Task<AssistantDecision?> TryStartAfterCentralSelectionAsync(Guid idConversa)
        {
            var scope = await _centralRouting.ResolveEffectiveScopeAsync(idConversa);
            if (scope == null || !await IsGarageEstabelecimentoAsync(scope.IdEstabelecimento))
            {
                return null;
            }

            var telefoneCliente = await GarantirTelefoneClienteAsync(scope);
            if (string.IsNullOrWhiteSpace(telefoneCliente))
            {
                _logger.LogWarning("[Conversa={Conversa}] Fluxo garagem sem telefone do cliente", idConversa);
                return null;
            }

            var contextoAtual = await _conversationRepository.ObterContextoAsync(idConversa);
            var lead = await ObterOuCriarLeadAsync(
                idConversa,
                scope.IdEstabelecimento,
                scope.IdCliente,
                telefoneCliente,
                viaNumeroCentral: true);

            var etapa = DeterminarEtapaAtual(lead);
            var nomeEstabelecimento = await ObterNomeEstabelecimentoAsync(scope.IdEstabelecimento);

            if (string.Equals(etapa, EtapaNome, StringComparison.Ordinal))
            {
                await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, etapa, true);
                return CriarBoasVindas(nomeEstabelecimento);
            }

            await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, etapa, true);
            return CriarPergunta(etapa, lead, nomeEstabelecimento, incluirErro: false);
        }

        public async Task<(bool Intercepted, AssistantDecision? Decision)> TryHandleAsync(
            Guid idConversa,
            string mensagemTexto,
            string? phoneNumberDisplay)
        {
            var scope = await _centralRouting.ResolveEffectiveScopeAsync(idConversa);
            if (scope == null || !await IsGarageEstabelecimentoAsync(scope.IdEstabelecimento))
            {
                return (false, null);
            }

            var contextoAtual = await _conversationRepository.ObterContextoAsync(idConversa);
            if (string.Equals(contextoAtual?.Estado, EstadoConcluido, StringComparison.OrdinalIgnoreCase))
            {
                return (true, CriarMensagemPosConclusao());
            }

            var telefoneCliente = await GarantirTelefoneClienteAsync(scope);
            if (string.IsNullOrWhiteSpace(telefoneCliente))
            {
                _logger.LogWarning("[Conversa={Conversa}] Fluxo garagem sem telefone do cliente", idConversa);
                return (false, null);
            }

            var viaNumeroCentral = _centralRouting.IsCentralDisplayPhone(phoneNumberDisplay);
            var lead = await _garagemLeadRepository.ObterLeadAbertoAsync(scope.IdEstabelecimento, telefoneCliente);
            if (lead == null)
            {
                lead = await ObterOuCriarLeadAsync(
                    idConversa,
                    scope.IdEstabelecimento,
                    scope.IdCliente,
                    telefoneCliente,
                    viaNumeroCentral);

                var nomeEstabelecimento = await ObterNomeEstabelecimentoAsync(scope.IdEstabelecimento);
                await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaNome, viaNumeroCentral);
                return (true, CriarBoasVindas(nomeEstabelecimento));
            }

            var etapaContexto = ObterEtapaDoContexto(contextoAtual);
            var etapaAtual = etapaContexto ?? DeterminarEtapaAtual(lead);
            var conversaGrupoId = CentralRoutingService.GetGuidValue(
                contextoAtual?.DadosColetados,
                CentralRoutingService.ChaveConversaGrupoId);
            var ehSegmentoCentral = viaNumeroCentral &&
                                    conversaGrupoId.HasValue &&
                                    conversaGrupoId.Value != idConversa;
            var estadoCentral = string.Equals(contextoAtual?.Estado, CentralRoutingService.EstadoAguardandoEscolha, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(contextoAtual?.Estado, CentralRoutingService.EstadoEstabelecimentoSelecionado, StringComparison.OrdinalIgnoreCase);

            if (ehSegmentoCentral &&
                estadoCentral &&
                string.IsNullOrWhiteSpace(etapaContexto) &&
                !string.Equals(etapaAtual, EtapaNome, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "[Conversa={Conversa}] Restaurando contexto da garagem a partir do lead na etapa {Etapa}",
                    idConversa,
                    etapaAtual);

                await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, etapaAtual, viaNumeroCentral);
                contextoAtual = new ConversationContext
                {
                    Estado = EstadoQuestionario,
                    DadosColetados = BuildDadosContexto(contextoAtual, lead, etapaAtual, viaNumeroCentral),
                    ExpiracaoEstado = null
                };
            }

            if (!string.Equals(contextoAtual?.Estado, EstadoQuestionario, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(etapaAtual, EtapaNome, StringComparison.Ordinal))
            {
                var nomeEstabelecimento = await ObterNomeEstabelecimentoAsync(scope.IdEstabelecimento);
                await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, etapaAtual, viaNumeroCentral);
                return (true, CriarBoasVindas(nomeEstabelecimento));
            }

            var decision = await ProcessarEtapaAsync(
                idConversa,
                contextoAtual,
                lead,
                etapaAtual,
                mensagemTexto,
                viaNumeroCentral,
                scope.IdEstabelecimento);

            return (true, decision);
        }

        public async Task<bool> IsGarageEstabelecimentoAsync(Guid idEstabelecimento)
        {
            var modulos = await _estabelecimentoRepository.ObterModulosAtivosAsync(idEstabelecimento);
            return modulos.Any(modulo => string.Equals(modulo, "GARAGEM", StringComparison.OrdinalIgnoreCase));
        }

        private async Task<AssistantDecision> ProcessarEtapaAsync(
            Guid idConversa,
            ConversationContext? contextoAtual,
            GarageLead lead,
            string etapaAtual,
            string mensagemTexto,
            bool viaNumeroCentral,
            Guid idEstabelecimento)
        {
            var nomeEstabelecimento = await ObterNomeEstabelecimentoAsync(idEstabelecimento);

            switch (etapaAtual)
            {
                case EtapaNome:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaNome, viaNumeroCentral);
                        return CriarBoasVindas(nomeEstabelecimento);
                    }

                    lead.NomeCliente = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaObjetivo, viaNumeroCentral);
                    return CriarPergunta(EtapaObjetivo, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaObjetivo:
                {
                    var objetivo = ParseObjetivo(mensagemTexto);
                    if (objetivo == null)
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaObjetivo, viaNumeroCentral);
                        return CriarPergunta(EtapaObjetivo, lead, nomeEstabelecimento, incluirErro: true);
                    }

                    lead.Objetivo = objetivo;
                    LimparCamposNaoRelacionadosAoObjetivo(lead, objetivo);
                    lead.Urgencia = null;
                    await _garagemLeadRepository.AtualizarAsync(lead);

                    if (string.Equals(objetivo, ObjetivoComprar, StringComparison.Ordinal))
                    {
                        var vitrine = await _garagemVeiculoRepository.ObterVitrineAsync(idEstabelecimento, null, null);
                        if (vitrine.Items.Count > 0)
                        {
                            var vitrineUrl = await _garagemVeiculoRepository.ObterVitrineBaseUrlAsync(idEstabelecimento);
                            var estabelecimentoPublico = await _estabelecimentoRepository.ObterResumoPublicoAsync(idEstabelecimento);
                            await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaCatalogoLista, viaNumeroCentral);
                            return CriarMensagemCatalogo(
                                vitrine.Items.Take(8).ToList(),
                                vitrineUrl,
                                estabelecimentoPublico?.Slug,
                                _garagePublicBaseUrl,
                                ExtrairPrimeiroNome(lead.NomeCliente));
                        }
                    }

                    var proximaEtapa = DeterminarPrimeiraEtapaPorObjetivo(objetivo);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, proximaEtapa, viaNumeroCentral);
                    return CriarPergunta(proximaEtapa, lead, nomeEstabelecimento, incluirErro: false);
                }

                case EtapaModelo:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaModelo, viaNumeroCentral);
                        return CriarPergunta(EtapaModelo, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.ModeloInteresse = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaFaixaInvestimento, viaNumeroCentral);
                    return CriarPergunta(EtapaFaixaInvestimento, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaFaixaInvestimento:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaFaixaInvestimento, viaNumeroCentral);
                        return CriarPergunta(EtapaFaixaInvestimento, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.FaixaInvestimento = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaFormaPagamento, viaNumeroCentral);
                    return CriarPergunta(EtapaFormaPagamento, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaFormaPagamento:
                {
                    var formaPagamento = ParseFormaPagamento(mensagemTexto);
                    if (formaPagamento == null)
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaFormaPagamento, viaNumeroCentral);
                        return CriarPergunta(EtapaFormaPagamento, lead, nomeEstabelecimento, incluirErro: true);
                    }

                    lead.FormaPagamento = formaPagamento;
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    if (RequerPerguntaValorEntrada(formaPagamento))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaValorEntrada, viaNumeroCentral);
                        return CriarPergunta(EtapaValorEntrada, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.ValorEntradaTexto = null;
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaUrgencia, viaNumeroCentral);
                    return CriarPergunta(EtapaUrgencia, lead, nomeEstabelecimento, incluirErro: false);
                }

                case EtapaValorEntrada:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaValorEntrada, viaNumeroCentral);
                        return CriarPergunta(EtapaValorEntrada, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.ValorEntradaTexto = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaUrgencia, viaNumeroCentral);
                    return CriarPergunta(EtapaUrgencia, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaTrocaModeloAtual:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaModeloAtual, viaNumeroCentral);
                        return CriarPergunta(EtapaTrocaModeloAtual, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.TrocaModeloAtual = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaAnoModelo, viaNumeroCentral);
                    return CriarPergunta(EtapaTrocaAnoModelo, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaTrocaAnoModelo:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaAnoModelo, viaNumeroCentral);
                        return CriarPergunta(EtapaTrocaAnoModelo, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.TrocaAnoModelo = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaKm, viaNumeroCentral);
                    return CriarPergunta(EtapaTrocaKm, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaTrocaKm:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaKm, viaNumeroCentral);
                        return CriarPergunta(EtapaTrocaKm, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.TrocaKm = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaQuitado, viaNumeroCentral);
                    return CriarPergunta(EtapaTrocaQuitado, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaTrocaQuitado:
                {
                    var quitado = ParseQuitado(mensagemTexto);
                    if (quitado == null)
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaQuitado, viaNumeroCentral);
                        return CriarPergunta(EtapaTrocaQuitado, lead, nomeEstabelecimento, incluirErro: true);
                    }

                    lead.TrocaQuitado = quitado;
                    await _garagemLeadRepository.AtualizarAsync(lead);

                    var vitrineParaTroca = await _garagemVeiculoRepository.ObterVitrineAsync(idEstabelecimento, null, null);
                    if (vitrineParaTroca.Items.Count > 0)
                    {
                        var vitrineUrlTroca = await _garagemVeiculoRepository.ObterVitrineBaseUrlAsync(idEstabelecimento);
                        var estabelecimentoPublico = await _estabelecimentoRepository.ObterResumoPublicoAsync(idEstabelecimento);
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaCatalogoLista, viaNumeroCentral);
                        return CriarMensagemCatalogo(
                            vitrineParaTroca.Items.Take(8).ToList(),
                            vitrineUrlTroca,
                            estabelecimentoPublico?.Slug,
                            _garagePublicBaseUrl,
                            ExtrairPrimeiroNome(lead.NomeCliente));
                    }

                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaModeloDesejado, viaNumeroCentral);
                    return CriarPergunta(EtapaTrocaModeloDesejado, lead, nomeEstabelecimento, incluirErro: false);
                }

                case EtapaTrocaModeloDesejado:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaModeloDesejado, viaNumeroCentral);
                        return CriarPergunta(EtapaTrocaModeloDesejado, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.TrocaModeloDesejado = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaCondicao, viaNumeroCentral);
                    return CriarPergunta(EtapaTrocaCondicao, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaTrocaCondicao:
                {
                    var condicao = ParseCondicaoTroca(mensagemTexto);
                    if (condicao == null)
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaCondicao, viaNumeroCentral);
                        return CriarPergunta(EtapaTrocaCondicao, lead, nomeEstabelecimento, incluirErro: true);
                    }

                    lead.TrocaCondicao = condicao;
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaUrgencia, viaNumeroCentral);
                    return CriarPergunta(EtapaUrgencia, lead, nomeEstabelecimento, incluirErro: false);
                }

                case EtapaVendaModelo:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaVendaModelo, viaNumeroCentral);
                        return CriarPergunta(EtapaVendaModelo, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.VendaModelo = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaVendaAno, viaNumeroCentral);
                    return CriarPergunta(EtapaVendaAno, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaVendaAno:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaVendaAno, viaNumeroCentral);
                        return CriarPergunta(EtapaVendaAno, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.VendaAno = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaVendaKm, viaNumeroCentral);
                    return CriarPergunta(EtapaVendaKm, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaVendaKm:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaVendaKm, viaNumeroCentral);
                        return CriarPergunta(EtapaVendaKm, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.VendaKm = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaVendaQuitado, viaNumeroCentral);
                    return CriarPergunta(EtapaVendaQuitado, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaVendaQuitado:
                {
                    var quitado = ParseQuitado(mensagemTexto);
                    if (quitado == null)
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaVendaQuitado, viaNumeroCentral);
                        return CriarPergunta(EtapaVendaQuitado, lead, nomeEstabelecimento, incluirErro: true);
                    }

                    lead.VendaQuitado = quitado;
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaUrgencia, viaNumeroCentral);
                    return CriarPergunta(EtapaUrgencia, lead, nomeEstabelecimento, incluirErro: false);
                }

                case EtapaUrgencia:
                {
                    var urgenciaNormalizada = NormalizeText(mensagemTexto);
                    var urgencia = ParseUrgencia(mensagemTexto);
                    _logger.LogInformation(
                        "[Conversa={Conversa}] Garagem urgencia recebida raw='{Mensagem}' normalizado='{Normalizado}' parse='{Urgencia}'",
                        idConversa,
                        mensagemTexto ?? string.Empty,
                        urgenciaNormalizada,
                        urgencia ?? "(null)");
                    if (urgencia == null)
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaUrgencia, viaNumeroCentral);
                        return CriarPergunta(EtapaUrgencia, lead, nomeEstabelecimento, incluirErro: true);
                    }

                    lead.Urgencia = urgencia;
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaCpf, viaNumeroCentral);
                    return CriarPergunta(EtapaCpf, lead, nomeEstabelecimento, incluirErro: false);
                }

                case EtapaCpf:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaCpf, viaNumeroCentral);
                        return CriarPergunta(EtapaCpf, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.Cpf = mensagemTexto.Trim();
                    await _garagemLeadRepository.ConcluirAsync(lead);

                    lead.Status = "concluido";
                    lead.DataConclusao = DateTime.UtcNow;
                    await SalvarContextoConcluidoAsync(idConversa, contextoAtual, lead, viaNumeroCentral);
                    return CriarMensagemConclusao(lead.NomeCliente);

                case EtapaCatalogoLista:
                {
                    var vitrine = await _garagemVeiculoRepository.ObterVitrineAsync(idEstabelecimento, null, null);
                    var veiculos = vitrine.Items.Take(8).ToList();
                    var vitrineUrl = await _garagemVeiculoRepository.ObterVitrineBaseUrlAsync(idEstabelecimento);
                    var estabelecimentoPublico = await _estabelecimentoRepository.ObterResumoPublicoAsync(idEstabelecimento);
                    var proximaEtapaAposCatalogo = DeterminarProximaEtapaAposCatalogo(lead.Objetivo);
                    var inputCatalogo = NormalizeText(mensagemTexto);

                    if (inputCatalogo == "pular" || inputCatalogo == "garagem_catalogo_pular")
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, proximaEtapaAposCatalogo, viaNumeroCentral);
                        return CriarPergunta(proximaEtapaAposCatalogo, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    if (inputCatalogo == "vitrine" || inputCatalogo == "ver vitrine" || inputCatalogo == "garagem_catalogo_vitrine")
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaCatalogoLista, viaNumeroCentral);
                        return CriarMensagemLinkVitrine(
                            BuildCanonicalStoreUrl(vitrineUrl, estabelecimentoPublico?.Slug, _garagePublicBaseUrl));
                    }

                    if (int.TryParse(mensagemTexto?.Trim(), out var numero) && numero >= 1 && numero <= veiculos.Count)
                    {
                        var veiculo = veiculos[numero - 1];
                        lead.IdVeiculoSelecionado = veiculo.Id;
                        lead.VeiculoSelecionadoTitulo = veiculo.Title;
                        var urlVeiculo = BuildCanonicalVehicleUrl(vitrineUrl, estabelecimentoPublico?.Slug, veiculo.Slug, _garagePublicBaseUrl);

                        if (string.Equals(lead.Objetivo, ObjetivoComprar, StringComparison.Ordinal))
                        {
                            lead.ModeloInteresse = veiculo.Title;
                            await _garagemLeadRepository.AtualizarAsync(lead);
                            await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaFormaPagamento, viaNumeroCentral);
                            var perguntaPagamento = CriarPergunta(EtapaFormaPagamento, lead, nomeEstabelecimento, incluirErro: false);
                            return new AssistantDecision(
                                BuildMensagemSelecaoVeiculo(veiculo.Title, urlVeiculo, perguntaPagamento.Reply),
                                "none",
                                null,
                                false,
                                null,
                                null,
                                perguntaPagamento.ReplyButtons);
                        }
                        else
                        {
                            lead.TrocaModeloDesejado = veiculo.Title;
                            await _garagemLeadRepository.AtualizarAsync(lead);
                            await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaCondicao, viaNumeroCentral);
                            var perguntaCondicao = CriarPergunta(EtapaTrocaCondicao, lead, nomeEstabelecimento, incluirErro: false);
                            return new AssistantDecision(
                                BuildMensagemSelecaoVeiculo(veiculo.Title, urlVeiculo, perguntaCondicao.Reply),
                                "none", null, false, null, null, perguntaCondicao.ReplyButtons);
                        }
                    }

                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaCatalogoLista, viaNumeroCentral);
                    return CriarMensagemCatalogo(
                        veiculos,
                        vitrineUrl,
                        estabelecimentoPublico?.Slug,
                        _garagePublicBaseUrl,
                        ExtrairPrimeiroNome(lead.NomeCliente),
                        incluirErro: true);
                }

                default:
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaNome, viaNumeroCentral);
                    return CriarBoasVindas(nomeEstabelecimento);
            }
        }

        private async Task<GarageLead> ObterOuCriarLeadAsync(
            Guid idConversa,
            Guid idEstabelecimento,
            Guid idCliente,
            string telefoneCliente,
            bool viaNumeroCentral)
        {
            var existente = await _garagemLeadRepository.ObterLeadAbertoAsync(idEstabelecimento, telefoneCliente);
            if (existente != null)
            {
                existente.IdConversa = idConversa;
                existente.IdCliente = idCliente;
                existente.TelefoneE164 = telefoneCliente;
                existente.ViaNumeroCentral = viaNumeroCentral;
                await _garagemLeadRepository.AtualizarAsync(existente);
                return existente;
            }

            var lead = new GarageLead
            {
                Id = Guid.NewGuid(),
                IdConversa = idConversa,
                IdCliente = idCliente,
                IdEstabelecimento = idEstabelecimento,
                TelefoneE164 = telefoneCliente,
                ViaNumeroCentral = viaNumeroCentral,
                Status = "em_andamento",
                DataCriacao = DateTime.UtcNow,
                DataAtualizacao = DateTime.UtcNow
            };

            await _garagemLeadRepository.CriarAsync(lead);
            return lead;
        }

        private async Task<string?> GarantirTelefoneClienteAsync(EffectiveConversationScope scope)
        {
            if (!string.IsNullOrWhiteSpace(scope.TelefoneCliente))
            {
                return scope.TelefoneCliente;
            }

            if (scope.IdCliente == Guid.Empty || scope.IdEstabelecimento == Guid.Empty)
            {
                return null;
            }

            return await _clienteRepository.ObterTelefoneClienteAsync(scope.IdCliente, scope.IdEstabelecimento);
        }

        private async Task<string> ObterNomeEstabelecimentoAsync(Guid idEstabelecimento)
        {
            var nome = await _estabelecimentoRepository.ObterNomeFantasiaAsync(idEstabelecimento);
            return string.IsNullOrWhiteSpace(nome) ? "Brasil Motors" : nome.Trim();
        }

        private async Task SalvarContextoQuestionarioAsync(
            Guid idConversa,
            ConversationContext? contextoAtual,
            GarageLead lead,
            string etapa,
            bool viaNumeroCentral)
        {
            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = EstadoQuestionario,
                DadosColetados = BuildDadosContexto(contextoAtual, lead, etapa, viaNumeroCentral),
                ExpiracaoEstado = null
            });
        }

        private async Task SalvarContextoConcluidoAsync(
            Guid idConversa,
            ConversationContext? contextoAtual,
            GarageLead lead,
            bool viaNumeroCentral)
        {
            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = EstadoConcluido,
                DadosColetados = BuildDadosContexto(contextoAtual, lead, EtapaCpf, viaNumeroCentral),
                ExpiracaoEstado = null
            });
        }

        private Dictionary<string, object> BuildDadosContexto(
            ConversationContext? contextoAtual,
            GarageLead lead,
            string etapa,
            bool viaNumeroCentral)
        {
            var dados = new Dictionary<string, object>
            {
                [ChaveLeadId] = lead.Id.ToString(),
                [ChaveEstabelecimentoId] = lead.IdEstabelecimento.ToString(),
                [ChaveEtapa] = etapa,
                [ChaveViaNumeroCentral] = viaNumeroCentral
            };

            if (!string.IsNullOrWhiteSpace(lead.NomeCliente))
            {
                dados[ChaveNomeCliente] = lead.NomeCliente!;
            }

            if (!string.IsNullOrWhiteSpace(lead.Objetivo))
            {
                dados[ChaveObjetivo] = lead.Objetivo!;
            }

            if (!string.IsNullOrWhiteSpace(lead.ModeloInteresse))
            {
                dados[ChaveModeloInteresse] = lead.ModeloInteresse!;
            }

            if (!string.IsNullOrWhiteSpace(lead.FaixaInvestimento))
            {
                dados[ChaveFaixaInvestimento] = lead.FaixaInvestimento!;
            }

            if (!string.IsNullOrWhiteSpace(lead.FormaPagamento))
            {
                dados[ChaveFormaPagamento] = lead.FormaPagamento!;
            }

            if (!string.IsNullOrWhiteSpace(lead.ValorEntradaTexto))
            {
                dados[ChaveValorEntrada] = lead.ValorEntradaTexto!;
            }

            if (!string.IsNullOrWhiteSpace(lead.Urgencia))
            {
                dados[ChaveUrgencia] = lead.Urgencia!;
            }

            if (!string.IsNullOrWhiteSpace(lead.TrocaModeloAtual))
            {
                dados[ChaveTrocaModeloAtual] = lead.TrocaModeloAtual!;
            }

            if (!string.IsNullOrWhiteSpace(lead.TrocaAnoModelo))
            {
                dados[ChaveTrocaAnoModelo] = lead.TrocaAnoModelo!;
            }

            if (!string.IsNullOrWhiteSpace(lead.TrocaKm))
            {
                dados[ChaveTrocaKm] = lead.TrocaKm!;
            }

            if (!string.IsNullOrWhiteSpace(lead.TrocaQuitado))
            {
                dados[ChaveTrocaQuitado] = lead.TrocaQuitado!;
            }

            if (!string.IsNullOrWhiteSpace(lead.TrocaModeloDesejado))
            {
                dados[ChaveTrocaModeloDesejado] = lead.TrocaModeloDesejado!;
            }

            if (!string.IsNullOrWhiteSpace(lead.TrocaCondicao))
            {
                dados[ChaveTrocaCondicao] = lead.TrocaCondicao!;
            }

            if (!string.IsNullOrWhiteSpace(lead.VendaModelo))
            {
                dados[ChaveVendaModelo] = lead.VendaModelo!;
            }

            if (!string.IsNullOrWhiteSpace(lead.VendaAno))
            {
                dados[ChaveVendaAno] = lead.VendaAno!;
            }

            if (!string.IsNullOrWhiteSpace(lead.VendaKm))
            {
                dados[ChaveVendaKm] = lead.VendaKm!;
            }

            if (!string.IsNullOrWhiteSpace(lead.VendaQuitado))
            {
                dados[ChaveVendaQuitado] = lead.VendaQuitado!;
            }

            if (!string.IsNullOrWhiteSpace(lead.Cpf))
            {
                dados[ChaveCpf] = lead.Cpf!;
            }

            if (viaNumeroCentral)
            {
                var snapshot = CentralRoutingService.BuildSnapshot(contextoAtual);
                if (!string.IsNullOrWhiteSpace(snapshot.CentralDisplayPhone))
                {
                    dados[CentralRoutingService.ChaveCentralDisplayPhone] = snapshot.CentralDisplayPhone!;
                }

                if (snapshot.ConversaGrupoId.HasValue)
                {
                    dados[CentralRoutingService.ChaveConversaGrupoId] = snapshot.ConversaGrupoId.Value.ToString();
                }

                if (snapshot.ConversaSegmentoAtivaId.HasValue)
                {
                    dados[CentralRoutingService.ChaveConversaSegmentoAtivaId] = snapshot.ConversaSegmentoAtivaId.Value.ToString();
                }

                if (snapshot.EstabelecimentoId.HasValue)
                {
                    dados[CentralRoutingService.ChaveEstabelecimentoEscolhidoId] = snapshot.EstabelecimentoId.Value.ToString();
                }

                if (!string.IsNullOrWhiteSpace(snapshot.EstabelecimentoNome))
                {
                    dados[CentralRoutingService.ChaveEstabelecimentoEscolhidoNome] = snapshot.EstabelecimentoNome!;
                }

                if (!string.IsNullOrWhiteSpace(snapshot.EstabelecimentoTipo))
                {
                    dados[CentralRoutingService.ChaveEstabelecimentoEscolhidoTipo] = snapshot.EstabelecimentoTipo!;
                }
            }

            return dados;
        }

        private static AssistantDecision CriarBoasVindas(string nomeEstabelecimento)
        {
            var mensagem = $"Ola, tudo bem? 👋\n\nSeja bem-vindo(a) a {nomeEstabelecimento} 🚗✨\n\nVou te fazer algumas perguntas rapidas para que nosso vendedor te chame ja com uma simulacao mais assertiva, combinado?\n\nQual e o seu nome?";
            return new AssistantDecision(mensagem, "none", null, false, null);
        }

        private AssistantDecision CriarPergunta(string etapa, GarageLead lead, string nomeEstabelecimento, bool incluirErro)
        {
            return etapa switch
            {
                EtapaObjetivo => new AssistantDecision(
                    BuildStructuredPrompt(
                        incluirErro ? "Nao consegui identificar essa opcao. 😊" : $"Perfeito, {ExtrairPrimeiroNome(lead.NomeCliente)}! Agora me diz uma coisa 😊",
                        "Voce quer:"),
                    "none",
                    null,
                    false,
                    null,
                    null,
                    BuildObjetivoButtons()),
                EtapaModelo => new AssistantDecision(
                    "Voce ja tem algum modelo em mente ou ainda esta pesquisando? 🔍",
                    "none",
                    null,
                    false,
                    null),
                EtapaFaixaInvestimento => new AssistantDecision(
                    "Em qual faixa voce pretende investir? 💵\n\nExemplo: ate 60 mil, entre 80 e 100 mil, ainda nao defini.",
                    "none",
                    null,
                    false,
                    null),
                EtapaFormaPagamento => new AssistantDecision(
                    BuildStructuredPrompt(
                        incluirErro ? "Nao consegui identificar essa opcao. 😊" : "Como voce pretende fazer a compra?",
                        string.Empty),
                    "none",
                    null,
                    false,
                    null,
                    null,
                    BuildFormaPagamentoButtons()),
                EtapaValorEntrada => new AssistantDecision(
                    "Qual valor voce consegue dar de entrada? 💰\n\nSe for sem entrada, pode me responder assim mesmo.",
                    "none",
                    null,
                    false,
                    null),
                EtapaTrocaModeloAtual => new AssistantDecision(
                    "Qual e o modelo atual do seu veiculo?",
                    "none",
                    null,
                    false,
                    null),
                EtapaTrocaAnoModelo => new AssistantDecision(
                    "Qual e o ano do veiculo?",
                    "none",
                    null,
                    false,
                    null),
                EtapaTrocaKm => new AssistantDecision(
                    "Qual a quilometragem aproximada?",
                    "none",
                    null,
                    false,
                    null),
                EtapaTrocaQuitado => new AssistantDecision(
                    BuildStructuredPrompt(
                        incluirErro ? "Nao consegui identificar essa opcao. 😊" : "O veiculo esta quitado?",
                        string.Empty),
                    "none",
                    null,
                    false,
                    null,
                    null,
                    BuildQuitadoButtons()),
                EtapaTrocaModeloDesejado => new AssistantDecision(
                    "Qual modelo voce deseja na troca?",
                    "none",
                    null,
                    false,
                    null),
                EtapaTrocaCondicao => new AssistantDecision(
                    BuildStructuredPrompt(
                        incluirErro ? "Nao consegui identificar essa opcao. 😊" : "Qual condicao de troca voce busca?",
                        string.Empty),
                    "none",
                    null,
                    false,
                    null,
                    null,
                    BuildCondicaoTrocaButtons()),
                EtapaVendaModelo => new AssistantDecision(
                    "Qual e o modelo do veiculo que voce quer vender?",
                    "none",
                    null,
                    false,
                    null),
                EtapaVendaAno => new AssistantDecision(
                    "Qual e o ano do veiculo?",
                    "none",
                    null,
                    false,
                    null),
                EtapaVendaKm => new AssistantDecision(
                    "Qual a quilometragem aproximada?",
                    "none",
                    null,
                    false,
                    null),
                EtapaVendaQuitado => new AssistantDecision(
                    BuildStructuredPrompt(
                        incluirErro ? "Nao consegui identificar essa opcao. 😊" : "O veiculo esta quitado?",
                        string.Empty),
                    "none",
                    null,
                    false,
                    null,
                    null,
                    BuildQuitadoButtons()),
                EtapaUrgencia => new AssistantDecision(
                    BuildStructuredPrompt(
                        incluirErro ? "Nao consegui identificar essa opcao. 😊" : "Pra quando voce pretende resolver isso?",
                        string.Empty),
                    "none",
                    null,
                    false,
                    null,
                    null,
                    BuildUrgenciaButtons()),
                EtapaCpf => new AssistantDecision(
                    $"Otimo! Por ultimo, qual e o seu CPF? 📋\n\nEle sera usado apenas para facilitar a simulacao de credito.",
                    "none",
                    null,
                    false,
                    null),
                _ => CriarBoasVindas(nomeEstabelecimento)
            };
        }

        private static AssistantDecision CriarMensagemConclusao(string? nomeCliente)
        {
            var prefixo = string.IsNullOrWhiteSpace(nomeCliente)
                ? "Perfeito! 🚗✨"
                : $"Perfeito, {ExtrairPrimeiroNome(nomeCliente)}! 🚗✨";

            return new AssistantDecision(
                $"{prefixo}\n\nJa encaminhei suas informacoes para o nosso vendedor.\nEm breve ele vai te chamar ja com uma simulacao 😉",
                "none",
                null,
                false,
                null);
        }

        private static AssistantDecision CriarMensagemPosConclusao()
        {
            return new AssistantDecision(
                "Suas informacoes ja foram enviadas para o vendedor ✅\n\nEm breve ele entra em contato com voce com a simulacao 😉",
                "none",
                null,
                false,
                null);
        }

        private static string BuildStructuredPrompt(string titulo, string subtitulo)
        {
            var builder = new StringBuilder();
            builder.AppendLine(titulo);

            if (!string.IsNullOrWhiteSpace(subtitulo))
            {
                builder.AppendLine();
                builder.Append(subtitulo);
            }

            return builder.ToString().Trim();
        }

        private static IReadOnlyList<WhatsAppReplyButtonOption> BuildObjetivoButtons()
        {
            return new[]
            {
                new WhatsAppReplyButtonOption("garagem_objetivo_comprar", "Comprar 🚘"),
                new WhatsAppReplyButtonOption("garagem_objetivo_vender", "Vender 💰"),
                new WhatsAppReplyButtonOption("garagem_objetivo_trocar", "Trocar 🔄")
            };
        }

        private static IReadOnlyList<WhatsAppReplyButtonOption> BuildFormaPagamentoButtons()
        {
            return new[]
            {
                new WhatsAppReplyButtonOption("garagem_pagamento_avista", "A vista 💸"),
                new WhatsAppReplyButtonOption("garagem_pagamento_entrada_financiamento", "Entrada + Fin."),
                new WhatsAppReplyButtonOption("garagem_pagamento_financiado_100", "100% financ.")
            };
        }

        private static IReadOnlyList<WhatsAppReplyButtonOption> BuildQuitadoButtons()
        {
            return new[]
            {
                new WhatsAppReplyButtonOption("garagem_quitado_sim", "Sim"),
                new WhatsAppReplyButtonOption("garagem_quitado_nao", "Nao")
            };
        }

        private static IReadOnlyList<WhatsAppReplyButtonOption> BuildCondicaoTrocaButtons()
        {
            return new[]
            {
                new WhatsAppReplyButtonOption("garagem_troca_condicao_direta", "Troca direta"),
                new WhatsAppReplyButtonOption("garagem_troca_condicao_volta", "Troca + volta"),
                new WhatsAppReplyButtonOption("garagem_troca_condicao_financiamento", "Troca + financ.")
            };
        }

        private static IReadOnlyList<WhatsAppReplyButtonOption> BuildUrgenciaButtons()
        {
            return new[]
            {
                new WhatsAppReplyButtonOption("garagem_urgencia_esta_semana", "Essa semana ⚡"),
                new WhatsAppReplyButtonOption("garagem_urgencia_proximas_semanas", "Prox. semanas"),
                new WhatsAppReplyButtonOption("garagem_urgencia_so_pesquisando", "So pesquisando")
            };
        }

        private static string? ParseObjetivo(string? texto)
        {
            var normalizado = NormalizeText(texto);
            return normalizado switch
            {
                "1" or "comprar" or "garagem_objetivo_comprar" => ObjetivoComprar,
                "2" or "vender" or "garagem_objetivo_vender" => ObjetivoVender,
                "3" or "trocar" or "garagem_objetivo_trocar" => ObjetivoTrocar,
                _ => null
            };
        }

        private static string? ParseFormaPagamento(string? texto)
        {
            var normalizado = NormalizeText(texto);
            return normalizado switch
            {
                "1" or "avista" or "a vista" or "garagem_pagamento_avista" => "avista",
                "2" or "entrada financiamento" or "entrada fin" or "entrada + financiamento" or "garagem_pagamento_entrada_financiamento" => "entrada_financiamento",
                "3" or "financiado" or "100 financiado" or "100 financ" or "100% financiado" or "garagem_pagamento_financiado_100" => "financiado_100",
                _ => null
            };
        }

        private static string? ParseQuitado(string? texto)
        {
            var normalizado = NormalizeText(texto);
            return normalizado switch
            {
                "1" or "sim" or "s" or "garagem_quitado_sim" => "sim",
                "2" or "nao" or "n" or "garagem_quitado_nao" => "nao",
                _ => null
            };
        }

        private static string? ParseCondicaoTroca(string? texto)
        {
            var normalizado = NormalizeText(texto);
            return normalizado switch
            {
                "1" or "troca direta" or "direta" or "garagem_troca_condicao_direta" => "troca_direta",
                "2" or "troca + volta" or "troca volta" or "volta" or "garagem_troca_condicao_volta" => "troca_volta",
                "3" or "troca + financiamento" or "troca financiamento" or "troca + financ." or "troca financ." or "troca financ" or "financiamento" or "garagem_troca_condicao_financiamento" => "troca_financiamento",
                _ => null
            };
        }

        private static string? ParseUrgencia(string? texto)
        {
            var normalizado = NormalizeText(texto);
            return normalizado switch
            {
                "1" or "ainda essa semana" or "essa semana" or "garagem_urgencia_esta_semana" => "esta_semana",
                "2" or "prox semanas" or "proximas semanas" or "nas proximas semanas" or "garagem_urgencia_proximas_semanas" => "proximas_semanas",
                "3" or "so pesquisando" or "garagem_urgencia_so_pesquisando" => "so_pesquisando",
                _ => null
            };
        }

        private static void LimparCamposNaoRelacionadosAoObjetivo(GarageLead lead, string objetivo)
        {
            lead.IdVeiculoSelecionado = null;
            lead.VeiculoSelecionadoTitulo = null;

            if (string.Equals(objetivo, ObjetivoTrocar, StringComparison.Ordinal))
            {
                lead.ModeloInteresse = null;
                lead.FaixaInvestimento = null;
                lead.FormaPagamento = null;
                lead.ValorEntradaTexto = null;
                lead.VendaModelo = null;
                lead.VendaAno = null;
                lead.VendaKm = null;
                lead.VendaQuitado = null;
                return;
            }

            if (string.Equals(objetivo, ObjetivoVender, StringComparison.Ordinal))
            {
                lead.ModeloInteresse = null;
                lead.FaixaInvestimento = null;
                lead.FormaPagamento = null;
                lead.ValorEntradaTexto = null;
                lead.TrocaModeloAtual = null;
                lead.TrocaAnoModelo = null;
                lead.TrocaKm = null;
                lead.TrocaQuitado = null;
                lead.TrocaModeloDesejado = null;
                lead.TrocaCondicao = null;
                return;
            }

            lead.TrocaModeloAtual = null;
            lead.TrocaAnoModelo = null;
            lead.TrocaKm = null;
            lead.TrocaQuitado = null;
            lead.TrocaModeloDesejado = null;
            lead.TrocaCondicao = null;
            lead.VendaModelo = null;
            lead.VendaAno = null;
            lead.VendaKm = null;
            lead.VendaQuitado = null;
        }

        private static string DeterminarPrimeiraEtapaPorObjetivo(string? objetivo)
        {
            return objetivo switch
            {
                ObjetivoTrocar => EtapaTrocaModeloAtual,
                ObjetivoVender => EtapaVendaModelo,
                _ => EtapaModelo
            };
        }

        internal static string DeterminarEtapaAtual(GarageLead lead)
        {
            if (string.IsNullOrWhiteSpace(lead.NomeCliente))
            {
                return EtapaNome;
            }

            if (string.IsNullOrWhiteSpace(lead.Objetivo))
            {
                return EtapaObjetivo;
            }

            var objetivo = NormalizeText(lead.Objetivo);

            if (string.Equals(objetivo, ObjetivoTrocar, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(lead.TrocaModeloAtual))
                {
                    return EtapaTrocaModeloAtual;
                }

                if (string.IsNullOrWhiteSpace(lead.TrocaAnoModelo))
                {
                    return EtapaTrocaAnoModelo;
                }

                if (string.IsNullOrWhiteSpace(lead.TrocaKm))
                {
                    return EtapaTrocaKm;
                }

                if (string.IsNullOrWhiteSpace(lead.TrocaQuitado))
                {
                    return EtapaTrocaQuitado;
                }

                if (string.IsNullOrWhiteSpace(lead.TrocaModeloDesejado))
                {
                    return EtapaTrocaModeloDesejado;
                }

                if (string.IsNullOrWhiteSpace(lead.TrocaCondicao))
                {
                    return EtapaTrocaCondicao;
                }

                if (string.IsNullOrWhiteSpace(lead.Urgencia))
                {
                    return EtapaUrgencia;
                }

                return EtapaCpf;
            }

            if (string.Equals(objetivo, ObjetivoVender, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(lead.VendaModelo))
                {
                    return EtapaVendaModelo;
                }

                if (string.IsNullOrWhiteSpace(lead.VendaAno))
                {
                    return EtapaVendaAno;
                }

                if (string.IsNullOrWhiteSpace(lead.VendaKm))
                {
                    return EtapaVendaKm;
                }

                if (string.IsNullOrWhiteSpace(lead.VendaQuitado))
                {
                    return EtapaVendaQuitado;
                }

                if (string.IsNullOrWhiteSpace(lead.Urgencia))
                {
                    return EtapaUrgencia;
                }

                return EtapaCpf;
            }

            if (string.IsNullOrWhiteSpace(lead.ModeloInteresse))
            {
                return EtapaModelo;
            }

            if (DevePerguntarFaixaInvestimento(lead) && string.IsNullOrWhiteSpace(lead.FaixaInvestimento))
            {
                return EtapaFaixaInvestimento;
            }

            if (string.IsNullOrWhiteSpace(lead.FormaPagamento))
            {
                return EtapaFormaPagamento;
            }

            if (RequerPerguntaValorEntrada(lead.FormaPagamento) && string.IsNullOrWhiteSpace(lead.ValorEntradaTexto))
            {
                return EtapaValorEntrada;
            }

            if (string.IsNullOrWhiteSpace(lead.Urgencia))
            {
                return EtapaUrgencia;
            }

            return EtapaCpf;
        }

        private static string? ObterEtapaDoContexto(ConversationContext? contexto)
        {
            return CentralRoutingService.GetStringValue(contexto?.DadosColetados, ChaveEtapa);
        }

        private static string NormalizeText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (category == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(ch) || ch == '_' || char.IsWhiteSpace(ch))
                {
                    builder.Append(char.ToLowerInvariant(ch));
                    continue;
                }

                builder.Append(' ');
            }

            var flattened = builder.ToString().Normalize(NormalizationForm.FormC);
            return string.Join(" ", flattened.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        private static AssistantDecision CriarMensagemCatalogo(
            IReadOnlyList<GarageVehicleDto> veiculos,
            string? vitrineUrl,
            string? estabelecimentoSlug,
            string? publicBaseUrl,
            string primeiroNome,
            bool incluirErro = false)
        {
            var ptBr = CultureInfo.GetCultureInfo("pt-BR");
            var sb = new StringBuilder();
            var lojaUrl = BuildCanonicalStoreUrl(vitrineUrl, estabelecimentoSlug, publicBaseUrl);

            if (incluirErro)
            {
                sb.AppendLine("Nao entendi a opcao. Digite o numero do veiculo ou escolha abaixo.");
            }
            else
            {
                sb.AppendLine($"Legal, {primeiroNome}! Antes de continuar, veja os veiculos disponiveis na nossa loja 🚗✨");
            }

            sb.AppendLine();

            for (var i = 0; i < veiculos.Count; i++)
            {
                var v = veiculos[i];
                var preco = v.Price.ToString("N0", ptBr);
                var km = v.Km > 0 ? $" | {v.Km.ToString("N0", ptBr)} km" : string.Empty;
                sb.AppendLine($"{i + 1}. *{v.Title}* - R$ {preco}{km}");
                var veiculoUrl = BuildCanonicalVehicleUrl(vitrineUrl, estabelecimentoSlug, v.Slug, publicBaseUrl);
                if (!string.IsNullOrWhiteSpace(veiculoUrl))
                {
                    sb.AppendLine($"🔗 {veiculoUrl}");
                }

                if (i < veiculos.Count - 1)
                {
                    sb.AppendLine();
                }
            }

            if (!string.IsNullOrWhiteSpace(lojaUrl))
            {
                sb.AppendLine();
                sb.AppendLine($"Ver toda a vitrine: {lojaUrl}");
            }

            sb.AppendLine();
            sb.AppendLine("Algum desses te interessou? Digite o *numero* do veiculo.");
            sb.AppendLine("Se escolher um numero, eu te envio o link do carro.");
            sb.Append("Ou *PULAR* para continuar.");

            var buttons = new List<WhatsAppReplyButtonOption>();
            if (!string.IsNullOrWhiteSpace(lojaUrl))
            {
                buttons.Add(new WhatsAppReplyButtonOption("garagem_catalogo_vitrine", "Ver vitrine 🌐"));
            }

            buttons.Add(new WhatsAppReplyButtonOption("garagem_catalogo_pular", "Pular ➡️"));

            return new AssistantDecision(
                sb.ToString().Trim(),
                "none",
                null,
                false,
                null,
                null,
                buttons);
        }

        private static AssistantDecision CriarMensagemLinkVitrine(string? lojaUrl)
        {
            var reply = string.IsNullOrWhiteSpace(lojaUrl)
                ? "Nao consegui montar o link da vitrine agora. Se preferir, me envie o *numero* do carro ou *PULAR* para continuar."
                : $"Aqui esta a vitrine completa: {lojaUrl}\n\nSe preferir, me envie o *numero* do carro ou *PULAR* para continuar.";

            return new AssistantDecision(
                reply,
                "none",
                null,
                false,
                null,
                null,
                new[] { new WhatsAppReplyButtonOption("garagem_catalogo_pular", "Pular ➡️") });
        }

        internal static bool DevePerguntarFaixaInvestimento(GarageLead lead)
        {
            return !TemVeiculoSelecionado(lead);
        }

        internal static bool RequerPerguntaValorEntrada(string? formaPagamento)
        {
            return !string.Equals(NormalizeText(formaPagamento), "avista", StringComparison.Ordinal);
        }

        internal static string? BuildCanonicalStoreUrl(string? vitrineUrl, string? estabelecimentoSlug, string? publicBaseUrl = null)
        {
            var slug = ResolveCanonicalStoreSlug(vitrineUrl, estabelecimentoSlug);
            if (string.IsNullOrWhiteSpace(slug))
            {
                return NormalizeNullable(vitrineUrl);
            }

            var path = $"/garagem/vitrine/{slug}";
            var origin = ResolvePreferredOrigin(publicBaseUrl, vitrineUrl);
            if (!string.IsNullOrWhiteSpace(origin))
            {
                return $"{origin}{path}";
            }

            return path;
        }

        internal static string? BuildCanonicalVehicleUrl(string? vitrineUrl, string? estabelecimentoSlug, string? veiculoSlug, string? publicBaseUrl = null)
        {
            var slugLoja = ResolveCanonicalStoreSlug(vitrineUrl, estabelecimentoSlug);
            var slugVeiculo = NormalizeNullable(veiculoSlug);
            if (string.IsNullOrWhiteSpace(slugVeiculo))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(slugLoja))
            {
                var lojaUrl = BuildCanonicalStoreUrl(vitrineUrl, slugLoja, publicBaseUrl);
                return string.IsNullOrWhiteSpace(lojaUrl)
                    ? null
                    : $"{lojaUrl.TrimEnd('/')}/{slugVeiculo}";
            }

            var vitrineLegada = NormalizeNullable(vitrineUrl);
            return string.IsNullOrWhiteSpace(vitrineLegada)
                ? null
                : $"{vitrineLegada}{(vitrineLegada.Contains('?') ? "&" : "?")}v={slugVeiculo}";
        }

        internal static string BuildMensagemSelecaoVeiculo(string veiculoTitulo, string? veiculoUrl, string proximaPergunta)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Otimo! Selecionei o *{veiculoTitulo}* para voce 😊");

            if (!string.IsNullOrWhiteSpace(veiculoUrl))
            {
                sb.AppendLine();
                sb.AppendLine($"Veja o carro aqui: {veiculoUrl}");
            }

            sb.AppendLine();
            sb.Append(proximaPergunta);
            return sb.ToString().Trim();
        }

        private static string DeterminarProximaEtapaAposCatalogo(string? objetivo)
        {
            return string.Equals(objetivo, ObjetivoTrocar, StringComparison.Ordinal)
                ? EtapaTrocaModeloDesejado
                : EtapaModelo;
        }

        private static bool TemVeiculoSelecionado(GarageLead lead)
        {
            return lead.IdVeiculoSelecionado.HasValue || !string.IsNullOrWhiteSpace(lead.VeiculoSelecionadoTitulo);
        }

        private static string? ResolveCanonicalStoreSlug(string? vitrineUrl, string? estabelecimentoSlug)
        {
            var slugInformado = NormalizeNullable(estabelecimentoSlug);
            if (!string.IsNullOrWhiteSpace(slugInformado))
            {
                return slugInformado;
            }

            return ExtractStoreSlugFromUrl(vitrineUrl);
        }

        private static string? ExtractStoreSlugFromUrl(string? vitrineUrl)
        {
            var normalizedUrl = NormalizeNullable(vitrineUrl);
            if (string.IsNullOrWhiteSpace(normalizedUrl))
            {
                return null;
            }

            string path;
            string? query;

            if (Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var absolute))
            {
                path = absolute.AbsolutePath;
                query = absolute.Query;
            }
            else
            {
                var fragmentIndex = normalizedUrl.IndexOf('#');
                if (fragmentIndex >= 0)
                {
                    normalizedUrl = normalizedUrl[..fragmentIndex];
                }

                var queryIndex = normalizedUrl.IndexOf('?');
                if (queryIndex >= 0)
                {
                    path = normalizedUrl[..queryIndex];
                    query = normalizedUrl[queryIndex..];
                }
                else
                {
                    path = normalizedUrl;
                    query = null;
                }
            }

            var slugFromPath = ExtractStoreSlugFromPath(path);
            if (!string.IsNullOrWhiteSpace(slugFromPath))
            {
                return slugFromPath;
            }

            var slugFromQuery = ExtractQueryValue(query, "estabelecimentoSlug");
            if (!string.IsNullOrWhiteSpace(slugFromQuery))
            {
                return slugFromQuery;
            }

            var legacyStoreToken = ExtractQueryValue(query, "estabelecimentoId");
            if (!string.IsNullOrWhiteSpace(legacyStoreToken) &&
                !Guid.TryParse(legacyStoreToken, out _))
            {
                return legacyStoreToken;
            }

            return null;
        }

        private static string? ExtractStoreSlugFromPath(string? path)
        {
            var normalizedPath = NormalizeNullable(path);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return null;
            }

            var segments = normalizedPath
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);

            for (var i = 0; i < segments.Length; i++)
            {
                if (!string.Equals(segments[i], "vitrine", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (i + 1 >= segments.Length)
                {
                    return null;
                }

                return Uri.UnescapeDataString(segments[i + 1]).Trim();
            }

            return null;
        }

        private static string? ExtractQueryValue(string? query, string key)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var pairs = query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries);

            foreach (var pair in pairs)
            {
                var separatorIndex = pair.IndexOf('=');
                var rawKey = separatorIndex >= 0 ? pair[..separatorIndex] : pair;
                if (!string.Equals(Uri.UnescapeDataString(rawKey), key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rawValue = separatorIndex >= 0 ? pair[(separatorIndex + 1)..] : string.Empty;
                var value = Uri.UnescapeDataString(rawValue).Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            return null;
        }

        private static string? ExtractAbsoluteOrigin(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            {
                return null;
            }

            return $"{absolute.Scheme}://{absolute.Authority}";
        }

        private static string? ResolvePreferredOrigin(string? publicBaseUrl, string? fallbackUrl)
        {
            var preferred = NormalizeBaseUrl(publicBaseUrl);
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                return preferred;
            }

            return ExtractAbsoluteOrigin(fallbackUrl);
        }

        private static string? NormalizeBaseUrl(string? value)
        {
            var normalized = NormalizeNullable(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var absolute))
            {
                return null;
            }

            return $"{absolute.Scheme}://{absolute.Authority}";
        }

        private static string? NormalizeNullable(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('/');
        }

        private static string ExtrairPrimeiroNome(string? nomeCompleto)
        {
            if (string.IsNullOrWhiteSpace(nomeCompleto))
            {
                return "cliente";
            }

            return nomeCompleto
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "cliente";
        }
    }
}
