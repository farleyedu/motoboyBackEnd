using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento
{
    public sealed record ResultadoIngestao(int Novos, int Repetidos, int Ignorados);

    /// <summary>
    /// Pega o corpo de um webhook da Meta (ja com a assinatura conferida) e GRAVA cada mensagem/status em wa_evento; o processamento
    /// acontece depois, no WaEventoWorker. E o mesmo para o webhook global (/wa/webhook) e para o de cada numero proprio.
    /// </summary>
    public interface IWaWebhookIngestor
    {
        /// <param name="canal">
        /// Nulo no webhook global. No webhook de um numero proprio, so valem os eventos cujo metadata.phone_number_id e o deste canal:
        /// sem isso o dono de um app poderia forjar mensagens para o numero de outra loja.
        /// </param>
        Task<ResultadoIngestao> IngerirAsync(JsonElement raiz, CanalWhatsapp? canal);
    }

    public sealed class WaWebhookIngestor : IWaWebhookIngestor
    {
        private readonly IWaEventoRepository _eventos;
        private readonly ILogger<WaWebhookIngestor> _logger;
        private readonly ICanalRepository? _canais;

        /// <param name="canais">
        /// Resolve o destino (canal + loja) NO INGRESSO, gravado junto com a mensagem. Sem ele (testes antigos) o evento fica sem
        /// destino capturado e o processador usa o caminho legado.
        /// </param>
        public WaWebhookIngestor(IWaEventoRepository eventos, ILogger<WaWebhookIngestor> logger, ICanalRepository? canais = null)
        {
            _eventos = eventos;
            _logger = logger;
            _canais = canais;
        }

        /// <summary>Relogio trocavel nos testes.</summary>
        internal Func<DateTime> Agora { get; set; } = () => DateTime.UtcNow;

        public async Task<ResultadoIngestao> IngerirAsync(JsonElement raiz, CanalWhatsapp? canal)
        {
            int novos = 0, repetidos = 0, ignorados = 0;
            foreach (var (campo, valor, wabaId) in Valores(raiz))
            {
                var (n, r, i) = campo switch
                {
                    // Ciclo de vida da conta (ex.: PARTNER_REMOVED). So vale no webhook global: e sobre o app ZippyGo, nao sobre o app do cliente.
                    "account_update" when canal == null => await GravarContaAsync(valor, wabaId),
                    "account_update" => (0, 0, 1),
                    // Coexistencia: historico e contatos do app WhatsApp Business nao viram conversa nova nem resposta automatica.
                    "history" or "smb_app_state_sync" => Ignorar(campo, wabaId),
                    _ => await GravarEventosAsync(valor, canal, wabaId)
                };
                novos += n;
                repetidos += r;
                ignorados += i;
            }

            return new ResultadoIngestao(novos, repetidos, ignorados);
        }

        /// <summary>Cada mudanca com o campo assinado (messages, account_update...) e o id da entrada (a WABA).</summary>
        private static IEnumerable<(string? Campo, JsonElement Valor, string? WabaId)> Valores(JsonElement raiz)
        {
            if (!raiz.TryGetProperty("entry", out var entradas) || entradas.ValueKind != JsonValueKind.Array) yield break;

            foreach (var entrada in entradas.EnumerateArray())
            {
                if (!entrada.TryGetProperty("changes", out var mudancas) || mudancas.ValueKind != JsonValueKind.Array) continue;
                var wabaId = Texto(entrada, "id");

                foreach (var mudanca in mudancas.EnumerateArray())
                {
                    if (mudanca.TryGetProperty("value", out var valor) && valor.ValueKind == JsonValueKind.Object)
                    {
                        yield return (Texto(mudanca, "field"), valor, wabaId);
                    }
                }
            }
        }

        private (int, int, int) Ignorar(string campo, string? wabaId)
        {
            _logger.LogInformation("[wa.in] ev=ignorada motivo=campo_sem_uso campo={Campo} waba={Waba}", campo, wabaId);
            return (0, 0, 1);
        }

        private async Task<(int Novos, int Repetidos, int Ignorados)> GravarContaAsync(JsonElement valor, string? wabaId)
        {
            var evento = Texto(valor, "event") ?? "desconhecido";
            var waba = valor.TryGetProperty("waba_info", out var info) ? Texto(info, "waba_id") ?? wabaId : wabaId;
            var payload = JsonSerializer.Serialize(new { field = "account_update", waba_id = waba, value = Clonar(valor) });
            // A Meta reenvia: o mesmo corpo vira a mesma chave (idempotente).
            var chave = $"{waba}:{evento}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(valor.GetRawText())))[..16]}";
            var novo = await _eventos.RegistrarAsync(new WaEvento { Id = Guid.NewGuid(), Tipo = TipoEvento.Conta, Chave = chave, PayloadJson = payload });
            _logger.LogInformation("[wa.in] ev={Ev} tipo=conta evento={Evento} waba={Waba}", novo ? "conta_recebida" : "conta_repetida", evento, waba);
            return novo ? (1, 0, 0) : (0, 1, 0);
        }

        private async Task<(int Novos, int Repetidos, int Ignorados)> GravarEventosAsync(JsonElement valor, CanalWhatsapp? canal, string? wabaId)
        {
            int novos = 0, repetidos = 0;
            valor.TryGetProperty("metadata", out var metadata);
            var phoneNumberId = Texto(metadata, "phone_number_id");
            var display = Texto(metadata, "display_phone_number");
            valor.TryGetProperty("contacts", out var contatos);

            if (canal != null && phoneNumberId != canal.PhoneNumberId)
            {
                // O app do cliente pode servir varios numeros, mas esta URL e so deste canal. Evento de outro numero nao entra.
                var quantos = Contar(valor, "messages") + Contar(valor, "statuses") + Contar(valor, "message_echoes");
                if (quantos > 0)
                {
                    _logger.LogWarning(
                        "[wa.in] ev=ignorada motivo=phone_number_id_diferente_do_canal canal={Canal} loja={Loja} phone_number_id={PhoneNumberId} eventos={Eventos}",
                        canal.Id, canal.IdEstabelecimento, phoneNumberId, quantos);
                }

                return (0, 0, quantos);
            }

            if (valor.TryGetProperty("messages", out var mensagens) && mensagens.ValueKind == JsonValueKind.Array)
            {
                // O destino e o dono do numero AGORA, no ingresso. O worker so confere; nunca recalcula pelo Phone Number ID,
                // entao trocar o numero de loja com a fila pendente nao leva mensagem antiga para a loja nova.
                var destino = await CapturarDestinoAsync(canal, phoneNumberId, mensagens.GetArrayLength());

                foreach (var mensagem in mensagens.EnumerateArray())
                {
                    var id = Texto(mensagem, "id");
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        _logger.LogWarning("[wa.in] ev=ignorada motivo=mensagem_sem_id phone_number_id={PhoneNumberId}", phoneNumberId);
                        continue;
                    }

                    // waba_id (id da entrada) deixa o processador conferir que o numero autorizado e mesmo daquela WABA.
                    var payload = JsonSerializer.Serialize(new
                    {
                        metadata = Clonar(metadata),
                        contacts = Clonar(contatos),
                        message = Clonar(mensagem),
                        waba_id = wabaId
                    });

                    var evento = new WaEvento
                    {
                        Id = Guid.NewGuid(), Tipo = TipoEvento.Mensagem, Chave = id,
                        PhoneNumberId = phoneNumberId, DisplayPhone = display, PayloadJson = payload, IdCanal = canal?.Id
                    };
                    Descrever(evento, mensagem);
                    destino?.Aplicar(evento);
                    var novo = await _eventos.RegistrarAsync(evento);

                    _logger.LogInformation(
                        "[wa.in] ev={Evento} wa={Wa} tipo={Tipo} de={De} phone_number_id={PhoneNumberId} display={Display} rota={Rota} loja={Loja} bytes={Bytes}",
                        novo ? "recebida" : "repetida", id, evento.TipoMensagem, Mascarar(evento.Remetente),
                        phoneNumberId, display, evento.Rota ?? "(nao_capturada)", evento.IdEstabelecimentoDestino, payload.Length);
                    if (novo) novos++; else repetidos++;
                }
            }

            if (valor.TryGetProperty("statuses", out var statuses) && statuses.ValueKind == JsonValueKind.Array)
            {
                foreach (var status in statuses.EnumerateArray())
                {
                    var id = Texto(status, "id");
                    var situacao = Texto(status, "status");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(situacao)) continue;

                    var payload = JsonSerializer.Serialize(new { metadata = Clonar(metadata), status = Clonar(status) });
                    var novo = await _eventos.RegistrarAsync(new WaEvento
                    {
                        Id = Guid.NewGuid(), Tipo = TipoEvento.Status, Chave = $"{id}:{situacao}",
                        PhoneNumberId = phoneNumberId, DisplayPhone = display, PayloadJson = payload, IdCanal = canal?.Id
                    });

                    _logger.LogInformation(
                        "[wa.in] ev={Evento} wa={Wa} status={Status} phone_number_id={PhoneNumberId}",
                        novo ? "status_recebido" : "status_repetido", id, situacao, phoneNumberId);
                    if (novo) novos++; else repetidos++;
                }
            }

            // smb_message_echoes: mensagem que o restaurante mandou pelo app WhatsApp Business (coexistencia). Fica gravada (idempotente)
            // e nunca dispara resposta automatica.
            if (valor.TryGetProperty("message_echoes", out var ecos) && ecos.ValueKind == JsonValueKind.Array)
            {
                foreach (var eco in ecos.EnumerateArray())
                {
                    var id = Texto(eco, "id");
                    if (string.IsNullOrWhiteSpace(id)) continue;

                    var payload = JsonSerializer.Serialize(new { metadata = Clonar(metadata), message = Clonar(eco), waba_id = wabaId });
                    var novo = await _eventos.RegistrarAsync(new WaEvento
                    {
                        Id = Guid.NewGuid(), Tipo = TipoEvento.Eco, Chave = id,
                        PhoneNumberId = phoneNumberId, DisplayPhone = display, PayloadJson = payload, IdCanal = canal?.Id
                    });
                    _logger.LogInformation("[wa.in] ev={Evento} wa={Wa} phone_number_id={PhoneNumberId}", novo ? "eco_recebido" : "eco_repetido", id, phoneNumberId);
                    if (novo) novos++; else repetidos++;
                }
            }

            return (novos, repetidos, 0);
        }

        /// <summary>Destino de um lote de mensagens do mesmo numero, capturado uma vez. Nulo = sem repositorio (caminho legado).</summary>
        private sealed record DestinoCapturado(string Rota, DateTime Em, Guid? Canal, Guid? Loja, string? SnapshotJson)
        {
            public void Aplicar(WaEvento e)
            {
                e.Rota = Rota;
                e.RotaCapturadaEm = Em;
                e.IdCanalDestino = Canal;
                e.IdEstabelecimentoDestino = Loja;
                e.RotaSnapshotJson = SnapshotJson;
            }
        }

        private async Task<DestinoCapturado?> CapturarDestinoAsync(CanalWhatsapp? canalDoWebhook, string? phoneNumberId, int quantas)
        {
            if (quantas == 0) return null;
            var agora = Agora();
            // URL propria: o canal ja foi conferido contra o phone_number_id acima. Global: o dono ativo do ID agora.
            var canal = canalDoWebhook;
            if (canal == null)
            {
                if (_canais == null) return null;
                canal = string.IsNullOrWhiteSpace(phoneNumberId) ? null : await _canais.ObterAtivoPorPhoneNumberIdAsync(phoneNumberId);
            }

            if (canal == null || canal.IdEstabelecimento == Guid.Empty)
                return new DestinoCapturado(RotaEvento.SemCanal, agora, null, null, null);

            var snapshot = JsonSerializer.Serialize(new
            {
                origem = canal.Origem, teste = canal.DeTeste, finalidade = canal.FinalidadeTeste, principal = canal.Central,
                waba = canal.WabaId, phone_number_id = canal.PhoneNumberId
            });
            return new DestinoCapturado(RotaEvento.Para(canal), agora, canal.Id, canal.IdEstabelecimento, snapshot);
        }

        /// <summary>Remetente, tipo, horario e referencia da midia (so o ID da Meta; o arquivo nunca passa por aqui).</summary>
        private static void Descrever(WaEvento evento, JsonElement mensagem)
        {
            evento.Remetente = Texto(mensagem, "from");
            evento.TipoMensagem = Texto(mensagem, "type");
            if (long.TryParse(Texto(mensagem, "timestamp"), out var unix))
            {
                try { evento.MensagemEm = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime; } catch (ArgumentOutOfRangeException) { }
            }

            if (evento.TipoMensagem is { Length: > 0 } tipo && mensagem.TryGetProperty(tipo, out var midia) && midia.ValueKind == JsonValueKind.Object)
            {
                evento.MidiaId = Texto(midia, "id");
                evento.MidiaMime = Texto(midia, "mime_type");
            }
        }

        private static int Contar(JsonElement valor, string propriedade) =>
            valor.TryGetProperty(propriedade, out var lista) && lista.ValueKind == JsonValueKind.Array ? lista.GetArrayLength() : 0;

        private static string? Texto(JsonElement elemento, string propriedade) =>
            elemento.ValueKind == JsonValueKind.Object && elemento.TryGetProperty(propriedade, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        // JsonElement e uma "visao" do documento; clonar permite serializar depois que o documento for descartado.
        private static object? Clonar(JsonElement elemento) =>
            elemento.ValueKind == JsonValueKind.Undefined ? null : elemento.Clone();

        private static string Mascarar(string? valor)
        {
            if (string.IsNullOrWhiteSpace(valor)) return "(vazio)";
            return valor.Length <= 4 ? valor : new string('*', valor.Length - 4) + valor[^4..];
        }
    }
}
