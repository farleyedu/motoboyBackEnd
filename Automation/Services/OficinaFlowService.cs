using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class OficinaFlowService
    {
        public const string EstadoAguardandoNome = "oficina_aguardando_nome";
        public const string EstadoAguardandoMotivoHandover = "oficina_aguardando_motivo_handover";
        public const string EstadoAguardandoTipoAtualizacao = "oficina_aguardando_tipo_atualizacao";

        private const string ChaveAtendimentoId = "oficina_atendimento_id";
        private const string ChaveViaNumeroCentral = "oficina_via_numero_central";
        private const string DefaultNomeEstabelecimento = "Citrocar";

        private static readonly Regex EspacosRegex = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex PrefixoNomeRegex = new(
            @"^(meu nome e|me chamo|pode me chamar de|sou o|sou a|sou)\s+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly IConversationRepository _conversationRepository;
        private readonly IClienteRepository _clienteRepository;
        private readonly IEstabelecimentoRepository _estabelecimentoRepository;
        private readonly IOficinaAtendimentoRepository _oficinaAtendimentoRepository;
        private readonly CentralRoutingService _centralRouting;
        private readonly ILogger<OficinaFlowService> _logger;

        public OficinaFlowService(
            IConversationRepository conversationRepository,
            IClienteRepository clienteRepository,
            IEstabelecimentoRepository estabelecimentoRepository,
            IOficinaAtendimentoRepository oficinaAtendimentoRepository,
            CentralRoutingService centralRouting,
            ILogger<OficinaFlowService> logger)
        {
            _conversationRepository = conversationRepository;
            _clienteRepository = clienteRepository;
            _estabelecimentoRepository = estabelecimentoRepository;
            _oficinaAtendimentoRepository = oficinaAtendimentoRepository;
            _centralRouting = centralRouting;
            _logger = logger;
        }

        public async Task<(bool Intercepted, AssistantDecision? Decision)> TryHandleAsync(
            Guid idConversa,
            string mensagemTexto,
            string? phoneNumberDisplay)
        {
            var scope = await _centralRouting.ResolveEffectiveScopeAsync(idConversa);
            if (scope == null || !await IsOficinaEstabelecimentoAsync(scope.IdEstabelecimento))
            {
                return (false, null);
            }

            if (scope.IdCliente == Guid.Empty)
            {
                _logger.LogWarning("[Conversa={Conversa}] Fluxo oficina sem id_cliente resolvido", idConversa);
                return (false, null);
            }

            var contextoAtual = await _conversationRepository.ObterContextoAsync(idConversa);
            var cliente = await _clienteRepository.ObterPorIdAsync(scope.IdCliente);
            var nomeEstabelecimento = await ObterNomeEstabelecimentoAsync(scope.IdEstabelecimento);
            var viaNumeroCentral = _centralRouting.IsCentralDisplayPhone(phoneNumberDisplay);

            if (string.Equals(contextoAtual?.Estado, EstadoAguardandoNome, StringComparison.OrdinalIgnoreCase))
            {
                return (true, await ProcessarNomeAsync(
                    idConversa,
                    scope.IdCliente,
                    scope.IdEstabelecimento,
                    nomeEstabelecimento,
                    mensagemTexto));
            }

            if (EhSaudacaoInicial(mensagemTexto))
            {
                await SalvarContextoAsync(idConversa, EstadoAguardandoNome, viaNumeroCentral, null);
                return (true, CriarRespostaJsonDecision(CriarSaudacaoInicial(nomeEstabelecimento)));
            }

            if (string.IsNullOrWhiteSpace(cliente?.Nome))
            {
                await SalvarContextoAsync(idConversa, EstadoAguardandoNome, viaNumeroCentral, null);

                _logger.LogInformation(
                    "[Conversa={Conversa}] Fluxo oficina iniciou captura de nome para estabelecimento {Estabelecimento}",
                    idConversa,
                    scope.IdEstabelecimento);

                return (true, CriarRespostaJsonDecision(CriarSaudacaoInicial(nomeEstabelecimento)));
            }

            if (string.Equals(contextoAtual?.Estado, EstadoAguardandoMotivoHandover, StringComparison.OrdinalIgnoreCase))
            {
                var decision = await ProcessarMotivoHandoverAsync(
                    idConversa,
                    scope,
                    cliente,
                    nomeEstabelecimento,
                    mensagemTexto,
                    viaNumeroCentral,
                    contextoAtual);

                return (true, decision);
            }

            if (string.Equals(contextoAtual?.Estado, EstadoAguardandoTipoAtualizacao, StringComparison.OrdinalIgnoreCase))
            {
                var decision = await ProcessarTipoAtualizacaoAsync(
                    idConversa,
                    scope,
                    cliente,
                    nomeEstabelecimento,
                    mensagemTexto,
                    viaNumeroCentral,
                    contextoAtual);

                return (true, decision);
            }

            var classificacao = ClassificarMensagem(mensagemTexto);
            if (!classificacao.Interceptar)
            {
                return (false, null);
            }

            if (classificacao.PedirMotivoHumano)
            {
                await SalvarContextoAsync(idConversa, EstadoAguardandoMotivoHandover, viaNumeroCentral, null);
                return (true, CriarRespostaJsonDecision(
                    $"{MontarTratamentoCliente(cliente?.Nome)} Me diz em uma frase o motivo do atendimento que eu encaminho para a equipe da {nomeEstabelecimento}."));
            }

            if (classificacao.PedirTipoAtualizacao)
            {
                await SalvarContextoAsync(idConversa, EstadoAguardandoTipoAtualizacao, viaNumeroCentral, null);
                return (true, CriarRespostaJsonDecision(
                    $"{MontarTratamentoCliente(cliente?.Nome)} E sobre um servico em andamento ou sobre um agendamento?"));
            }

            var resumo = MontarResumoAtendimento(classificacao.IntencaoPrincipal, classificacao.IntencaoDetalhe, cliente?.Nome, mensagemTexto);
            var atendimento = await ObterOuCriarAtendimentoAsync(
                idConversa,
                scope,
                cliente,
                classificacao.IntencaoPrincipal,
                classificacao.IntencaoDetalhe,
                resumo,
                "aguardando_interno",
                etapaAtual: "encaminhado",
                ultimaPergunta: null,
                viaNumeroCentral,
                CriarExtras(
                    mensagemTexto,
                    classificacao.OrigemHandover,
                    classificacao.IntencaoDetalhe,
                    assuntoServico: classificacao.AssuntoServico));

            await _conversationRepository.LimparContextoAsync(idConversa);

            var detalhes = await CriarDetalhesHandoverAsync(
                idConversa,
                scope,
                cliente,
                classificacao.IntencaoPrincipal,
                classificacao.IntencaoDetalhe,
                resumo,
                mensagemTexto);

            if (atendimento != null)
            {
                detalhes.Historico = (detalhes.Historico ?? Array.Empty<string>())
                    .Concat(new[] { $"Atendimento oficina: {atendimento.Id}" })
                    .ToList();
            }

            return (true, CriarRespostaJsonDecision(
                $"{MontarTratamentoCliente(cliente?.Nome)} Nossa equipe da {nomeEstabelecimento} vai continuar seu atendimento por aqui.",
                handoverAction: "escalar_para_humano",
                detalhes: detalhes));
        }

        public async Task<bool> IsOficinaEstabelecimentoAsync(Guid idEstabelecimento)
        {
            var tipo = await _estabelecimentoRepository.ObterTipoEstabelecimentoAsync(idEstabelecimento);
            return string.Equals(tipo, "oficina", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<AssistantDecision> ProcessarNomeAsync(
            Guid idConversa,
            Guid idCliente,
            Guid idEstabelecimento,
            string nomeEstabelecimento,
            string mensagemTexto)
        {
            if (!TryExtrairNomeValido(mensagemTexto, out var nome))
            {
                return CriarRespostaJsonDecision(
                    $"Nao consegui identificar seu nome ainda. Me responde so com seu nome para eu continuar o atendimento da {nomeEstabelecimento}, por favor.");
            }

            var atualizado = await _clienteRepository.AtualizarNomeAsync(idCliente, idEstabelecimento, nome);
            if (!atualizado)
            {
                _logger.LogWarning(
                    "[Conversa={Conversa}] Nao foi possivel atualizar nome do cliente {Cliente} no estabelecimento {Estabelecimento}",
                    idConversa,
                    idCliente,
                    idEstabelecimento);

                return CriarRespostaJsonDecision("Nao consegui registrar seu nome agora. Me manda novamente so o seu nome, por favor.");
            }

            await _conversationRepository.LimparContextoAsync(idConversa);

            _logger.LogInformation(
                "[Conversa={Conversa}] Nome do cliente registrado no fluxo oficina: {Nome}",
                idConversa,
                nome);

            return CriarRespostaJsonDecision(
                $"Perfeito, {nome}! Vou continuar seu atendimento por aqui na {nomeEstabelecimento}. Como posso te ajudar?");
        }

        private async Task<AssistantDecision> ProcessarMotivoHandoverAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            string nomeEstabelecimento,
            string mensagemTexto,
            bool viaNumeroCentral,
            ConversationContext? contextoAtual)
        {
            if (!TryExtrairMotivoHandover(mensagemTexto, out var motivo))
            {
                if (FoiPedidoHumano(mensagemTexto))
                {
                    motivo = "cliente solicitou atendimento humano sem detalhar";
                }
                else
                {
                    await SalvarContextoAsync(idConversa, EstadoAguardandoMotivoHandover, viaNumeroCentral, ObterAtendimentoId(contextoAtual));
                    return CriarRespostaJsonDecision("Me fala rapidinho o motivo do atendimento em uma frase para eu encaminhar certinho.");
                }
            }

            var resumo = MontarResumoAtendimento("falar_com_humano", "pedido_explicito", cliente?.Nome, motivo);
            var atendimento = await ObterOuCriarAtendimentoAsync(
                idConversa,
                scope,
                cliente,
                "falar_com_humano",
                "pedido_explicito",
                resumo,
                "aguardando_interno",
                etapaAtual: "encaminhado",
                ultimaPergunta: null,
                viaNumeroCentral,
                CriarExtras(motivo, "pedido_explicito_humano", null));

            await _conversationRepository.LimparContextoAsync(idConversa);

            var detalhes = await CriarDetalhesHandoverAsync(
                idConversa,
                scope,
                cliente,
                "falar_com_humano",
                "pedido_explicito",
                resumo,
                motivo);

            if (atendimento != null)
            {
                detalhes.Historico = (detalhes.Historico ?? Array.Empty<string>())
                    .Concat(new[] { $"Atendimento oficina: {atendimento.Id}" })
                    .ToList();
            }

            return CriarRespostaJsonDecision(
                $"{MontarTratamentoCliente(cliente?.Nome)} Nossa equipe da {nomeEstabelecimento} vai continuar seu atendimento por aqui.",
                handoverAction: "escalar_para_humano",
                detalhes: detalhes);
        }

        private async Task<AssistantDecision> ProcessarTipoAtualizacaoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            string nomeEstabelecimento,
            string mensagemTexto,
            bool viaNumeroCentral,
            ConversationContext? contextoAtual)
        {
            var subtipo = InferirTipoAtualizacaoDireta(mensagemTexto) ?? InferirTipoAtualizacao(mensagemTexto);
            if (subtipo == null)
            {
                await SalvarContextoAsync(idConversa, EstadoAguardandoTipoAtualizacao, viaNumeroCentral, ObterAtendimentoId(contextoAtual));
                return CriarRespostaJsonDecision("Pode me confirmar se e sobre um servico em andamento ou sobre um agendamento?");
            }

            var resumo = MontarResumoAtendimento("atualizacao", subtipo, cliente?.Nome, mensagemTexto);
            var atendimento = await ObterOuCriarAtendimentoAsync(
                idConversa,
                scope,
                cliente,
                "atualizacao",
                subtipo,
                resumo,
                "aguardando_interno",
                etapaAtual: "encaminhado",
                ultimaPergunta: null,
                viaNumeroCentral,
                CriarExtras(mensagemTexto, "atualizacao", subtipo));

            await _conversationRepository.LimparContextoAsync(idConversa);

            var detalhes = await CriarDetalhesHandoverAsync(
                idConversa,
                scope,
                cliente,
                "atualizacao",
                subtipo,
                resumo,
                mensagemTexto);

            if (atendimento != null)
            {
                detalhes.Historico = (detalhes.Historico ?? Array.Empty<string>())
                    .Concat(new[] { $"Atendimento oficina: {atendimento.Id}" })
                    .ToList();
            }

            return CriarRespostaJsonDecision(
                $"{MontarTratamentoCliente(cliente?.Nome)} Nossa equipe da {nomeEstabelecimento} vai continuar seu atendimento por aqui.",
                handoverAction: "escalar_para_humano",
                detalhes: detalhes);
        }

        private async Task SalvarContextoAsync(
            Guid idConversa,
            string estado,
            bool viaNumeroCentral,
            Guid? atendimentoId)
        {
            var dados = new Dictionary<string, object>
            {
                [ChaveViaNumeroCentral] = viaNumeroCentral
            };

            if (atendimentoId.HasValue && atendimentoId.Value != Guid.Empty)
            {
                dados[ChaveAtendimentoId] = atendimentoId.Value.ToString();
            }

            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = estado,
                DadosColetados = dados,
                ExpiracaoEstado = null
            });
        }

        private async Task<OficinaAtendimento?> ObterOuCriarAtendimentoAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            string intencaoPrincipal,
            string? intencaoDetalhe,
            string resumoAtendimento,
            string status,
            string etapaAtual,
            string? ultimaPergunta,
            bool viaNumeroCentral,
            IReadOnlyDictionary<string, object?> extras)
        {
            var telefoneCliente = await GarantirTelefoneClienteAsync(idConversa, scope);
            var atendimento = await _oficinaAtendimentoRepository.ObterPorConversaAsync(idConversa);

            if (atendimento == null && !string.IsNullOrWhiteSpace(telefoneCliente))
            {
                atendimento = await _oficinaAtendimentoRepository.ObterAbertoAsync(scope.IdEstabelecimento, telefoneCliente);
            }

            var perfilContato = InferirPerfilContato(intencaoPrincipal, intencaoDetalhe, resumoAtendimento);

            if (atendimento == null)
            {
                atendimento = new OficinaAtendimento
                {
                    Id = Guid.NewGuid(),
                    IdEstabelecimento = scope.IdEstabelecimento,
                    IdConversa = idConversa,
                    IdCliente = scope.IdCliente,
                    TelefoneE164 = telefoneCliente ?? string.Empty,
                    NomeCliente = cliente?.Nome?.Trim(),
                    PerfilContato = perfilContato,
                    IntencaoPrincipal = intencaoPrincipal,
                    IntencaoDetalhe = intencaoDetalhe,
                    ResumoAtendimento = resumoAtendimento,
                    Status = status,
                    EtapaAtual = etapaAtual,
                    UltimaPergunta = ultimaPergunta,
                    ViaNumeroCentral = viaNumeroCentral,
                    DadosExtras = new Dictionary<string, object?>(extras, StringComparer.OrdinalIgnoreCase),
                    DataHandover = string.Equals(status, "aguardando_interno", StringComparison.OrdinalIgnoreCase) ? DateTime.UtcNow : null,
                    DataCriacao = DateTime.UtcNow,
                    DataAtualizacao = DateTime.UtcNow
                };

                await _oficinaAtendimentoRepository.CriarAsync(atendimento);
                return atendimento;
            }

            atendimento.IdConversa = idConversa;
            atendimento.IdCliente = scope.IdCliente;
            atendimento.IdEstabelecimento = scope.IdEstabelecimento;
            atendimento.TelefoneE164 = string.IsNullOrWhiteSpace(telefoneCliente) ? atendimento.TelefoneE164 : telefoneCliente!;
            atendimento.NomeCliente = string.IsNullOrWhiteSpace(cliente?.Nome) ? atendimento.NomeCliente : cliente!.Nome!.Trim();
            atendimento.PerfilContato = AtualizarPerfilContato(atendimento.PerfilContato, perfilContato);
            atendimento.IntencaoPrincipal = intencaoPrincipal;
            atendimento.IntencaoDetalhe = intencaoDetalhe;
            atendimento.ResumoAtendimento = resumoAtendimento;
            atendimento.Status = status;
            atendimento.EtapaAtual = etapaAtual;
            atendimento.UltimaPergunta = ultimaPergunta;
            atendimento.ViaNumeroCentral = viaNumeroCentral;
            atendimento.DataHandover = string.Equals(status, "aguardando_interno", StringComparison.OrdinalIgnoreCase)
                ? atendimento.DataHandover ?? DateTime.UtcNow
                : atendimento.DataHandover;

            foreach (var extra in extras)
            {
                atendimento.DadosExtras[extra.Key] = extra.Value;
            }

            await _oficinaAtendimentoRepository.AtualizarAsync(atendimento);
            return atendimento;
        }

        private async Task<string?> GarantirTelefoneClienteAsync(Guid idConversa, EffectiveConversationScope scope)
        {
            if (!string.IsNullOrWhiteSpace(scope.TelefoneCliente))
            {
                return scope.TelefoneCliente;
            }

            var telefoneCliente = await _clienteRepository.ObterTelefoneClienteAsync(scope.IdCliente, scope.IdEstabelecimento);
            if (!string.IsNullOrWhiteSpace(telefoneCliente))
            {
                return telefoneCliente;
            }

            var conversa = await _conversationRepository.ObterPorIdAsync(idConversa);
            return conversa?.TelefoneCliente;
        }

        private async Task<HandoverContextDto> CriarDetalhesHandoverAsync(
            Guid idConversa,
            EffectiveConversationScope scope,
            Cliente? cliente,
            string intencaoPrincipal,
            string? intencaoDetalhe,
            string resumo,
            string motivoTexto)
        {
            var telefoneCliente = await GarantirTelefoneClienteAsync(idConversa, scope);
            return new HandoverContextDto
            {
                ClienteNome = cliente?.Nome,
                Telefone = telefoneCliente,
                Motivo = resumo,
                QueixaPrincipal = motivoTexto,
                Contexto = $"intencao={intencaoPrincipal}; detalhe={intencaoDetalhe ?? "nenhum"}",
                Historico = new[]
                {
                    $"Resumo: {resumo}",
                    $"Motivo: {motivoTexto}"
                }
            };
        }

        private async Task<string> ObterNomeEstabelecimentoAsync(Guid idEstabelecimento)
        {
            var nome = await _estabelecimentoRepository.ObterNomeFantasiaAsync(idEstabelecimento);
            return string.IsNullOrWhiteSpace(nome) ? DefaultNomeEstabelecimento : nome.Trim();
        }

        private static AssistantDecision CriarRespostaJsonDecision(
            string reply,
            string handoverAction = "none",
            HandoverContextDto? detalhes = null)
        {
            return new AssistantDecision(
                JsonSerializer.Serialize(new { acao = "responder", reply }),
                handoverAction,
                null,
                false,
                detalhes,
                null);
        }

        private static string CriarSaudacaoInicial(string nomeEstabelecimento)
        {
            if (string.Equals(nomeEstabelecimento, "Citrocar", StringComparison.OrdinalIgnoreCase))
            {
                return "Ola! Voce esta falando com a Citrocar. Como posso ajuda-lo hoje?";
            }

            return $"Ola! Voce esta falando com a equipe da {nomeEstabelecimento}. Como posso te ajudar hoje?";
        }

        private static ClassificacaoMensagem ClassificarMensagem(string? mensagemTexto)
        {
            var textoNormalizado = NormalizeText(mensagemTexto);
            if (string.IsNullOrWhiteSpace(textoNormalizado))
            {
                return ClassificacaoMensagem.Ignorar();
            }

            if (FoiPedidoHumano(textoNormalizado))
            {
                var pedirMotivo = !TemMotivoExplicitoNoPedidoHumano(textoNormalizado);
                return new ClassificacaoMensagem(true, "falar_com_humano", "pedido_explicito", pedirMotivo, false, "pedido_explicito_humano", null);
            }

            var tipoAtualizacao = InferirTipoAtualizacao(textoNormalizado);
            if (tipoAtualizacao != null)
            {
                return new ClassificacaoMensagem(true, "atualizacao", tipoAtualizacao, false, false, "atualizacao", null);
            }

            if (ParecePedidoAtualizacao(textoNormalizado))
            {
                return new ClassificacaoMensagem(true, "atualizacao", null, false, true, "atualizacao", null);
            }

            var detalheAgendamento = InferirAcaoAgendamento(textoNormalizado);
            if (detalheAgendamento != null)
            {
                return new ClassificacaoMensagem(true, "informacao_agendamento", detalheAgendamento, false, false, "agendamento", null);
            }

            var detalheComercial = InferirTemaComercial(textoNormalizado);
            if (detalheComercial != null)
            {
                var intencao = detalheComercial is "diagnostico" or "prazo"
                    ? "duvida_geral"
                    : "orcamento_comercial";

                return new ClassificacaoMensagem(true, intencao, detalheComercial, false, false, detalheComercial, null);
            }

            return ClassificacaoMensagem.Ignorar();
        }

        private static string? InferirTipoAtualizacao(string texto)
        {
            var normalizado = NormalizeText(texto);
            if (string.IsNullOrWhiteSpace(normalizado) || !ParecePedidoAtualizacao(normalizado))
            {
                return null;
            }

            return InferirTipoAtualizacaoDireta(normalizado);
        }

        private static string? InferirTipoAtualizacaoDireta(string texto)
        {
            var normalizado = NormalizeText(texto);

            if (ContemAlgum(normalizado, "agendamento", "marcacao", "remarcar", "remarcacao", "confirmar horario", "confirmacao de horario"))
            {
                return "agendamento";
            }

            if (ContemAlgum(normalizado, "servico", "conserto", "manutencao", "reparo", "veiculo", "carro", "orcam", "oficina"))
            {
                return "servico_em_andamento";
            }

            if (Regex.IsMatch(normalizado, @"\bpronto\b", RegexOptions.CultureInvariant))
            {
                return "servico_em_andamento";
            }

            return null;
        }

        private static bool ParecePedidoAtualizacao(string texto)
        {
            var normalizado = NormalizeText(texto);
            return ContemAlgum(
                normalizado,
                "atualizacao",
                "acompanhar",
                "andamento",
                "status",
                "ja ficou pronto",
                "ja esta pronto",
                "como esta",
                "como ta",
                "quero saber do meu",
                "retorno do meu",
                "noticia do meu",
                "noticias do meu");
        }

        private static string? InferirAcaoAgendamento(string texto)
        {
            var normalizado = NormalizeText(texto);
            if (!ContemAlgum(normalizado, "agend", "marc", "remarc", "confirm"))
            {
                return null;
            }

            if (EhFaqAgendamento(normalizado))
            {
                return null;
            }

            if (ContemAlgum(normalizado, "remarcar", "alterar agendamento", "mudar agendamento"))
            {
                return "alterar_agendamento";
            }

            if (ContemAlgum(normalizado, "confirmar agendamento", "confirmar horario"))
            {
                return "confirmar_agendamento";
            }

            if (ContemAlgum(normalizado, "cancelar agendamento", "desmarcar"))
            {
                return "cancelar_agendamento";
            }

            if (ContemAlgum(normalizado, "quero agendar", "gostaria de agendar", "preciso agendar", "quero marcar", "gostaria de marcar", "marcar horario", "agendar horario"))
            {
                return "solicitar_agendamento";
            }

            if (Regex.IsMatch(normalizado, @"\b(agendar|marcar)\b", RegexOptions.CultureInvariant))
            {
                return "solicitar_agendamento";
            }

            return null;
        }

        private static bool EhFaqAgendamento(string texto)
        {
            var normalizado = NormalizeText(texto);
            if (!ContemAlgum(normalizado, "agend", "horario", "marc"))
            {
                return false;
            }

            return ContemAlgum(
                normalizado,
                "como funciona",
                "voces agendam",
                "tem horario",
                "quais horarios",
                "quais os horarios",
                "qual horario",
                "qual o horario",
                "como agenda",
                "como faco para agendar",
                "fazem agendamento");
        }

        private static string? InferirTemaComercial(string texto)
        {
            var normalizado = NormalizeText(texto);

            if (ContemAlgum(normalizado, "diagnostico", "o que pode ser", "qual pode ser o problema", "avaliar meu carro", "avaliacao tecnica"))
            {
                return "diagnostico";
            }

            if (ContemAlgum(normalizado, "prazo", "quando fica pronto", "quanto tempo demora"))
            {
                return "prazo";
            }

            if (ContemAlgum(normalizado, "orcamento", "preco", "valor", "quanto custa", "cotacao"))
            {
                return "preco";
            }

            return null;
        }

        private static bool FoiPedidoHumano(string? texto)
        {
            var normalizado = NormalizeText(texto);
            return ContemAlgum(
                normalizado,
                "falar com humano",
                "falar com atendente",
                "falar com uma pessoa",
                "falar com alguem",
                "quero um atendente",
                "quero atendimento humano",
                "atendimento humano",
                "atendente humano",
                "atendente",
                "humano",
                "uma pessoa",
                "me passa para atendente");
        }

        private static bool TemMotivoExplicitoNoPedidoHumano(string? texto)
        {
            var normalizado = NormalizeText(texto);
            if (!FoiPedidoHumano(normalizado))
            {
                return false;
            }

            return ContemAlgum(normalizado, "sobre", "porque", "pois", "referente", "quero falar com atendente sobre")
                || normalizado.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 7;
        }

        private static bool TryExtrairMotivoHandover(string? mensagemTexto, out string motivo)
        {
            motivo = string.Empty;
            if (string.IsNullOrWhiteSpace(mensagemTexto))
            {
                return false;
            }

            var textoOriginal = EspacosRegex.Replace(mensagemTexto.Trim(), " ");
            var normalizado = NormalizeText(textoOriginal);

            if (textoOriginal.Length < 5 || FoiPedidoHumano(normalizado))
            {
                return false;
            }

            if (normalizado is "oi" or "ola" or "por favor" or "urgente")
            {
                return false;
            }

            motivo = textoOriginal.Trim(' ', '.', ',', ';', ':', '!', '?');
            return motivo.Length >= 5;
        }

        private static string MontarResumoAtendimento(string intencaoPrincipal, string? intencaoDetalhe, string? nomeCliente, string mensagemTexto)
        {
            var nome = string.IsNullOrWhiteSpace(nomeCliente) ? "Cliente" : nomeCliente.Trim();
            var contexto = mensagemTexto.Trim();

            return intencaoPrincipal switch
            {
                "falar_com_humano" when contexto.Contains("sem detalhar", StringComparison.OrdinalIgnoreCase)
                    => $"{nome} solicitou atendimento humano sem detalhar.",
                "falar_com_humano"
                    => $"{nome} solicitou atendimento humano. Motivo: {contexto}.",
                "atualizacao" when string.Equals(intencaoDetalhe, "agendamento", StringComparison.OrdinalIgnoreCase)
                    => $"{nome} pediu atualizacao de agendamento. Contexto: {contexto}.",
                "atualizacao"
                    => $"{nome} pediu atualizacao de servico em andamento. Contexto: {contexto}.",
                "informacao_agendamento"
                    => $"{nome} pediu ajuda com agendamento. Contexto: {contexto}.",
                "duvida_geral" when string.Equals(intencaoDetalhe, "diagnostico", StringComparison.OrdinalIgnoreCase)
                    => $"{nome} pediu apoio da equipe para orientacao sobre diagnostico. Contexto: {contexto}.",
                "duvida_geral" when string.Equals(intencaoDetalhe, "prazo", StringComparison.OrdinalIgnoreCase)
                    => $"{nome} pediu apoio da equipe para informacao de prazo. Contexto: {contexto}.",
                "orcamento_comercial"
                    => $"{nome} pediu informacao comercial. Contexto: {contexto}.",
                _
                    => $"{nome} pediu continuidade com a equipe. Contexto: {contexto}."
            };
        }

        private static string InferirPerfilContato(string intencaoPrincipal, string? intencaoDetalhe, string textoBase)
        {
            var normalizado = NormalizeText(textoBase);
            if (ContemAlgum(normalizado, "cliente novo", "sou novo cliente", "primeira vez", "nunca fui"))
            {
                return "novo";
            }

            if (string.Equals(intencaoPrincipal, "atualizacao", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(intencaoDetalhe, "alterar_agendamento", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(intencaoDetalhe, "confirmar_agendamento", StringComparison.OrdinalIgnoreCase) ||
                ContemAlgum(normalizado, "meu agendamento", "meu servico", "meu carro", "retorno"))
            {
                return "recorrente";
            }

            return "indefinido";
        }

        private static string AtualizarPerfilContato(string atual, string novo)
        {
            if (string.Equals(atual, "novo", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(atual, "recorrente", StringComparison.OrdinalIgnoreCase))
            {
                return atual;
            }

            return novo;
        }

        private static IReadOnlyDictionary<string, object?> CriarExtras(
            string motivoTexto,
            string origemHandover,
            string? subtipoAtualizacao,
            string? assuntoServico = null)
        {
            var extras = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["motivo_texto"] = motivoTexto,
                ["origem_handover"] = origemHandover
            };

            if (!string.IsNullOrWhiteSpace(subtipoAtualizacao))
            {
                extras["subtipo_atualizacao"] = subtipoAtualizacao;
            }

            if (!string.IsNullOrWhiteSpace(assuntoServico))
            {
                extras["assunto_servico"] = assuntoServico;
            }

            return extras;
        }

        private static Guid? ObterAtendimentoId(ConversationContext? contexto)
        {
            return CentralRoutingService.GetGuidValue(contexto?.DadosColetados, ChaveAtendimentoId);
        }

        private static string MontarTratamentoCliente(string? nomeCliente)
        {
            if (string.IsNullOrWhiteSpace(nomeCliente))
            {
                return "Entendi.";
            }

            var primeiroNome = nomeCliente.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrWhiteSpace(primeiroNome) ? "Entendi." : $"Entendi, {primeiroNome}.";
        }

        private static bool TryExtrairNomeValido(string? mensagemTexto, out string nome)
        {
            nome = string.Empty;
            if (string.IsNullOrWhiteSpace(mensagemTexto))
            {
                return false;
            }

            var texto = EspacosRegex.Replace(mensagemTexto.Trim(), " ");
            texto = PrefixoNomeRegex.Replace(texto, string.Empty).Trim();
            texto = texto.Trim(' ', '.', ',', ';', ':', '!', '?', '"', '\'', '-', '_');

            if (texto.Length < 2 || texto.Length > 60)
            {
                return false;
            }

            if (texto.Any(char.IsDigit) || !texto.Any(char.IsLetter))
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

        private static string NormalizeText(string? texto)
        {
            if (string.IsNullOrWhiteSpace(texto))
            {
                return string.Empty;
            }

            var normalized = texto.Normalize(NormalizationForm.FormD);
            var chars = normalized.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark);
            return EspacosRegex.Replace(new string(chars.ToArray()).ToLowerInvariant(), " ").Trim();
        }

        private static bool ContemAlgum(string texto, params string[] termos)
        {
            return termos.Any(termo => texto.Contains(NormalizeText(termo), StringComparison.Ordinal));
        }

        private static bool EhSaudacaoInicial(string? texto)
        {
            var normalizado = NormalizeText(texto);
            if (string.IsNullOrWhiteSpace(normalizado))
            {
                return false;
            }

            return normalizado is "oi" or "ola" or "ola!" or "bom dia" or "boa tarde" or "boa noite" ||
                   ContemAlgum(normalizado, "ola tudo bem", "oi tudo bem", "ola, tudo bem", "oi, tudo bem");
        }

        private sealed record ClassificacaoMensagem(
            bool Interceptar,
            string IntencaoPrincipal,
            string? IntencaoDetalhe,
            bool PedirMotivoHumano,
            bool PedirTipoAtualizacao,
            string OrigemHandover,
            string? AssuntoServico)
        {
            public static ClassificacaoMensagem Ignorar()
                => new(false, "duvida_geral", null, false, false, "none", null);
        }
    }
}
