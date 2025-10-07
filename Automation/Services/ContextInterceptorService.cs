// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using APIBack.Automation.Dtos;
using APIBack.Automation.Helpers;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace APIBack.Automation.Services
{
    /// <summary>
    /// Serviço responsável por interceptar mensagens quando há contexto de conversa ativo
    /// (ex: escolha de reserva, alteração de dados, confirmação)
    /// </summary>
    public class ContextInterceptorService
    {
        private readonly IConversationRepository _conversationRepository;
        private readonly APIBack.Repository.Interface.IReservaRepository _reservaRepository;
        private readonly IClienteRepository _clienteRepository;
        private readonly ILogger<ContextInterceptorService> _logger;

        public ContextInterceptorService(
            IConversationRepository conversationRepository,
            APIBack.Repository.Interface.IReservaRepository reservaRepository,
            IClienteRepository clienteRepository,
            ILogger<ContextInterceptorService> logger)
        {
            _conversationRepository = conversationRepository;
            _reservaRepository = reservaRepository;
            _clienteRepository = clienteRepository;
            _logger = logger;
        }

        /// <summary>
        /// Verifica se há contexto ativo e intercepta a mensagem se necessário
        /// </summary>
        /// <returns>True se a mensagem foi interceptada e processada, False se deve seguir para IA</returns>
        public async Task<(bool Intercepted, AssistantDecision? Decision)> TryInterceptAsync(
            Guid idConversa,
            string mensagemTexto)
        {
            var contexto = await _conversationRepository.ObterContextoAsync(idConversa);

            if (contexto == null || string.IsNullOrWhiteSpace(contexto.Estado))
            {
                return (false, null);
            }

            // Verificar se contexto expirou
            if (contexto.ExpiracaoEstado.HasValue && contexto.ExpiracaoEstado.Value < DateTime.UtcNow)
            {
                _logger.LogInformation("[Conversa={Conversa}] Contexto expirado, limpando", idConversa);
                await _conversationRepository.LimparContextoAsync(idConversa);
                return (false, null);
            }

            switch (contexto.Estado)
            {
                case "aguardando_escolha_reserva":
                    return await ProcessarEscolhaReservaAsync(idConversa, mensagemTexto, contexto);

                case "aguardando_dados_alteracao":
                    return await ProcessarDadosAlteracaoAsync(idConversa, mensagemTexto, contexto);

                case "aguardando_confirmacao_alteracao":
                    return await ProcessarConfirmacaoAlteracaoAsync(idConversa, mensagemTexto, contexto);

                default:
                    _logger.LogWarning("[Conversa={Conversa}] Estado de contexto desconhecido: {Estado}",
                        idConversa, contexto.Estado);
                    return (false, null);
            }
        }

        private async Task<(bool, AssistantDecision?)> ProcessarEscolhaReservaAsync(
            Guid idConversa,
            string mensagemTexto,
            ConversationContext contexto)
        {
            var numeroEscolhido = ExtrairNumeroEscolha(mensagemTexto);

            if (!numeroEscolhido.HasValue)
            {
                // Não conseguiu extrair número, não intercepta (deixa IA processar)
                return (false, null);
            }

            if (contexto.DadosColetados == null ||
                !contexto.DadosColetados.TryGetValue("mapeamento_reservas", out var mapeamentoJson))
            {
                _logger.LogWarning("[Conversa={Conversa}] Mapeamento de reservas não encontrado no contexto", idConversa);
                return (false, null);
            }

            var mapeamento = JsonSerializer.Deserialize<Dictionary<int, long>>(mapeamentoJson.ToString()!);

            if (mapeamento == null || !mapeamento.TryGetValue(numeroEscolhido.Value, out var idReserva))
            {
                var reply = "Não encontrei essa opção. Pode me dizer o número da reserva? (1, 2, 3...) 😊";
                await SalvarMensagemRespostaAsync(idConversa, reply);
                return (true, new AssistantDecision(reply, "none", null, false, null, null));
            }

            // Buscar reserva completa
            var reserva = await _reservaRepository.BuscarPorIdAsync(idReserva);

            if (reserva == null)
            {
                _logger.LogWarning("[Conversa={Conversa}] Reserva {IdReserva} não encontrada", idConversa, idReserva);
                await _conversationRepository.LimparContextoAsync(idConversa);
                return (false, null);
            }

            // Atualizar contexto com reserva escolhida
            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = "aguardando_dados_alteracao",
                ReservaIdPendente = idReserva,
                DadosColetados = new Dictionary<string, object>
                {
                    { "reserva_id", idReserva },
                    { "data_atual", reserva.DataReserva.ToString("yyyy-MM-dd") },
                    { "hora_atual", reserva.HoraInicio.ToString(@"hh\:mm") },
                    { "qtd_atual", reserva.QtdPessoas ?? 0 }
                },
                ExpiracaoEstado = DateTime.UtcNow.AddMinutes(30)
            });

            // Montar resposta mostrando informações completas
            var cliente = await _clienteRepository.ObterPorIdAsync(reserva.IdCliente);
            var nomeCliente = cliente?.Nome ?? "Cliente";

            var msg = new StringBuilder();
            msg.AppendLine($"📋 Reserva #{reserva.Id} - Informações completas:");
            msg.AppendLine();
            msg.AppendLine($"👤 Nome: {nomeCliente}");
            msg.AppendLine($"📅 Data: {reserva.DataReserva:dd/MM/yyyy} ({reserva.DataReserva:dddd})");
            msg.AppendLine($"⏰ Horário: {reserva.HoraInicio:hh\\:mm}");
            msg.AppendLine($"👥 Pessoas: {reserva.QtdPessoas}");
            msg.AppendLine($"🎫 Código: #{reserva.Id}");
            msg.AppendLine();
            msg.AppendLine("O que você quer alterar? 😊");
            msg.AppendLine("• Horário");
            msg.AppendLine("• Quantidade de pessoas");
            msg.AppendLine("• Data");

            var replyText = msg.ToString();
            await SalvarMensagemRespostaAsync(idConversa, replyText);

            return (true, new AssistantDecision(replyText, "none", null, false, null, null));
        }

        private async Task<(bool, AssistantDecision?)> ProcessarDadosAlteracaoAsync(
            Guid idConversa,
            string mensagemTexto,
            ConversationContext contexto)
        {
            // Extrair novos dados (horário, quantidade)
            var novoHorario = ExtrairHorario(mensagemTexto);
            var novaQtd = ExtrairQuantidade(mensagemTexto);

            if (novoHorario == null && !novaQtd.HasValue)
            {
                // Não conseguiu extrair dados, não intercepta (deixa IA processar)
                return (false, null);
            }

            long idReserva = contexto.ReservaIdPendente ?? 0;
            if (idReserva == 0)
            {
                _logger.LogWarning("[Conversa={Conversa}] ReservaIdPendente não encontrada no contexto", idConversa);
                await _conversationRepository.LimparContextoAsync(idConversa);
                return (false, null);
            }

            string horaAtual = contexto.DadosColetados?["hora_atual"]?.ToString() ?? "";
            int qtdAtual = int.Parse(contexto.DadosColetados?["qtd_atual"]?.ToString() ?? "0");
            string dataAtual = contexto.DadosColetados?["data_atual"]?.ToString() ?? "";

            var reserva = await _reservaRepository.BuscarPorIdAsync(idReserva);

            if (reserva == null)
            {
                _logger.LogWarning("[Conversa={Conversa}] Reserva {IdReserva} não encontrada", idConversa, idReserva);
                await _conversationRepository.LimparContextoAsync(idConversa);
                return (false, null);
            }

            // Montar mensagem de confirmação
            var msg = new StringBuilder();
            msg.AppendLine($"📋 Reserva #{idReserva} - Confirme as alterações:");
            msg.AppendLine();
            msg.AppendLine($"📅 Data: {reserva.DataReserva:dd/MM/yyyy} ({reserva.DataReserva:dddd})");
            msg.AppendLine();

            if (novoHorario != null)
            {
                msg.AppendLine("⏰ HORÁRIO:");
                msg.AppendLine($"❌ Antes: {horaAtual}");
                msg.AppendLine($"✅ Depois: {novoHorario}");
            }
            else
            {
                msg.AppendLine($"⏰ HORÁRIO:");
                msg.AppendLine($"✔️ Mantém: {horaAtual}");
            }

            msg.AppendLine();

            if (novaQtd.HasValue)
            {
                msg.AppendLine("👥 PESSOAS:");
                msg.AppendLine($"❌ Antes: {qtdAtual}");
                msg.AppendLine($"✅ Depois: {novaQtd}");
            }
            else
            {
                msg.AppendLine("👥 PESSOAS:");
                msg.AppendLine($"✔️ Mantém: {qtdAtual}");
            }

            msg.AppendLine();
            msg.Append("Confirma essas mudanças? 😊");

            // Atualizar contexto com dados da confirmação
            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = "aguardando_confirmacao_alteracao",
                ReservaIdPendente = idReserva,
                DadosColetados = new Dictionary<string, object>
                {
                    { "reserva_id", idReserva },
                    { "novo_horario", novoHorario ?? "" },
                    { "nova_qtd", novaQtd ?? 0 }
                },
                ExpiracaoEstado = DateTime.UtcNow.AddMinutes(10)
            });

            var reply = msg.ToString();
            await SalvarMensagemRespostaAsync(idConversa, reply);

            return (true, new AssistantDecision(reply, "none", null, false, null, null));
        }

        private async Task<(bool, AssistantDecision?)> ProcessarConfirmacaoAlteracaoAsync(
            Guid idConversa,
            string mensagemTexto,
            ConversationContext contexto)
        {
            var textoNorm = mensagemTexto.Trim().ToLower();

            if (textoNorm.Contains("sim") || textoNorm.Contains("confirma") ||
                textoNorm.Contains("pode") || textoNorm == "s")
            {
                // Executar atualização
                long idReserva = contexto.ReservaIdPendente ?? 0;
                string novoHorario = contexto.DadosColetados?["novo_horario"]?.ToString() ?? "";
                int novaQtd = int.Parse(contexto.DadosColetados?["nova_qtd"]?.ToString() ?? "0");

                var reserva = await _reservaRepository.BuscarPorIdAsync(idReserva);

                if (reserva != null)
                {
                    bool alterou = false;

                    if (!string.IsNullOrWhiteSpace(novoHorario) && TimeSpan.TryParseExact(novoHorario, @"hh\:mm", null, out var timeSpan))
                    {
                        reserva.HoraInicio = timeSpan;
                        alterou = true;
                    }

                    if (novaQtd > 0)
                    {
                        reserva.QtdPessoas = novaQtd;
                        alterou = true;
                    }

                    if (alterou)
                    {
                        reserva.DataAtualizacao = DateTime.UtcNow;
                        await _reservaRepository.AtualizarAsync(reserva);

                        var msg = new StringBuilder();
                        msg.AppendLine("✅ Reserva atualizada com sucesso! 🎉");
                        msg.AppendLine();
                        msg.AppendLine($"🎫 Código: #{reserva.Id}");
                        msg.AppendLine($"📅 Data: {reserva.DataReserva:dd/MM/yyyy}");
                        msg.AppendLine($"⏰ Horário: {reserva.HoraInicio:hh\\:mm}");
                        msg.AppendLine($"👥 Pessoas: {reserva.QtdPessoas}");
                        msg.AppendLine();
                        msg.Append("Nos vemos lá! ✨🥂");

                        await _conversationRepository.LimparContextoAsync(idConversa);

                        var reply = msg.ToString();
                        await SalvarMensagemRespostaAsync(idConversa, reply);

                        return (true, new AssistantDecision(reply, "none", null, false, null, null));
                    }
                }
            }
            else if (textoNorm.Contains("não") || textoNorm.Contains("nao") || textoNorm == "n")
            {
                await _conversationRepository.LimparContextoAsync(idConversa);
                var reply = "Tudo bem! Sua reserva permanece como estava. Se precisar de algo, estou aqui! 😊";
                await SalvarMensagemRespostaAsync(idConversa, reply);

                return (true, new AssistantDecision(reply, "none", null, false, null, null));
            }

            // Não conseguiu interpretar confirmação, não intercepta
            return (false, null);
        }

        private int? ExtrairNumeroEscolha(string texto)
        {
            texto = texto.ToLower().Trim();

            // Número direto
            if (int.TryParse(texto, out var numero))
                return numero;

            // Palavras
            var mapa = new Dictionary<string, int>
            {
                { "primeiro", 1 }, { "primeira", 1 }, { "um", 1 }, { "1", 1 },
                { "segundo", 2 }, { "segunda", 2 }, { "dois", 2 }, { "2", 2 },
                { "terceiro", 3 }, { "terceira", 3 }, { "tres", 3 }, { "três", 3 }, { "3", 3 },
                { "quarto", 4 }, { "quarta", 4 }, { "quatro", 4 }, { "4", 4 },
                { "quinto", 5 }, { "quinta", 5 }, { "cinco", 5 }, { "5", 5 }
            };

            foreach (var kvp in mapa)
            {
                if (texto.Contains(kvp.Key))
                    return kvp.Value;
            }

            return null;
        }

        private string? ExtrairHorario(string texto)
        {
            var match = Regex.Match(texto, @"(\d{1,2}):?(\d{2})");
            if (match.Success)
            {
                var hora = match.Groups[1].Value.PadLeft(2, '0');
                var minuto = match.Groups[2].Value;
                return $"{hora}:{minuto}";
            }

            match = Regex.Match(texto, @"(\d{1,2})\s*h");
            if (match.Success)
            {
                var hora = match.Groups[1].Value.PadLeft(2, '0');
                return $"{hora}:00";
            }

            return null;
        }

        private int? ExtrairQuantidade(string texto)
        {
            var match = Regex.Match(texto, @"(\d{1,3})\s*pessoas?", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var qtd))
                return qtd;

            match = Regex.Match(texto, @"para\s*(\d{1,3})", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out qtd))
                return qtd;

            return null;
        }

        private async Task SalvarMensagemRespostaAsync(Guid idConversa, string conteudo)
        {
            // Nota: A mensagem será salva e enviada pelo IAResponseHandler
            // Este método apenas registra que o contexto gerou uma resposta
            _logger.LogInformation("[Conversa={Conversa}] Resposta preparada pelo contexto interceptor", idConversa);
            await Task.CompletedTask;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
