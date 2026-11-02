using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Services;
using APIBack.IdentidadeWhatsapp;
using APIBack.Service.Interface;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento
{
    /// <summary>Como terminou o processamento de um evento do webhook.</summary>
    /// <param name="Codigo">So em "bloqueado": 403, 409 ou 422 (<see cref="RotaBloqueada"/>).</param>
    /// <param name="Categoria">So na loja Gestao: <see cref="CategoriaRemetente"/>.</param>
    public sealed record ResultadoEvento(string Resultado, string? Motivo = null, int? Codigo = null, string? Categoria = null)
    {
        public static ResultadoEvento Processado(string? categoria = null) => new("processado", Categoria: categoria);
        public static ResultadoEvento Ignorado(string motivo) => new("ignorado", motivo);
        /// <summary>Ainda nao da para concluir (ex.: o recibo chegou antes de a mensagem ser vinculada): tenta de novo logo.</summary>
        public static ResultadoEvento TentarDeNovo(string motivo) => new("tentar_de_novo", motivo);
        /// <summary>A rota falhou na conferencia de seguranca: nada foi entregue e o evento NAO conta como processado.</summary>
        public static ResultadoEvento Bloqueado(int codigo, string motivo, string? categoria = null) => new("bloqueado", motivo, codigo, categoria);
    }

    /// <summary>
    /// Contrato do bloqueio de rota (wa_evento.rota_codigo + motivo "codigo: detalhe"). 403 = sem permissao para aquele destino;
    /// 409 = o destino mudou depois do ingresso; 422 = nao da para rotear com seguranca.
    /// </summary>
    public static class RotaBloqueada
    {
        public const int Proibido = 403;
        public const int Conflito = 409;
        public const int NaoRoteavel = 422;

        /// <summary>403: o numero foi desativado/desconectado depois do ingresso.</summary>
        public const string AcessoRevogado = "acesso_revogado";
        /// <summary>403: evento de uma WABA para numero autorizado de outra.</summary>
        public const string WabaIncorreta = "waba_incorreta";
        /// <summary>403: chegou pela URL de webhook de um canal para o numero de outro.</summary>
        public const string CanalDoWebhookDiferente = "canal_do_webhook_diferente";
        /// <summary>403: funcionario resolvido como verificado, mas a autorizacao caiu antes de entregar.</summary>
        public const string AutorizacaoRevogada = "autorizacao_revogada";
        /// <summary>409: o numero passou para outra loja depois do ingresso (troca A -> B com fila pendente).</summary>
        public const string DestinoAlterado = "destino_alterado";
        /// <summary>409: o canal capturado nao existe mais ou trocou de Phone Number ID.</summary>
        public const string CanalAlterado = "canal_alterado";
        /// <summary>409: o numero mudou de finalidade (geral <-> gestao) depois do ingresso.</summary>
        public const string FinalidadeAlterada = "finalidade_alterada";
        /// <summary>422: nenhum numero ativo tinha este Phone Number ID no ingresso.</summary>
        public const string CanalDesconhecido = "canal_desconhecido";
        /// <summary>422: rota gestao sem o servico de identidade, ou funcionario verificado sem assistente interno disponivel.</summary>
        public const string GestaoIndisponivel = "gestao_indisponivel";
    }

    public interface IWaEventoProcessor
    {
        Task<ResultadoEvento> ProcessarAsync(WaEvento evento);
    }

    /// <summary>
    /// Processa um evento ja gravado: confere o destino capturado no ingresso (canal + loja) e entao entrega a mensagem ao
    /// atendimento, ou aplica o recibo (entregue/lida/falhou) a mensagem enviada. Cada passo deixa uma linha no log.
    /// </summary>
    public sealed class WaEventoProcessor : IWaEventoProcessor
    {
        private const int TentativasParaRecibo = 4;

        private readonly ICanalRepository _canais;
        private readonly IMessageService _mensagens;
        private readonly IPipelineDeMensagem _pipeline;
        private readonly ILogger<WaEventoProcessor> _logger;
        private readonly ConexaoMeta.IConexaoMetaRepository? _conexoes;
        private readonly IIdentidadeWhatsappService? _identidade;

        public WaEventoProcessor(
            ICanalRepository canais, IMessageService mensagens, IPipelineDeMensagem pipeline, ILogger<WaEventoProcessor> logger,
            ConexaoMeta.IConexaoMetaRepository? conexoes = null, IIdentidadeWhatsappService? identidade = null)
        {
            _canais = canais;
            _mensagens = mensagens;
            _pipeline = pipeline;
            _logger = logger;
            _conexoes = conexoes;
            _identidade = identidade;
        }

        public Task<ResultadoEvento> ProcessarAsync(WaEvento evento) => evento.Tipo switch
        {
            TipoEvento.Status => ProcessarStatusAsync(evento),
            TipoEvento.Conta => ProcessarContaAsync(evento),
            TipoEvento.Eco => ProcessarEcoAsync(evento),
            _ => ProcessarMensagemAsync(evento)
        };

        // ---- conta (account_update) ---------------------------------------------------------------------------

        /// <summary>
        /// PARTNER_REMOVED: o restaurante tirou o app ZippyGo da conta (ou desconectou no app WhatsApp Business). O Zippy para de usar o
        /// token e marca os numeros autorizados daquela WABA com erro; nada e apagado e outras origens nao mudam.
        /// </summary>
        private async Task<ResultadoEvento> ProcessarContaAsync(WaEvento evento)
        {
            using var documento = JsonDocument.Parse(evento.PayloadJson);
            var raiz = documento.RootElement;
            var waba = Texto(raiz, "waba_id");
            var tipo = raiz.TryGetProperty("value", out var valor) ? Texto(valor, "event") : null;

            if (tipo != "PARTNER_REMOVED" || string.IsNullOrWhiteSpace(waba))
            {
                _logger.LogInformation("[wa.in] ev=conta_evento evento={Evento} waba={Waba}", tipo, waba);
                return ResultadoEvento.Ignorado($"account_update {tipo ?? "sem evento"} sem acao no Zippy");
            }

            if (_conexoes == null) return ResultadoEvento.TentarDeNovo("repositorio de conexoes indisponivel");

            var canais = await _conexoes.RevogarPorWabaAsync(waba, null,
                "O restaurante removeu o acesso do Zippy na Meta. Use \"Reconectar\" para autorizar de novo.");
            _logger.LogWarning("[wa.in] ev=acesso_removido_pela_meta waba={Waba} canais_afetados={Canais}", waba, canais);
            return ResultadoEvento.Processado();
        }

        // ---- eco do app WhatsApp Business (coexistencia) ------------------------------------------------------

        private async Task<ResultadoEvento> ProcessarEcoAsync(WaEvento evento)
        {
            var canal = string.IsNullOrWhiteSpace(evento.PhoneNumberId) ? null : await _canais.ObterAtivoPorPhoneNumberIdAsync(evento.PhoneNumberId);
            _logger.LogInformation("[wa.in] ev=eco_app_business wa={Wa} canal={Canal} loja={Loja}", evento.Chave, canal?.Id, canal?.IdEstabelecimento);
            // Nunca entra no pipeline: o restaurante respondeu pelo celular e o robo nao pode responder de novo.
            return ResultadoEvento.Ignorado(canal == null
                ? "eco de numero desconhecido"
                : "eco do app WhatsApp Business: guardado, sem resposta automatica (exibicao no chat ainda nao implementada)");
        }

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

            var wabaDoEvento = Texto(raiz, "waba_id");
            var valor = new WebhookChangeValueDto { Metadados = metadados, Contatos = contatos };

            // Destino capturado no ingresso: so confere, nunca recalcula pelo Phone Number ID.
            if (evento.RotaCapturadaEm.HasValue)
            {
                return await ProcessarComDestinoCapturadoAsync(evento, mensagem, valor, phoneNumberId, display, wabaDoEvento, relogio);
            }

            // Legado: evento gravado sem destino (antes da migration 20261127_07 ou ingestor sem repositorio de canais).
            _logger.LogWarning("[wa.in] ev=rota_legada wa={Wa} phone_number_id={PhoneNumberId}", evento.Chave, phoneNumberId);
            var canal = string.IsNullOrWhiteSpace(phoneNumberId) ? null : await _canais.ObterAtivoPorPhoneNumberIdAsync(phoneNumberId);
            if (canal == null)
            {
                // O caso que mais confundiu na pratica: a Meta entrega por um ID que nenhuma loja cadastrou.
                _logger.LogWarning(
                    "[wa.in] ev=ignorada motivo=canal_desconhecido wa={Wa} phone_number_id={PhoneNumberId} display={Display} " +
                    "dica=\"cadastre este Phone Number ID na loja (Gestao > numeros de WhatsApp)\"", evento.Chave, phoneNumberId, display);
                return ResultadoEvento.Ignorado($"canal desconhecido (phone_number_id={phoneNumberId}, display={display})");
            }

            if (evento.IdCanal.HasValue && evento.IdCanal.Value != canal.Id)
            {
                // Chegou pela URL de webhook de um canal, mas o phone_number_id e de outro. O webhook ja confere isso ao gravar;
                // aqui e a segunda trava: um cliente nunca injeta mensagem na conversa de outra loja.
                _logger.LogWarning(
                    "[wa.in] ev=ignorada motivo=canal_do_webhook_diferente wa={Wa} canal_webhook={CanalWebhook} canal_dono={CanalDono}",
                    evento.Chave, evento.IdCanal, canal.Id);
                return ResultadoEvento.Ignorado($"evento do webhook do canal {evento.IdCanal} para o numero do canal {canal.Id}");
            }

            if (WabaDiferente(canal, wabaDoEvento))
            {
                // Webhook global: a entrada diz de qual WABA veio. Numero autorizado de outra WABA nao recebe (WABA compartilhada nao mistura).
                _logger.LogWarning("[wa.in] ev=ignorada motivo=waba_diferente_do_canal wa={Wa} canal={Canal} waba_evento={WabaEvento}",
                    evento.Chave, canal.Id, wabaDoEvento);
                return ResultadoEvento.Ignorado($"evento da WABA {wabaDoEvento} para o canal {canal.Id} de outra WABA");
            }

            var inputLegado = await PrepararAsync(evento, mensagem, valor, phoneNumberId, display, canal, canal.IdEstabelecimento, RotaEvento.Operacional);
            await _pipeline.ExecutarAsync(inputLegado, canal);

            _logger.LogInformation("[wa.in] ev=concluida wa={Wa} canal={Canal} ms={Ms}", evento.Chave, canal.Id, relogio.ElapsedMilliseconds);
            return ResultadoEvento.Processado();
        }

        /// <summary>
        /// Caminho normal: o destino (canal + loja) foi gravado no ingresso. Se o canal mudou depois (outra loja, outra finalidade,
        /// desativado, outro Phone Number ID), a mensagem NAO vai para o destino novo nem e recalculada: fica bloqueada com codigo e
        /// motivo, para diagnostico. Restaurante segue o pipeline de sempre; loja Gestao classifica o remetente antes.
        /// </summary>
        private async Task<ResultadoEvento> ProcessarComDestinoCapturadoAsync(
            WaEvento evento, WebhookMessageDto mensagem, WebhookChangeValueDto valor, string? phoneNumberId, string? display, string? wabaDoEvento,
            Stopwatch relogio)
        {
            if (evento.Rota == RotaEvento.SemCanal || !evento.IdCanalDestino.HasValue || !evento.IdEstabelecimentoDestino.HasValue)
            {
                _logger.LogWarning(
                    "[wa.in] ev=bloqueada motivo=canal_desconhecido wa={Wa} phone_number_id={PhoneNumberId} display={Display} " +
                    "dica=\"cadastre este Phone Number ID na loja (Gestao > numeros de WhatsApp)\"", evento.Chave, phoneNumberId, display);
                return Bloquear(evento, RotaBloqueada.NaoRoteavel, RotaBloqueada.CanalDesconhecido,
                    $"nenhum numero ativo tinha phone_number_id={phoneNumberId} (display={display}) no ingresso");
            }

            var lojaCapturada = evento.IdEstabelecimentoDestino.Value;
            var canal = await _canais.ObterAsync(evento.IdCanalDestino.Value);

            if (canal == null || !string.Equals(canal.PhoneNumberId, phoneNumberId, StringComparison.Ordinal))
                return Bloquear(evento, RotaBloqueada.Conflito, RotaBloqueada.CanalAlterado,
                    $"canal {evento.IdCanalDestino} removido ou com outro Phone Number ID desde o ingresso");
            if (evento.IdCanal.HasValue && evento.IdCanal.Value != canal.Id)
                return Bloquear(evento, RotaBloqueada.Proibido, RotaBloqueada.CanalDoWebhookDiferente,
                    $"evento do webhook do canal {evento.IdCanal} para o numero do canal {canal.Id}");
            if (canal.IdEstabelecimento != lojaCapturada)
                // Troca A -> B com a fila pendente: nunca entrega em B, e nao entrega em A por um numero que agora responde por B.
                return Bloquear(evento, RotaBloqueada.Conflito, RotaBloqueada.DestinoAlterado,
                    $"numero passou da loja {lojaCapturada} para {canal.IdEstabelecimento} depois do ingresso");
            if (canal.Status == StatusCanal.Inativo)
                return Bloquear(evento, RotaBloqueada.Proibido, RotaBloqueada.AcessoRevogado, $"canal {canal.Id} desativado depois do ingresso");
            if (RotaEvento.Para(canal) != evento.Rota)
                return Bloquear(evento, RotaBloqueada.Conflito, RotaBloqueada.FinalidadeAlterada,
                    $"rota capturada {evento.Rota}, rota atual {RotaEvento.Para(canal)}");
            if (WabaDiferente(canal, wabaDoEvento))
                return Bloquear(evento, RotaBloqueada.Proibido, RotaBloqueada.WabaIncorreta, $"evento da WABA {wabaDoEvento} para o canal {canal.Id} de outra WABA");

            if (evento.Rota == RotaEvento.Gestao)
            {
                return await ProcessarGestaoAsync(evento, mensagem, valor, phoneNumberId, display, canal, lojaCapturada, relogio);
            }

            // Restaurante: exatamente o fluxo de sempre.
            var input = await PrepararAsync(evento, mensagem, valor, phoneNumberId, display, canal, lojaCapturada, RotaEvento.Operacional);
            await _pipeline.ExecutarAsync(input, canal);
            _logger.LogInformation("[wa.in] ev=concluida wa={Wa} canal={Canal} ms={Ms}", evento.Chave, canal.Id, relogio.ElapsedMilliseconds);
            return ResultadoEvento.Processado();
        }

        /// <summary>
        /// Loja Gestao: quem mandou decide o fluxo, recalculado nesta mensagem (revogar, trocar telefone ou inativar corta na hora).
        /// Verificado -> assistente interno (permissoes reais); contato do CRM -> atendimento publico; desconhecido -> qualificacao;
        /// ambiguo (equipe e contato) -> pede identificacao, nada interno ate a confirmacao explicita.
        /// </summary>
        private async Task<ResultadoEvento> ProcessarGestaoAsync(
            WaEvento evento, WebhookMessageDto mensagem, WebhookChangeValueDto valor, string? phoneNumberId, string? display, CanalWhatsapp canal,
            Guid loja, Stopwatch relogio)
        {
            if (_identidade == null)
                return Bloquear(evento, RotaBloqueada.NaoRoteavel, RotaBloqueada.GestaoIndisponivel, "servico de identidade indisponivel");

            var remetente = await _identidade.ResolverRemetenteAsync(mensagem.De, simulacaoExterna: false);
            var categoria = Categoria(remetente);
            AutorizacaoWhatsapp? autorizacao = null;
            if (remetente.Destino == DestinoRemetente.AssistenteInterno)
            {
                autorizacao = await _identidade.AutorizarAsync(mensagem.De);
                if (autorizacao == null || autorizacao.UsuarioId != remetente.UsuarioId)
                    return Bloquear(evento, RotaBloqueada.Proibido, RotaBloqueada.AutorizacaoRevogada, "vinculo do funcionario caiu antes da entrega", categoria);
            }

            var input = await PrepararAsync(evento, mensagem, valor, phoneNumberId, display, canal, loja, RotaEvento.Gestao);
            var contexto = new ContextoGestao(remetente.Destino, categoria, autorizacao,
                new ReferenciaMidia(evento.MidiaId, evento.MidiaMime, evento.TipoMensagem, evento.Chave));
            var entregue = await _pipeline.ExecutarGestaoAsync(input, canal, contexto);
            if (!entregue)
                return Bloquear(evento, RotaBloqueada.NaoRoteavel, RotaBloqueada.GestaoIndisponivel, "assistente interno indisponivel", categoria);

            _logger.LogInformation("[wa.in] ev=concluida wa={Wa} canal={Canal} rota=gestao categoria={Categoria} ms={Ms}",
                evento.Chave, canal.Id, categoria, relogio.ElapsedMilliseconds);
            return ResultadoEvento.Processado(categoria);
        }

        private async Task<ConversationProcessingInput> PrepararAsync(
            WaEvento evento, WebhookMessageDto mensagem, WebhookChangeValueDto valor, string? phoneNumberId, string? display, CanalWhatsapp canal,
            Guid loja, string rota)
        {
            await _canais.MarcarRecebimentoAsync(canal.Id);

            var texto = WaMensagemTexto.Exibicao(mensagem);
            _logger.LogInformation(
                "[wa.in] ev=roteada wa={Wa} canal={Canal} loja={Loja} rota={Rota} modo={Modo} tipo={Tipo} texto=\"{Texto}\"",
                evento.Chave, canal.Id, loja, rota, canal.ModoAtendimento, mensagem.Tipo, Cortar(texto));

            DateTime? dataUtc = null;
            if (long.TryParse(mensagem.CarimboTempo, out var unix))
            {
                try { dataUtc = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime; } catch (ArgumentOutOfRangeException) { }
            }

            return new ConversationProcessingInput(
                Mensagem: mensagem,
                Texto: texto,
                PhoneNumberDisplay: display,
                PhoneNumberId: phoneNumberId,
                DataMensagemUtc: dataUtc,
                Valor: valor,
                TextoInterpretado: WaMensagemTexto.Interpretado(mensagem),
                IdEstabelecimento: loja,
                IdCanal: canal.Id);
        }

        private ResultadoEvento Bloquear(WaEvento evento, int codigo, string motivo, string detalhe, string? categoria = null)
        {
            _logger.LogWarning(
                "[wa.in] ev=bloqueada codigo={Codigo} motivo={Motivo} wa={Wa} canal_capturado={Canal} loja_capturada={Loja} rota={Rota} detalhe=\"{Detalhe}\"",
                codigo, motivo, evento.Chave, evento.IdCanalDestino, evento.IdEstabelecimentoDestino, evento.Rota, detalhe);
            return ResultadoEvento.Bloqueado(codigo, $"{motivo}: {detalhe}", categoria);
        }

        private static bool WabaDiferente(CanalWhatsapp canal, string? wabaDoEvento) =>
            canal.Origem == OrigemCanal.Autorizado && wabaDoEvento != null && canal.WabaId != null && wabaDoEvento != canal.WabaId;

        private static string Categoria(RemetenteResolvido r) => r.Destino switch
        {
            DestinoRemetente.AssistenteInterno => CategoriaRemetente.FuncionarioVerificado,
            DestinoRemetente.ConfirmarPapel => CategoriaRemetente.Ambiguo,
            DestinoRemetente.AtendimentoPublico => CategoriaRemetente.ContatoCrm,
            _ => CategoriaRemetente.Desconhecido
        };

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
