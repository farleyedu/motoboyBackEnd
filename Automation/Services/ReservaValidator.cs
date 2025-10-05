// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using APIBack.Automation.Helpers;
using APIBack.Automation.Interfaces;
using APIBack.Model;
using APIBack.Repository.Interface;
using APIBack.Service;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace APIBack.Automation.Services
{
    /// <summary>
    /// Resultado da validação prévia de reserva
    /// </summary>
    public class ReservaValidationResult
    {
        public bool IsValid { get; set; }
        public string? MensagemErro { get; set; }
        public ReservaValidationIssue? Issue { get; set; }
        public DateTime? DataCalculada { get; set; }
        public TimeSpan? HoraCalculada { get; set; }
        public Reserva? ReservaExistenteMesmoDia { get; set; }
        public int? CapacidadeTotal { get; set; }
        public int? VagasOcupadas { get; set; }
        public int? VagasDisponiveis { get; set; }

        public static ReservaValidationResult Success(DateTime dataCalculada, TimeSpan horaCalculada)
        {
            return new ReservaValidationResult
            {
                IsValid = true,
                DataCalculada = dataCalculada,
                HoraCalculada = horaCalculada
            };
        }

        public static ReservaValidationResult Failure(string mensagem, ReservaValidationIssue issue)
        {
            return new ReservaValidationResult
            {
                IsValid = false,
                MensagemErro = mensagem,
                Issue = issue
            };
        }
    }

    public enum ReservaValidationIssue
    {
        DadosIncompletos,
        HorarioInvalido,
        DataInvalida,
        DataPassada,
        DataMuitoDistante,
        DuplicacaoMesmoDia,
        CapacidadeExcedida,
        QuantidadePessoasInvalida
    }

    /// <summary>
    /// Validador preventivo de reservas - executa ANTES da IA tentar confirmar
    /// </summary>
    public class ReservaValidator
    {
        private readonly IReservaRepository _reservaRepository;
        private readonly IConversationRepository _conversationRepository;
        private readonly ILogger<ReservaValidator> _logger;

        public ReservaValidator(
            IReservaRepository reservaRepository,
            IConversationRepository conversationRepository,
            ILogger<ReservaValidator> logger)
        {
            _reservaRepository = reservaRepository;
            _conversationRepository = conversationRepository;
            _logger = logger;
        }

        /// <summary>
        /// Valida uma tentativa de reserva ANTES de processar com a IA
        /// </summary>
        public async Task<ReservaValidationResult> ValidateReservaAsync(
            Guid idConversa,
            string? nomeCompleto,
            int? qtdPessoas,
            string? dataTexto,
            string? horaTexto)
        {
            // 1. Validar dados básicos
            if (string.IsNullOrWhiteSpace(nomeCompleto) ||
                !qtdPessoas.HasValue || qtdPessoas.Value <= 0 ||
                string.IsNullOrWhiteSpace(dataTexto) ||
                string.IsNullOrWhiteSpace(horaTexto))
            {
                return ReservaValidationResult.Failure(
                    "Para confirmar sua reserva, preciso de:\n\n📋 Nome completo\n👥 Quantidade de pessoas\n📅 Data\n⏰ Horário\n\nPode me passar essas informações? 😊",
                    ReservaValidationIssue.DadosIncompletos);
            }

            // 2. Validar horário
            if (!TryParseHora(horaTexto, out var horaConvertida))
            {
                return ReservaValidationResult.Failure(
                    "Não consegui entender o horário informado.\n\nPode enviar no formato HH:mm? 😊\nExemplo: 19:00",
                    ReservaValidationIssue.HorarioInvalido);
            }

            // 3. Parsear e validar data
            var referenciaAtual = TimeZoneHelper.GetSaoPauloNow();
            var dataCalculada = ParseDataRelativa(dataTexto, referenciaAtual);

            if (!dataCalculada.HasValue)
            {
                return ReservaValidationResult.Failure(
                    $"Não consegui entender a data '{dataTexto}'.\n\nPode me enviar com dia e mês, por favor? 😊\nExemplo: 25/12 ou 25/12/2025",
                    ReservaValidationIssue.DataInvalida);
            }

            var dataReserva = dataCalculada.Value.Date;
            var dataHoraReserva = DateTime.SpecifyKind(dataReserva, DateTimeKind.Unspecified).Add(horaConvertida);

            // 4. Validar se não é passado
            if (dataHoraReserva <= referenciaAtual)
            {
                return ReservaValidationResult.Failure(
                    "Para garantir a melhor experiência, as reservas precisam ser feitas para um horário futuro.\n\nPode escolher outro horário? 😊",
                    ReservaValidationIssue.DataPassada);
            }

            // 5. Validar limite de 14 dias
            var limiteMaximo = referenciaAtual.Date.AddDays(14);
            if (dataReserva > limiteMaximo)
            {
                return ReservaValidationResult.Failure(
                    "Atendemos reservas com até 14 dias de antecedência.\n\nPode escolher uma data mais próxima? 😊",
                    ReservaValidationIssue.DataMuitoDistante);
            }

            // 6. Obter dados da conversa
            var conversa = await _conversationRepository.ObterPorIdAsync(idConversa);
            if (conversa == null)
            {
                return ReservaValidationResult.Failure(
                    "Não consegui localizar nossa conversa agora.\n\nPode tentar novamente em instantes? 😊",
                    ReservaValidationIssue.DadosIncompletos);
            }

            var idCliente = conversa.IdCliente;
            var idEstabelecimento = conversa.IdEstabelecimento;

            if (idCliente == Guid.Empty || idEstabelecimento == Guid.Empty)
            {
                return ReservaValidationResult.Failure(
                    "Tivemos um probleminha ao confirmar.\n\nPode tentar novamente em instantes? 😊",
                    ReservaValidationIssue.DadosIncompletos);
            }

            // 7. Verificar duplicação no mesmo dia
            var reservasExistentes = await _reservaRepository.ObterPorClienteEstabelecimentoAsync(idCliente, idEstabelecimento);
            var reservasAtivas = reservasExistentes
                .Where(r => r.Status == ReservaStatus.Confirmado && r.DataReserva >= referenciaAtual.Date)
                .ToList();

            var reservaMesmoDia = reservasAtivas.FirstOrDefault(r => r.DataReserva.Date == dataReserva.Date);
            if (reservaMesmoDia != null)
            {
                var horaExistente = reservaMesmoDia.HoraInicio.ToString(@"hh\:mm");
                var dataExistente = reservaMesmoDia.DataReserva.ToString("dd/MM/yyyy");

                var msgDuplicada = new StringBuilder();
                msgDuplicada.AppendLine("📋 Você já possui uma reserva para este dia:");
                msgDuplicada.AppendLine();
                msgDuplicada.AppendLine($"📅 Data: {dataExistente}");
                msgDuplicada.AppendLine($"⏰ Horário atual: {horaExistente}");
                msgDuplicada.AppendLine($"👥 Pessoas: {reservaMesmoDia.QtdPessoas ?? 0}");
                msgDuplicada.AppendLine();
                msgDuplicada.AppendLine($"🔄 Dados novos informados:");
                msgDuplicada.AppendLine($"⏰ Horário: {horaConvertida:hh\\:mm}");
                msgDuplicada.AppendLine($"👥 Pessoas: {qtdPessoas}");
                msgDuplicada.AppendLine();
                msgDuplicada.AppendLine("O que você prefere?");
                msgDuplicada.AppendLine("1️⃣ Manter a reserva atual");
                msgDuplicada.AppendLine("2️⃣ Atualizar para os novos dados");
                msgDuplicada.AppendLine("3️⃣ Cancelar ambas");

                var result = ReservaValidationResult.Failure(msgDuplicada.ToString(), ReservaValidationIssue.DuplicacaoMesmoDia);
                result.ReservaExistenteMesmoDia = reservaMesmoDia;
                result.DataCalculada = dataReserva;
                result.HoraCalculada = horaConvertida;
                return result;
            }

            // 8. Validar capacidade disponível
            var capacidadeInfo = await ObterInformacoesCapacidadeAsync(idEstabelecimento, dataReserva, qtdPessoas.Value);

            if (!capacidadeInfo.TemCapacidade)
            {
                var msg = new StringBuilder();
                msg.AppendLine($"😔 Infelizmente não temos vagas suficientes para {qtdPessoas} pessoas neste dia.");
                msg.AppendLine();
                msg.AppendLine($"📊 Situação do dia {dataReserva:dd/MM/yyyy}:");
                msg.AppendLine($"• Capacidade máxima: {capacidadeInfo.CapacidadeTotal} pessoas");
                msg.AppendLine($"• Já reservadas: {capacidadeInfo.VagasOcupadas} pessoas");
                msg.AppendLine($"• Vagas disponíveis: {capacidadeInfo.VagasDisponiveis} pessoas");
                msg.AppendLine();
                msg.AppendLine("💡 Sugestões:");
                msg.AppendLine($"• Escolher outro dia");
                msg.AppendLine($"• Reduzir para até {capacidadeInfo.VagasDisponiveis} pessoas");
                msg.AppendLine();
                msg.Append("Como prefere continuar? 😊");

                var result = ReservaValidationResult.Failure(msg.ToString(), ReservaValidationIssue.CapacidadeExcedida);
                result.CapacidadeTotal = capacidadeInfo.CapacidadeTotal;
                result.VagasOcupadas = capacidadeInfo.VagasOcupadas;
                result.VagasDisponiveis = capacidadeInfo.VagasDisponiveis;
                result.DataCalculada = dataReserva;
                result.HoraCalculada = horaConvertida;
                return result;
            }

            // ✅ Tudo válido!
            var successResult = ReservaValidationResult.Success(dataReserva, horaConvertida);
            successResult.CapacidadeTotal = capacidadeInfo.CapacidadeTotal;
            successResult.VagasOcupadas = capacidadeInfo.VagasOcupadas;
            successResult.VagasDisponiveis = capacidadeInfo.VagasDisponiveis;
            return successResult;
        }

        private async Task<(bool TemCapacidade, int CapacidadeTotal, int VagasOcupadas, int VagasDisponiveis)>
            ObterInformacoesCapacidadeAsync(Guid idEstabelecimento, DateTime dataReserva, int qtdPessoasSolicitada)
        {
            var hoje = DateTime.Today;
            var mesmoDia = dataReserva.Date == hoje;
            var capacidadeTotal = mesmoDia ? 50 : 110;

            var ocupadas = await _reservaRepository.SomarPessoasDoDiaAsync(idEstabelecimento, dataReserva.Date);
            var disponiveis = capacidadeTotal - ocupadas;
            var temCapacidade = ocupadas + qtdPessoasSolicitada <= capacidadeTotal;

            return (temCapacidade, capacidadeTotal, ocupadas, disponiveis);
        }

        private static bool TryParseHora(string horaTexto, out TimeSpan hora)
        {
            return TimeSpan.TryParseExact(horaTexto, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out hora);
        }

        private DateTime? ParseDataRelativa(string dataTexto, DateTime referenciaAtual)
        {
            if (string.IsNullOrWhiteSpace(dataTexto))
                return null;

            var textoNormalizado = RemoveDiacritics(dataTexto.ToLowerInvariant().Trim()).Replace("-feira", string.Empty);
            var hoje = referenciaAtual.Date;

            // Termos relativos exatos
            if (textoNormalizado == "hoje") return hoje;
            if (textoNormalizado == "amanha") return hoje.AddDays(1);
            if (textoNormalizado == "depois de amanha") return hoje.AddDays(2);

            // Dias da semana
            var diasDaSemana = new System.Collections.Generic.Dictionary<string, DayOfWeek>
            {
                { "domingo", DayOfWeek.Sunday },
                { "segunda", DayOfWeek.Monday },
                { "terca", DayOfWeek.Tuesday },
                { "quarta", DayOfWeek.Wednesday },
                { "quinta", DayOfWeek.Thursday },
                { "sexta", DayOfWeek.Friday },
                { "sabado", DayOfWeek.Saturday }
            };

            foreach (var dia in diasDaSemana)
            {
                if (textoNormalizado.Contains(dia.Key))
                {
                    var dataResultado = hoje.AddDays(1);
                    while (dataResultado.DayOfWeek != dia.Value)
                        dataResultado = dataResultado.AddDays(1);

                    if (textoNormalizado.Contains("que vem") || textoNormalizado.Contains("proxima"))
                        dataResultado = dataResultado.AddDays(7);

                    return dataResultado;
                }
            }

            // Formatos específicos
            if (DateTime.TryParseExact(textoNormalizado, "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dataEspecifica))
                return dataEspecifica.Date;

            if (DateTime.TryParse(dataTexto, new System.Globalization.CultureInfo("pt-BR"), System.Globalization.DateTimeStyles.None, out var dataOutroFormato))
                return dataOutroFormato.Date;

            return null;
        }

        private static string RemoveDiacritics(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value.Normalize(System.Text.NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (category != System.Globalization.UnicodeCategory.NonSpacingMark)
                    builder.Append(ch);
            }
            return builder.ToString().Normalize(System.Text.NormalizationForm.FormC);
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) =================