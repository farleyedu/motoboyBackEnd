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
            // ✅ LOG PARA DEBUG - Ver exatamente o que foi recebido
            _logger.LogInformation(
                "[Conversa={Conversa}] ValidateReservaAsync - Nome: '{Nome}', Qtd: {Qtd}, Data: '{Data}', Hora: '{Hora}'",
                idConversa, nomeCompleto ?? "null", qtdPessoas, dataTexto ?? "null", horaTexto ?? "null");

            // ✅ LOG DA REFERÊNCIA ATUAL
            var referenciaAtual = TimeZoneHelper.GetSaoPauloNow();
            _logger.LogInformation(
                "[Conversa={Conversa}] Referência atual (São Paulo): {Data:yyyy-MM-dd HH:mm:ss}",
                idConversa, referenciaAtual);

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
            var dataCalculada = ParseDataRelativa(dataTexto, referenciaAtual);

            if (!dataCalculada.HasValue)
            {
                return ReservaValidationResult.Failure(
                    $"Não consegui entender a data '{dataTexto}'.\n\nPode me enviar com dia e mês, por favor? 😊\nExemplo: 25/12 ou 25/12/2025",
                    ReservaValidationIssue.DataInvalida);
            }

            var dataReserva = dataCalculada.Value.Date;

            // Validar regras de alteração por horário
            var ehMesmoDia = dataReserva.Date == referenciaAtual.Date;
            var horaAtual = referenciaAtual.TimeOfDay;
            var conversa = await _conversationRepository.ObterPorIdAsync(idConversa);
            var idEstabelecimento = conversa.IdEstabelecimento;
            if (ehMesmoDia)
            {
                // Regra: não pode criar/alterar reserva do mesmo dia após 16h
                if (horaAtual >= new TimeSpan(16, 0, 0))
                {
                    return ReservaValidationResult.Failure(
                        "Reservas para hoje só podem ser feitas até as 16h.\n\nPode escolher outro dia? 😊",
                        ReservaValidationIssue.DataInvalida);
                }


                // Limite do mesmo dia: 50 pessoas
                var capacidadeMesmoDia = 50;
                var ocupadasHoje = await _reservaRepository.SomarPessoasDoDiaAsync(idEstabelecimento, dataReserva.Date);

                if (ocupadasHoje + qtdPessoas.Value > capacidadeMesmoDia)
                {
                    var vagasRestantes = Math.Max(0, capacidadeMesmoDia - ocupadasHoje);
                    return ReservaValidationResult.Failure(
                        $"😔 Para reservas hoje, temos capacidade de {capacidadeMesmoDia} pessoas.\n\n📊 Situação atual:\n• Já reservadas: {ocupadasHoje} pessoas\n• Vagas disponíveis: {vagasRestantes} pessoas\n\nPode reduzir para até {vagasRestantes} pessoas ou escolher outro dia? 😊",
                        ReservaValidationIssue.CapacidadeExcedida);
                }
            }
            else
            {
                // Reserva antecipada: dia anterior até 21h
                var ehDiaAnterior = dataReserva.Date == referenciaAtual.Date.AddDays(1);
                if (ehDiaAnterior && horaAtual >= new TimeSpan(21, 0, 0))
                {
                    return ReservaValidationResult.Failure(
                        "Reservas para amanhã só podem ser feitas até as 21h de hoje.\n\nPode tentar novamente amanhã cedo? 😊",
                        ReservaValidationIssue.DataInvalida);
                }
            }

            // Validar se dia da semana corresponde
            var alerta = ValidarDiaDaSemana(dataTexto, dataReserva);
            if (!string.IsNullOrWhiteSpace(alerta))
            {
                return ReservaValidationResult.Failure(alerta, ReservaValidationIssue.DataInvalida);
            }

            var dataHoraReserva = dataReserva.Add(horaConvertida);

            // Validar se data+hora já passou (com margem de 1 hora)
            var limiteMinimo = referenciaAtual.AddHours(1);
            if (dataHoraReserva < limiteMinimo)
            {
                var msgErro = dataReserva.Date == referenciaAtual.Date
                    ? "Para reservas hoje, o horário deve ser pelo menos 1 hora no futuro.\n\nPode escolher outro horário? 😊"
                    : "A data e horário informados já passaram.\n\nPode escolher uma data futura? 😊";

                return ReservaValidationResult.Failure(msgErro, ReservaValidationIssue.DataPassada);
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
            if (conversa == null)
            {
                return ReservaValidationResult.Failure(
                    "Não consegui localizar nossa conversa agora.\n\nPode tentar novamente em instantes? 😊",
                    ReservaValidationIssue.DadosIncompletos);
            }

            var idCliente = conversa.IdCliente;

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

            var reservaMesmoDia = reservasAtivas
                .Where(r => r.DataReserva.Date == dataReserva.Date)
                .OrderByDescending(r => r.DataAtualizacao)
                .FirstOrDefault();

            if (reservaMesmoDia != null)
            {
                // Verificar se dados são EXATAMENTE iguais (cliente confirmando a mesma coisa)
                var horaExistente = reservaMesmoDia.HoraInicio;
                var qtdExistente = reservaMesmoDia.QtdPessoas ?? 0;

                var horaIgual = Math.Abs((horaExistente - horaConvertida).TotalMinutes) < 1;
                var qtdIgual = qtdExistente == qtdPessoas.Value;

                if (horaIgual && qtdIgual)
                {
                    // Cliente está confirmando a mesma reserva - permitir (não é duplicação)
                    _logger.LogDebug(
                        "[Conversa={Conversa}] Cliente confirmando reserva existente #{Id} com mesmos dados",
                        idConversa,
                        reservaMesmoDia.Id);

                    var result = ReservaValidationResult.Success(dataReserva, horaConvertida);
                    result.ReservaExistenteMesmoDia = reservaMesmoDia;
                    return result;
                }

                // Dados diferentes - avisar sobre duplicação
                var horaExistenteStr = reservaMesmoDia.HoraInicio.ToString(@"hh\:mm");
                var dataExistente = reservaMesmoDia.DataReserva.ToString("dd/MM/yyyy");

                var msgDuplicada = new StringBuilder();
                msgDuplicada.AppendLine("📋 Você já possui uma reserva para este dia:");
                msgDuplicada.AppendLine();
                msgDuplicada.AppendLine($"📅 Data: {dataExistente}");
                msgDuplicada.AppendLine($"⏰ Horário atual: {horaExistenteStr}");
                msgDuplicada.AppendLine($"👥 Pessoas: {qtdExistente}");
                msgDuplicada.AppendLine();
                msgDuplicada.AppendLine($"🔄 Dados novos informados:");
                msgDuplicada.AppendLine($"⏰ Horário: {horaConvertida:hh\\:mm}");
                msgDuplicada.AppendLine($"👥 Pessoas: {qtdPessoas}");
                msgDuplicada.AppendLine();
                msgDuplicada.AppendLine("O que você prefere?");
                msgDuplicada.AppendLine("1️⃣ Manter a reserva atual");
                msgDuplicada.AppendLine("2️⃣ Atualizar para os novos dados");
                msgDuplicada.AppendLine("3️⃣ Cancelar ambas");

                var result2 = ReservaValidationResult.Failure(msgDuplicada.ToString(), ReservaValidationIssue.DuplicacaoMesmoDia);
                result2.ReservaExistenteMesmoDia = reservaMesmoDia;
                result2.DataCalculada = dataReserva;
                result2.HoraCalculada = horaConvertida;
                return result2;
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
            // ✅ Usar horário de São Paulo para comparação
            var hojeEmSaoPaulo = TimeZoneHelper.GetSaoPauloNow().Date;
            var mesmoDia = dataReserva.Date == hojeEmSaoPaulo;
            var capacidadeTotal = mesmoDia ? 50 : 110;

            var ocupadas = await _reservaRepository.SomarPessoasDoDiaAsync(idEstabelecimento, dataReserva.Date);
            var disponiveis = capacidadeTotal - ocupadas;
            var temCapacidade = ocupadas + qtdPessoasSolicitada <= capacidadeTotal;

            return (temCapacidade, capacidadeTotal, ocupadas, disponiveis);
        }

        private static bool TryParseHora(string horaTexto, out TimeSpan hora)
        {
            // Tentar formato 24h primeiro (HH:mm) - CORRIGE O BUG
            if (TimeSpan.TryParseExact(horaTexto, @"HH\:mm", System.Globalization.CultureInfo.InvariantCulture, out hora))
            {
                return true;
            }

            // Fallback: tentar formato 12h (hh:mm)
            if (TimeSpan.TryParseExact(horaTexto, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out hora))
            {
                return true;
            }

            // Último fallback: parse livre
            return TimeSpan.TryParse(horaTexto, System.Globalization.CultureInfo.InvariantCulture, out hora);
        }

        /// <summary>
        /// ✅ MELHORADO: Parse robusto de datas com suporte a: hoje, amanhã, dd/MM, dd/MM/yyyy, dias da semana, "dia X"
        /// </summary>
        private DateTime? ParseDataRelativa(string dataTexto, DateTime referenciaAtual)
        {
            if (string.IsNullOrWhiteSpace(dataTexto))
                return null;

            var hojeSP = referenciaAtual.Date;
            var textoNorm = RemoveDiacritics(dataTexto.ToLowerInvariant().Trim()).Replace("-feira", string.Empty);

            _logger.LogInformation(
                "[ParseData] Input: '{Input}' | Normalizado: '{Norm}' | Ref: {Ref:yyyy-MM-dd}",
                dataTexto, textoNorm, hojeSP);

            // 1. TERMOS RELATIVOS (prioridade maxima)
            if (textoNorm == "hoje")
            {
                _logger.LogInformation("[ParseData] HOJE -> {Data:yyyy-MM-dd}", hojeSP);
                return hojeSP;
            }

            if (textoNorm.Contains("depois") && textoNorm.Contains("amanha"))
            {
                var depoisAmanha = hojeSP.AddDays(2);
                _logger.LogInformation("[ParseData] DEPOIS DE AMANHA -> {Data:yyyy-MM-dd}", depoisAmanha);
                return depoisAmanha;
            }

            if (textoNorm.Contains("amanha"))
            {
                var amanha = hojeSP.AddDays(1);
                _logger.LogInformation("[ParseData] AMANHA -> {Data:yyyy-MM-dd}", amanha);
                return amanha;
            }

            // 2. FORMATOS ABSOLUTOS (dd/MM/yyyy)
            if (DateTime.TryParseExact(textoNorm, "dd/MM/yyyy",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dataCompleta))
            {
                _logger.LogInformation("[ParseData] ✅ dd/MM/yyyy → {Data:yyyy-MM-dd}", dataCompleta.Date);
                return dataCompleta.Date;
            }

            // 3. FORMATO dd/MM (sem ano)
            if (DateTime.TryParseExact(textoNorm, "dd/MM",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dataSemAno))
            {
                var ano = hojeSP.Year;
                var dataComAno = new DateTime(ano, dataSemAno.Month, dataSemAno.Day);

                // Se ficou no passado, avançar 1 ano
                if (dataComAno < hojeSP)
                    dataComAno = dataComAno.AddYears(1);

                _logger.LogInformation("[ParseData] ✅ dd/MM → {Data:yyyy-MM-dd}", dataComAno.Date);
                return dataComAno.Date;
            }

            // 4. DIAS DA SEMANA (segunda, terça, quarta, quinta, sexta, sábado, domingo)
            var diasSemana = new System.Collections.Generic.Dictionary<string, DayOfWeek>
            {
                { "domingo", DayOfWeek.Sunday },
                { "segunda", DayOfWeek.Monday },
                { "terca", DayOfWeek.Tuesday },
                { "quarta", DayOfWeek.Wednesday },
                { "quinta", DayOfWeek.Thursday },
                { "sexta", DayOfWeek.Friday },
                { "sabado", DayOfWeek.Saturday }
            };

            foreach (var (nome, diaSemana) in diasSemana)
            {
                if (textoNorm.Contains(nome))
                {
                    // Calcular próxima ocorrência
                    var delta = ((int)diaSemana - (int)hojeSP.DayOfWeek + 7) % 7;
                    if (delta == 0) delta = 7; // Se é hoje, próxima semana

                    var dataResultado = hojeSP.AddDays(delta);

                    // Se mencionar "que vem" ou "próxima", adicionar 7 dias
                    if (textoNorm.Contains("que vem") || textoNorm.Contains("proxima"))
                        dataResultado = dataResultado.AddDays(7);

                    _logger.LogInformation("[ParseData] ✅ DIA SEMANA '{Nome}' → {Data:yyyy-MM-dd}", nome, dataResultado.Date);
                    return dataResultado.Date;
                }
            }

            // 5. "DIA X" ou numeros isolados
            if (DateParsingHelper.TryExtractDayNumber(textoNorm, out var diaExtraido))
            {
                try
                {
                    var mesAtual = hojeSP.Month;
                    var anoAtual = hojeSP.Year;

                    if (diaExtraido < hojeSP.Day)
                    {
                        mesAtual++;
                        if (mesAtual > 12)
                        {
                            mesAtual = 1;
                            anoAtual++;
                        }
                    }

                    var dataCalculada = new DateTime(anoAtual, mesAtual, diaExtraido);
                    _logger.LogInformation("[ParseData] DIA DETECTADO {Dia} -> {Data:yyyy-MM-dd}", diaExtraido, dataCalculada.Date);
                    return dataCalculada.Date;
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    _logger.LogWarning("[ParseData] Dia {Dia} invalido para o mes - {Erro}", diaExtraido, ex.Message);
                    return null;
                }
            }

            // 6. PARSE LIVRE (ultimo recurso)
            if (DateTime.TryParse(dataTexto, new System.Globalization.CultureInfo("pt-BR"), System.Globalization.DateTimeStyles.None, out var dataLivre))
            {
                _logger.LogWarning("[ParseData] ⚠️ Parse livre → {Data:yyyy-MM-dd}", dataLivre.Date);
                return dataLivre.Date;
            }

            _logger.LogWarning("[ParseData] ❌ FALHA ao parsear: '{Input}'", dataTexto);
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

        private string? ValidarDiaDaSemana(string dataTexto, DateTime dataCalculada)
        {
            var textoNormalizado = RemoveDiacritics(dataTexto.ToLowerInvariant());

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
                    var diaCalculado = dataCalculada.DayOfWeek;
                    if (diaCalculado != dia.Value)
                    {
                        var nomeDiaCalculado = dataCalculada.ToString("dddd", new System.Globalization.CultureInfo("pt-BR"));
                        var dataFormatada = dataCalculada.ToString("dd/MM/yyyy");

                        var msgAlerta = new StringBuilder();
                        msgAlerta.AppendLine($"⚠️ Atenção: Você mencionou '{dia.Key}', mas {dataFormatada} cai em {nomeDiaCalculado}.");
                        msgAlerta.AppendLine();
                        msgAlerta.Append("Deseja continuar com esta data? 😊");
                        return msgAlerta.ToString();
                    }
                    break;
                }
            }

            return null;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) =================
