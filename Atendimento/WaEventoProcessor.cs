using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Services;
using APIBack.Service.Interface;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento
{
    /// <summary>Como terminou o processamento de um evento do webhook.</summary>
    public sealed record ResultadoEvento(string Resultado, string? Motivo = null)
    {
        public static ResultadoEvento Processado() => new("processado");
        public static ResultadoEvento Ignorado(string motivo) => new("ignorado", motivo);
        /// <summary>Ainda nao da para concluir (ex.: o recibo chegou antes de a mensagem ser vinculada): tenta de novo logo.</summary>
        public static ResultadoEvento TentarDeNovo(string motivo) => new("tentar_de_novo", motivo);
    }

    public interface IWaEventoProcessor
    {
        Task<ResultadoEvento> ProcessarAsync(WaEvento evento);
    }

    /// <summary>
    /// Processa um evento ja gravado: resolve o numero (canal) pelo phone_number_id, e entao entrega a mensagem ao
    /// atendimento ou aplica o recibo (entregue/lida/falhou) a mensagem enviada. Cada passo deixa uma linha no log.
    /// </summary>
    public sealed class WaEventoProcessor : IWaEventoProcessor
    {
        private const int TentativasParaRecibo = 4;

        private readonly ICanalRepository _canais;
        private readonly IMessageService _mensagens;
        private readonly IPipelineDeMensagem _pipeline;
        private readonly ILogger<WaEventoProcessor> _logger;

        public WaEventoProcessor(ICanalRepository canais, IMessageService mensagens, IPipelineDeMensagem pipeline, ILogger<WaEventoProcessor> logger)
        {
            _canais = canais;
            _mensagens = mensagens;
            _pipeline = pipeline;
            _logger = logger;
        }

        public Task<ResultadoEvento> ProcessarAsync(WaEvento evento) =>
            evento.Tipo == TipoEvento.Status ? ProcessarStatusAsync(evento) : ProcessarMensagemAsync(evento);

        // ---- mensagem recebida ----------------------------------------------------------------------------------

        private async Task<ResultadoEvento> ProcessarMensagemAsync(WaEvento evento)
        {
            var relogio = Stopwatch.StartNew();
            using var documento = JsonDocument.Parse(evento.PayloadJson);
            var raiz = documento.RootElement;

            var metadados = raiz.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<WebhookMetadataDto>(m.GetRawText())
                : null;
            var mensagem = raiz.TryGetProperty("message", out var msg) ? JsonSerializer.Deserialize<WebhookMessageDto>(msg.GetRawText()) : null;
            var contatos = raiz.TryGetProperty("contacts", out var c) && c.ValueKind == JsonValueKind.Array
                ? JsonSerializer.Deserialize<System.Collections.Generic.List<WebhookContactDto>>(c.GetRawText())
                : null;

            var phoneNumberId = metadados?.IdNumeroTelefone ?? evento.PhoneNumberId;
            var display = metadados?.NumeroTelefoneExibicao ?? evento.DisplayPhone;

            if (mensagem == null || string.IsNullOrWhiteSpace(mensagem.Id) || string.IsNullOrWhiteSpace(mensagem.De))
            {
                _logger.LogWarning("[wa.in] ev=ignorada motivo=mensagem_incompleta wa={Wa}", evento.Chave);
                return ResultadoEvento.Ignorado("mensagem sem id ou remetente");
            }

            var canal = string.IsNullOrWhiteSpace(phoneNumberId) ? null : await _canais.ObterAtivoPorPhoneNumberIdAsync(phoneNumberId);
            if (canal == null)
            {
                // O caso que mais confundiu na pratica: a Meta entrega por um ID que nenhuma loja cadastrou.
                _logger.LogWarning(
                    "[wa.in] ev=ignorada motivo=canal_desconhecido wa={Wa} phone_number_id={PhoneNumberId} display={Display} " +
                    "dica=\"cadastre este Phone Number ID na loja (Gestao > numeros de WhatsApp)\"", evento.Chave, phoneNumberId, display);
                return ResultadoEvento.Ignorado($"canal desconhecido (phone_number_id={phoneNumberId}, display={display})");
            }

            await _canais.MarcarRecebimentoAsync(canal.Id);

            var texto = WaMensagemTexto.Exibicao(mensagem);
            var interpretado = WaMensagemTexto.Interpretado(mensagem);
            _logger.LogInformation(
                "[wa.in] ev=roteada wa={Wa} canal={Canal} loja={Loja} modo={Modo} tipo={Tipo} texto=\"{Texto}\"",
                evento.Chave, canal.Id, canal.IdEstabelecimento, canal.ModoAtendimento, mensagem.Tipo, Cortar(texto));

            DateTime? dataUtc = null;
            if (long.TryParse(mensagem.CarimboTempo, out var unix))
            {
                try { dataUtc = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime; } catch (ArgumentOutOfRangeException) { }
            }

            var input = new ConversationProcessingInput(
                Mensagem: mensagem,
                Texto: texto,
                PhoneNumberDisplay: display,
                PhoneNumberId: phoneNumberId,
                DataMensagemUtc: dataUtc,
                Valor: new WebhookChangeValueDto { Metadados = metadados, Contatos = contatos },
                TextoInterpretado: interpretado,
                IdEstabelecimento: canal.IdEstabelecimento,
                IdCanal: canal.Id);

            await _pipeline.ExecutarAsync(input, canal);

            _logger.LogInformation("[wa.in] ev=concluida wa={Wa} canal={Canal} ms={Ms}", evento.Chave, canal.Id, relogio.ElapsedMilliseconds);
            return ResultadoEvento.Processado();
        }

        // ---- recibos (entregue / lida / falhou) ---------------------------------------------------------------------

        private async Task<ResultadoEvento> ProcessarStatusAsync(WaEvento evento)
        {
            using var documento = JsonDocument.Parse(evento.PayloadJson);
            if (!documento.RootElement.TryGetProperty("status", out var status))
            {
                return ResultadoEvento.Ignorado("status ausente");
            }

            var wamid = Texto(status, "id");
            var situacao = Texto(status, "status");
            if (string.IsNullOrWhiteSpace(wamid) || string.IsNullOrWhiteSpace(situacao))
            {
                return ResultadoEvento.Ignorado("status incompleto");
            }

            string? codigo = null, detalhe = null;
            if (status.TryGetProperty("errors", out var erros) && erros.ValueKind == JsonValueKind.Array && erros.GetArrayLength() > 0)
            {
                var primeiro = erros[0];
                codigo = primeiro.TryGetProperty("code", out var cd) ? cd.ToString() : null;
                detalhe = Texto(primeiro, "title") ?? Texto(primeiro, "message");
                if (primeiro.TryGetProperty("error_data", out var dados) && Texto(dados, "details") is { } d) detalhe = $"{detalhe}: {d}";
            }

            var atualizada = await _mensagens.AtualizarStatusPorProvedorAsync(wamid, situacao, codigo, detalhe);
            if (!atualizada)
            {
                // O recibo pode chegar milissegundos antes de a mensagem receber o id da Meta: tenta de novo algumas vezes.
                if (evento.Tentativas < TentativasParaRecibo)
                {
                    return ResultadoEvento.TentarDeNovo($"mensagem {wamid} ainda nao vinculada");
                }

                _logger.LogInformation("[wa.status] ev=sem_mensagem wa={Wa} status={Status}", wamid, situacao);
                return ResultadoEvento.Ignorado("mensagem do recibo nao encontrada");
            }

            if (string.Equals(situacao, "failed", StringComparison.OrdinalIgnoreCase))
            {
                // Falha DEPOIS de a Meta aceitar o envio (numero sem WhatsApp, janela fechada...): precisa aparecer.
                _logger.LogWarning("[wa.status] ev=falhou wa={Wa} codigo={Codigo} motivo=\"{Motivo}\"", wamid, codigo, detalhe);
            }
            else
            {
                _logger.LogInformation("[wa.status] ev={Status} wa={Wa}", situacao.ToLowerInvariant(), wamid);
            }

            return ResultadoEvento.Processado();
        }

        private static string? Texto(JsonElement elemento, string propriedade) =>
            elemento.ValueKind == JsonValueKind.Object && elemento.TryGetProperty(propriedade, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        private static string Cortar(string? texto) =>
            string.IsNullOrEmpty(texto) ? string.Empty : texto.Length <= 80 ? texto : texto[..80] + "…";
    }

    /// <summary>Texto que o atendimento enxerga de uma mensagem recebida (inclusive os tipos que nao sao texto).</summary>
    public static class WaMensagemTexto
    {
        public static string Exibicao(WebhookMessageDto mensagem)
        {
            if (!string.IsNullOrWhiteSpace(mensagem.Texto?.Corpo)) return mensagem.Texto.Corpo;
            if (!string.IsNullOrWhiteSpace(mensagem.Interactive?.ButtonReply?.Title)) return mensagem.Interactive.ButtonReply.Title;
            if (!string.IsNullOrWhiteSpace(mensagem.Interactive?.ListReply?.Title)) return mensagem.Interactive.ListReply.Title;

            // Imagem, audio, documento, localizacao...: ainda nao sao tratados, mas o atendente precisa saber que chegou algo.
            return $"[{(string.IsNullOrWhiteSpace(mensagem.Tipo) ? "mensagem" : mensagem.Tipo)} recebida: o atendimento automatico ainda nao le este tipo]";
        }

        public static string? Interpretado(WebhookMessageDto mensagem)
        {
            if (!string.IsNullOrWhiteSpace(mensagem.Interactive?.ButtonReply?.Id)) return mensagem.Interactive.ButtonReply.Id;
            if (!string.IsNullOrWhiteSpace(mensagem.Interactive?.ListReply?.Id)) return mensagem.Interactive.ListReply.Id;
            return mensagem.Texto?.Corpo;
        }
    }
}
