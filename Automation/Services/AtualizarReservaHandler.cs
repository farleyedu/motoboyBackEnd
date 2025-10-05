// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using APIBack.Automation.Helpers;
using APIBack.Automation.Interfaces;
using APIBack.Model;
using APIBack.Repository.Interface;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    /// <summary>
    /// Handler especializado para atualização de reservas existentes
    /// </summary>
    public class AtualizarReservaHandler
    {
        private readonly IReservaRepository _reservaRepository;
        private readonly IConversationRepository _conversationRepository;
        private readonly ILogger<AtualizarReservaHandler> _logger;

        public AtualizarReservaHandler(
            IReservaRepository reservaRepository,
            IConversationRepository conversationRepository,
            ILogger<AtualizarReservaHandler> logger)
        {
            _reservaRepository = reservaRepository;
            _conversationRepository = conversationRepository;
            _logger = logger;
        }

        /// <summary>
        /// Atualiza uma reserva existente com novos dados
        /// </summary>
        public async Task<string> AtualizarReservaAsync(
            Guid idConversa,
            long idReservaExistente,
            int? novaQtdPessoas,
            TimeSpan? novoHorario)
        {
            try
            {
                var reserva = await _reservaRepository.BuscarPorIdAsync(idReservaExistente);
                if (reserva == null)
                {
                    _logger.LogWarning("[Conversa={Conversa}] Reserva #{Id} não encontrada para atualização", idConversa, idReservaExistente);
                    return "Não consegui localizar a reserva para atualizar 😔\n\nPode tentar novamente?";
                }

                // Validar se a reserva ainda está ativa
                if (reserva.Status != ReservaStatus.Confirmado)
                {
                    return "Esta reserva já foi cancelada ou finalizada.\n\nGostaria de criar uma nova? 😊";
                }

                var dadosAntigos = new StringBuilder();
                dadosAntigos.AppendLine("📋 Dados anteriores:");
                dadosAntigos.AppendLine($"📅 Data: {reserva.DataReserva:dd/MM/yyyy}");
                dadosAntigos.AppendLine($"⏰ Horário: {reserva.HoraInicio:hh\\:mm}");
                dadosAntigos.AppendLine($"👥 Pessoas: {reserva.QtdPessoas ?? 0}");

                var mudancas = new StringBuilder();
                bool houveAlteracao = false;

                // Atualizar quantidade de pessoas
                if (novaQtdPessoas.HasValue && novaQtdPessoas.Value != reserva.QtdPessoas)
                {
                    reserva.QtdPessoas = novaQtdPessoas.Value;
                    mudancas.AppendLine($"👥 Pessoas: {reserva.QtdPessoas}");
                    houveAlteracao = true;
                }

                // Atualizar horário
                if (novoHorario.HasValue && novoHorario.Value != reserva.HoraInicio)
                {
                    reserva.HoraInicio = novoHorario.Value;
                    mudancas.AppendLine($"⏰ Horário: {reserva.HoraInicio:hh\\:mm}");
                    houveAlteracao = true;
                }

                if (!houveAlteracao)
                {
                    return "Os dados informados são os mesmos da reserva atual 🤔\n\nGostaria de alterar algo diferente?";
                }

                // Validar capacidade com os novos dados
                if (novaQtdPessoas.HasValue)
                {
                    var conversa = await _conversationRepository.ObterPorIdAsync(idConversa);
                    if (conversa == null)
                    {
                        return "Não consegui validar a capacidade agora.\n\nPode tentar novamente? 😊";
                    }

                    var capacidadeTotal = reserva.DataReserva.Date == DateTime.Today ? 50 : 110;
                    var ocupadasSemEstaReserva = await _reservaRepository.SomarPessoasDoDiaAsync(
                        conversa.IdEstabelecimento,
                        reserva.DataReserva.Date) - (reserva.QtdPessoas ?? 0);

                    if (ocupadasSemEstaReserva + novaQtdPessoas.Value > capacidadeTotal)
                    {
                        var disponiveis = capacidadeTotal - ocupadasSemEstaReserva;
                        return $"😔 Não há capacidade suficiente para {novaQtdPessoas} pessoas.\n\n" +
                               $"📊 Situação atual:\n" +
                               $"• Capacidade máxima: {capacidadeTotal} pessoas\n" +
                               $"• Vagas disponíveis: {disponiveis} pessoas\n\n" +
                               $"Pode reduzir para até {disponiveis} pessoas? 😊";
                    }
                }

                // Atualizar no banco
                reserva.DataAtualizacao = DateTime.UtcNow;
                await _reservaRepository.AtualizarAsync(reserva);

                _logger.LogInformation(
                    "[Conversa={Conversa}] Reserva #{Id} atualizada com sucesso",
                    idConversa,
                    idReservaExistente);

                // Montar resposta
                var resposta = new StringBuilder();
                resposta.AppendLine("✅ Reserva atualizada com sucesso!");
                resposta.AppendLine();
                resposta.Append(dadosAntigos.ToString());
                resposta.AppendLine();
                resposta.AppendLine("🔄 Novos dados:");
                resposta.Append(mudancas.ToString());
                resposta.AppendLine();
                resposta.AppendLine($"🎫 Código da reserva: #{idReservaExistente}");
                resposta.AppendLine();
                resposta.Append("Nos vemos lá! ✨");

                return resposta.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Conversa={Conversa}] Erro ao atualizar reserva #{Id}", idConversa, idReservaExistente);
                return "Tivemos um problema ao atualizar a reserva 😔\n\nPode tentar novamente em instantes?";
            }
        }

        /// <summary>
        /// Detecta intenção de atualização a partir do texto do usuário
        /// </summary>
        public DeteccaoAtualizacao DetectarIntencaoAtualizacao(string textoUsuario)
        {
            var textoNormalizado = RemoveDiacritics(textoUsuario.ToLowerInvariant().Trim());

            // Palavras-chave que indicam atualização
            var palavrasAtualizacao = new[]
            {
                "atualizar", "atualiza", "mudar", "muda", "alterar", "altera",
                "modificar", "modifica", "trocar", "troca", "ajustar", "ajusta",
                "corrigir", "corrige", "nova", "novo", "novos", "novas",
                "para os novos dados", "com os novos", "esses dados"
            };

            var contemIntencao = palavrasAtualizacao.Any(p => textoNormalizado.Contains(p));

            // Detectar se menciona "opção 2" ou "número 2" ou "segunda"
            var opcao2 = textoNormalizado.Contains("opcao 2") ||
                         textoNormalizado.Contains("numero 2") ||
                         textoNormalizado.Contains("segunda opcao") ||
                         textoNormalizado.Contains("2") && (textoNormalizado.Contains("opcao") || textoNormalizado.Contains("escolho"));

            return new DeteccaoAtualizacao
            {
                TemIntencao = contemIntencao || opcao2,
                Confianca = opcao2 ? 0.95 : (contemIntencao ? 0.8 : 0.0)
            };
        }

        private static string RemoveDiacritics(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (category != UnicodeCategory.NonSpacingMark)
                    builder.Append(ch);
            }
            return builder.ToString().Normalize(NormalizationForm.FormC);
        }
    }

    public class DeteccaoAtualizacao
    {
        public bool TemIntencao { get; set; }
        public double Confianca { get; set; }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) =================