using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace APIBack.AssistenteComercial.Audio
{
    public interface IAudioService
    {
        /// <summary>Contrato com o pipeline do 07: audio de funcionario verificado. Idempotente por mensagem da Meta.</summary>
        Task<EntradaAudio> IngerirAsync(AudioRecebido audio, CancellationToken ct);
        /// <summary>Resposta de texto curta (SIM/NAO) do vendedor ao resumo enviado. Devolve o texto de retorno ou nulo se nao era resposta.</summary>
        Task<string?> ResponderTextoAsync(int usuarioId, Guid canalId, string texto, CancellationToken ct);
        Task<IReadOnlyList<EntradaResumoDto>> ListarAsync(int usuarioId, string? estado, int limite, CancellationToken ct);
        Task<EntradaDetalheDto> ObterAsync(int usuarioId, Guid id, CancellationToken ct);
        Task<ResultadoConfirmacao> ConfirmarAsync(int usuarioId, Guid id, ConfirmarRequest? request, string? chaveIdempotencia, CancellationToken ct);
        Task<EntradaDetalheDto> RejeitarAsync(int usuarioId, Guid id, string? motivo, CancellationToken ct);
        Task<EntradaDetalheDto> ReprocessarAsync(int usuarioId, Guid id, CancellationToken ct);
        Task<PreVisitaDto> PreVisitaAsync(int usuarioId, Guid oportunidadeId, CancellationToken ct);
        Task<bool> ConsentimentoAsync(int usuarioId, CancellationToken ct);
        Task ConsentirAsync(int usuarioId, CancellationToken ct);
        Task RevogarConsentimentoAsync(int usuarioId, CancellationToken ct);
        /// <summary>Processa uma entrada ja reservada (lease) pelo worker.</summary>
        Task ProcessarAsync(EntradaAudio entrada, CancellationToken ct);
    }

    public sealed class AudioService : IAudioService
    {
        public const string VersaoTextoConsentimento = "2026-11-audio-v1";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private static readonly string[] Revisaveis = { EstadoEntrada.AguardandoRevisao };

        private readonly IAudioStore _store;
        private readonly IAudioCrm _crm;
        private readonly IVerificacaoFuncionario _funcionarios;
        private readonly IMidiaAudio _midia;
        private readonly ITranscritor _transcritor;
        private readonly IInterpretador _interpretador;
        private readonly IRetornoWhatsapp _retorno;
        private readonly IOptionsMonitor<AssistenteAudioOptions> _opcoes;
        private readonly TimeProvider _relogio;
        private readonly ILogger<AudioService> _logger;

        public AudioService(IAudioStore store, IAudioCrm crm, IVerificacaoFuncionario funcionarios, IMidiaAudio midia, ITranscritor transcritor,
            IInterpretador interpretador, IRetornoWhatsapp retorno, IOptionsMonitor<AssistenteAudioOptions> opcoes, TimeProvider relogio,
            ILogger<AudioService> logger)
        {
            _store = store; _crm = crm; _funcionarios = funcionarios; _midia = midia; _transcritor = transcritor; _interpretador = interpretador;
            _retorno = retorno; _opcoes = opcoes; _relogio = relogio; _logger = logger;
        }

        private DateTime Agora => _relogio.GetUtcNow().UtcDateTime;
        private AssistenteAudioOptions Opcoes => _opcoes.CurrentValue;

        // ---- acesso ----

        private async Task GarantirFuncionarioAsync(int usuarioId, CancellationToken ct)
        {
            if (!await _funcionarios.VerificadoAsync(usuarioId, ct) || !await _funcionarios.EquipeCrmAsync(usuarioId, ct))
                throw new AssistenteAudioException(403, CodigoAudio.NaoVerificado, "Somente funcionario do CRM com WhatsApp verificado usa o assistente.");
        }

        private async Task<EntradaAudio> DaPessoaAsync(int usuarioId, Guid id, CancellationToken ct)
        {
            await GarantirFuncionarioAsync(usuarioId, ct);
            var e = await _store.ObterAsync(id, ct);
            // Entrada de outra pessoa e igual a inexistente: nao confirma que ela existe.
            return e != null && e.UsuarioId == usuarioId ? e : throw new AssistenteAudioException(404, CodigoAudio.NaoEncontrada, "Entrada nao encontrada.");
        }

        private static PropostaVisita? LerProposta(EntradaAudio e) =>
            string.IsNullOrWhiteSpace(e.PropostaJson) ? null : JsonSerializer.Deserialize<PropostaVisita>(e.PropostaJson, Json);

        private static List<ResultadoAlteracao> LerResultados(EntradaAudio e) =>
            string.IsNullOrWhiteSpace(e.ResultadoJson) ? new() : JsonSerializer.Deserialize<List<ResultadoAlteracao>>(e.ResultadoJson, Json) ?? new();

        // ---- ingresso ----

        public async Task<EntradaAudio> IngerirAsync(AudioRecebido a, CancellationToken ct)
        {
            await GarantirFuncionarioAsync(a.UsuarioId, ct);
            var nova = new EntradaAudio
            {
                Id = Guid.NewGuid(), WaMensagemId = a.WaMensagemId, UsuarioId = a.UsuarioId, CanalId = a.CanalId, LojaGestaoId = a.LojaGestaoId,
                PhoneNumberId = a.PhoneNumberId, NumeroCanalE164 = a.NumeroCanalE164, TelefoneE164 = a.TelefoneE164, MidiaId = a.MidiaId,
                MidiaMime = a.Mime, RecebidaEm = a.RecebidaEmUtc
            };
            var (entrada, criada) = await _store.CriarAsync(nova, ct);
            if (!criada)
            {
                _logger.LogInformation("[assistente.audio] ev=duplicada_meta wa={Wa} entrada={Entrada}", a.WaMensagemId, entrada.Id);
                return entrada;
            }

            if (!await _store.ConsentimentoAtivoAsync(a.UsuarioId, ct))
                await EncerrarAsync(entrada, EstadoEntrada.Bloqueada, "consentimento", CodigoAudio.ConsentimentoAusente,
                    "O vendedor ainda nao autorizou o uso de audio no assistente.", avisar: true, ct);
            else if (ConfigFaltando() is { } faltando)
                await EncerrarAsync(entrada, EstadoEntrada.Falha, "configuracao", CodigoAudio.IaIndisponivel, faltando, avisar: true, ct);
            return entrada;
        }

        /// <summary>Texto explicando o que falta, ou nulo se a configuracao esta completa e ligada.</summary>
        private string? ConfigFaltando()
        {
            var o = Opcoes;
            if (!o.Habilitado) return "Assistente de audio desabilitado (CrmAssistente:Audio:Habilitado=false). Nenhum audio e enviado a fornecedor externo.";
            var f = o.Faltando();
            return f.Count == 0 ? null : "Configuracao obrigatoria ausente em CrmAssistente:Audio: " + string.Join(", ", f) + ".";
        }

        private async Task EncerrarAsync(EntradaAudio e, string estado, string etapa, string codigo, string detalhe, bool avisar, CancellationToken ct)
        {
            e.Estado = estado; e.Etapa = etapa; e.ErroCodigo = codigo; e.ErroDetalhe = detalhe; e.ProximaTentativaEm = null;
            if (!await _store.SalvarAsync(e, ct)) return;
            await _store.AuditarAsync(e.Id, estado, null, new { etapa, codigo, detalhe }, ct);
            _logger.LogWarning("[assistente.audio] ev={Estado} entrada={Entrada} etapa={Etapa} codigo={Codigo}", estado, e.Id, etapa, codigo);
            if (avisar && await _retorno.EnviarAsync(e, AudioRegras.TextoErroWhatsapp(codigo), ct))
            {
                e.RetornoEnviadoEm = Agora;
                await _store.SalvarAsync(e, ct);
            }
        }

        // ---- pipeline assincrono ----

        public async Task ProcessarAsync(EntradaAudio e, CancellationToken ct)
        {
            var o = Opcoes;
            if (ConfigFaltando() is { } faltando) { await EncerrarAsync(e, EstadoEntrada.Falha, "configuracao", CodigoAudio.IaIndisponivel, faltando, true, ct); return; }
            if (!await _store.ConsentimentoAtivoAsync(e.UsuarioId, ct))
            { await EncerrarAsync(e, EstadoEntrada.Bloqueada, "consentimento", CodigoAudio.ConsentimentoAusente, "Consentimento ausente ou revogado.", true, ct); return; }

            var reservou = false;
            try
            {
                // Custo do pior caso reservado ANTES de qualquer chamada externa; estourou o teto, nada e enviado.
                var reserva = e.Transcricao == null ? AudioRegras.CustoReserva(o) : AudioRegras.CustoInterpretacao(o, AudioRegras.TokensEntradaMaximos(o), o.MaxTokensSaida!.Value);
                e.CustoEstimado = reserva;
                if (!await _store.ReservarCustoAsync(e.Id, Agora, reserva, o.TetoMensalCusto!.Value, o.InterpretacaoProvedor!, o.InterpretacaoModelo!, ct))
                {
                    await EncerrarAsync(e, EstadoEntrada.Bloqueada, "custo", CodigoAudio.TetoExcedido, "Teto mensal de custo do assistente atingido.", true, ct);
                    return;
                }
                reservou = true;

                if (e.Transcricao == null && !await TranscreverAsync(e, o, ct)) return;
                await InterpretarAsync(e, o, ct);
            }
            catch (AssistenteAudioException ex)
            {
                await FalharOuAgendarAsync(e, o, ex.Codigo, ex.Message, ex.TentarDepois, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await FalharOuAgendarAsync(e, o, CodigoAudio.IaIndisponivel, "Tempo esgotado no fornecedor.", true, ct);
            }
            catch (OperationCanceledException)
            {
                // Desligando: a trava (lease) vence e outra rodada retoma sem perder a entrada.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[assistente.audio] ev=erro_interno entrada={Entrada}", e.Id);
                await FalharOuAgendarAsync(e, o, "ERRO_INTERNO", "Erro inesperado ao processar o audio.", true, ct);
            }
            finally
            {
                if (reservou) await _store.LiberarReservaAsync(e.Id, CancellationToken.None);
            }
        }

        private CancellationTokenSource Prazo(AssistenteAudioOptions o, CancellationToken ct)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSegundos!.Value));
            return cts;
        }

        /// <summary>false = a entrada ja foi encerrada aqui (duplicada, arquivo invalido...).</summary>
        private async Task<bool> TranscreverAsync(EntradaAudio e, AssistenteAudioOptions o, CancellationToken ct)
        {
            e.Etapa = "midia";
            if (string.IsNullOrWhiteSpace(e.MidiaId))
                throw new AssistenteAudioException(422, CodigoAudio.MidiaInvalida, "A mensagem nao trouxe referencia de midia.");

            byte[] bytes; string mime;
            using (var prazo = Prazo(o, ct))
                (bytes, mime) = await _midia.BaixarAsync(e.LojaGestaoId, e.PhoneNumberId, e.MidiaId, prazo.Token);
            if (!mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                throw new AssistenteAudioException(422, CodigoAudio.MidiaInvalida, "A midia nao e um audio.");
            if (bytes.Length > o.MaxBytes!.Value)
                throw new AssistenteAudioException(422, CodigoAudio.MidiaInvalida, $"Audio acima do limite de {o.MaxBytes} bytes.");

            e.MidiaBytes = bytes.Length; e.MidiaMime = mime;
            e.MidiaHash = Convert.ToHexString(SHA256.HashData(bytes));
            // O mesmo arquivo reenviado pela mesma pessoa nao paga transcricao de novo.
            var igual = await _store.DuplicadaPorHashAsync(e.UsuarioId, e.MidiaHash, e.Id, ct);
            if (igual != null)
            {
                e.DuplicadaDe = igual.Id;
                await EncerrarAsync(e, EstadoEntrada.Duplicada, "midia", "MIDIA_DUPLICADA", "Este audio ja foi enviado antes; veja a entrada original.", false, ct);
                return false;
            }

            e.Etapa = "transcricao";
            Transcricao t;
            using (var prazo = Prazo(o, ct))
                t = await _transcritor.TranscreverAsync(bytes, mime, prazo.Token);

            // Custo real pela duracao devolvida; se vier sem duracao, vale o pior caso (nunca subestima).
            var segundos = t.DuracaoSegundos > 0 ? t.DuracaoSegundos : o.MaxDuracaoSegundos!.Value;
            var custo = AudioRegras.CustoTranscricao(o, segundos);
            await _store.RegistrarUsoAsync(e.Id, Agora, "transcricao", o.TranscricaoProvedor!, o.TranscricaoModelo!, segundos, custo, ct);
            e.CustoReal += custo; e.DuracaoSegundos = segundos;

            if (segundos > o.MaxDuracaoSegundos!.Value)
                throw new AssistenteAudioException(422, CodigoAudio.MidiaInvalida, $"Audio acima do limite de {o.MaxDuracaoSegundos} segundos.");
            var texto = AudioRegras.Limpar(t.Texto, AudioRegras.LimiteCaracteresTranscricao(o));
            if (texto == null)
                throw new AssistenteAudioException(422, CodigoAudio.MidiaInvalida, "Nao ha fala reconhecivel no audio.");

            e.Transcricao = texto;
            return await _store.SalvarAsync(e, ct); // false = outro processo assumiu a entrada
        }

        private async Task InterpretarAsync(EntradaAudio e, AssistenteAudioOptions o, CancellationToken ct)
        {
            e.Etapa = "interpretacao";
            SaidaInterpretacao saida;
            using (var prazo = Prazo(o, ct))
                saida = await _interpretador.InterpretarAsync(e.Transcricao!, prazo.Token);
            var custo = AudioRegras.CustoInterpretacao(o, saida.TokensEntrada, saida.TokensSaida);
            await _store.RegistrarUsoAsync(e.Id, Agora, "interpretacao", o.InterpretacaoProvedor!, o.InterpretacaoModelo!, saida.TokensEntrada + saida.TokensSaida, custo, ct);
            e.CustoReal += custo;

            var proposta = AudioRegras.ValidarSaida(saida.Json, e.Transcricao!);
            if (AudioRegras.TemInstrucao(e.Transcricao)) proposta.Sinais.Add("tentativa_instrucao");
            proposta.AcoesRecusadas.AddRange(AudioRegras.AcoesProibidas(e.Transcricao));

            proposta.Oportunidade = await CasarAsync(e.UsuarioId, proposta, e.Transcricao!, ct);
            string? anterior = null;
            if (proposta.Oportunidade.OportunidadeId is { } unica)
                anterior = (await _crm.ObterAsync(unica, ct))?.ProximaAcao;
            proposta.Alteracoes = AudioRegras.MontarAlteracoes(proposta, anterior);

            e.PropostaJson = JsonSerializer.Serialize(proposta, Json);
            e.OportunidadeId = proposta.Oportunidade.OportunidadeId;
            e.Estado = EstadoEntrada.AguardandoRevisao; e.Etapa = null; e.ErroCodigo = null; e.ErroDetalhe = null; e.ProximaTentativaEm = null;
            if (!await _store.SalvarAsync(e, ct)) return;
            await _store.AuditarAsync(e.Id, "proposta_gerada", null, new { situacao = proposta.Oportunidade.Situacao, alteracoes = proposta.Alteracoes.Count, sinais = proposta.Sinais }, ct);

            var empresa = proposta.Oportunidade.Candidatos.FirstOrDefault(c => c.Id == proposta.Oportunidade.OportunidadeId)?.Empresa;
            if (await _retorno.EnviarAsync(e, AudioRegras.ResumoWhatsapp(e, proposta, empresa), ct))
            {
                e.RetornoEnviadoEm = Agora;
                await _store.SalvarAsync(e, ct);
            }
        }

        /// <summary>Match so entre as oportunidades do proprio vendedor; o historico (so das candidatas) refina o desempate.</summary>
        private async Task<ResultadoMatch> CasarAsync(int usuarioId, PropostaVisita proposta, string transcricao, CancellationToken ct)
        {
            var visiveis = await _crm.OportunidadesDoUsuarioAsync(usuarioId, ct);
            var primeira = AudioRegras.Resolver(proposta, transcricao, visiveis);
            if (primeira.Candidatos.Count == 0) return primeira;
            var comHistorico = new List<OportunidadeParaMatch>();
            foreach (var c in primeira.Candidatos)
            {
                var o = visiveis.First(v => v.Id == c.Id);
                var hist = string.Join(' ', await _crm.HistoricoAsync(c.Id, 20, ct));
                comHistorico.Add(o with { Historico = hist });
            }
            return AudioRegras.Resolver(proposta, transcricao, comHistorico);
        }

        private async Task FalharOuAgendarAsync(EntradaAudio e, AssistenteAudioOptions o, string codigo, string detalhe, bool transitorio, CancellationToken ct)
        {
            e.Tentativas++;
            if (transitorio && e.Tentativas < o.MaxTentativas!.Value)
            {
                // Entrada volta para a fila com espera crescente; transcricao ja feita e mantida (nao paga de novo).
                e.Estado = EstadoEntrada.Recebido; e.ErroCodigo = codigo; e.ErroDetalhe = detalhe;
                e.ProximaTentativaEm = Agora.AddSeconds(o.AtrasoTentativaSegundos * Math.Pow(2, e.Tentativas - 1));
                if (await _store.SalvarAsync(e, ct))
                    await _store.AuditarAsync(e.Id, "nova_tentativa_agendada", null, new { e.Etapa, codigo, e.Tentativas, e.ProximaTentativaEm }, ct);
                return;
            }
            await EncerrarAsync(e, EstadoEntrada.Falha, e.Etapa ?? "processamento", codigo, detalhe, avisar: true, ct);
        }

        // ---- consulta ----

        public async Task<IReadOnlyList<EntradaResumoDto>> ListarAsync(int usuarioId, string? estado, int limite, CancellationToken ct)
        {
            await GarantirFuncionarioAsync(usuarioId, ct);
            return (await _store.ListarAsync(usuarioId, estado, limite <= 0 ? 30 : limite, ct)).Select(Resumo).ToList();
        }

        private static EntradaResumoDto Resumo(EntradaAudio e)
        {
            var p = LerProposta(e);
            var empresa = p?.Oportunidade.Candidatos.FirstOrDefault(c => c.Id == e.OportunidadeId)?.Empresa;
            return new EntradaResumoDto(e.Id, e.Estado, e.Etapa, e.ErroCodigo, e.ErroDetalhe, p?.Resumo, empresa, p?.Oportunidade.Situacao ?? "nenhum",
                p?.Alteracoes.Count ?? 0, e.RecebidaEm, e.AtualizadaEm);
        }

        public async Task<EntradaDetalheDto> ObterAsync(int usuarioId, Guid id, CancellationToken ct) => Detalhe(await DaPessoaAsync(usuarioId, id, ct));

        private static EntradaDetalheDto Detalhe(EntradaAudio e) => new(
            e.Id, e.WaMensagemId, e.Estado, e.Etapa, e.ErroCodigo, e.ErroDetalhe, e.Tentativas, e.ProximaTentativaEm, e.RecebidaEm, e.AtualizadaEm,
            e.DuracaoSegundos, e.CustoEstimado, e.CustoReal, e.Transcricao, LerProposta(e), e.OportunidadeId, e.DuplicadaDe,
            string.IsNullOrWhiteSpace(e.ResultadoJson) ? null : JsonSerializer.Deserialize<JsonElement>(e.ResultadoJson));

        public async Task<PreVisitaDto> PreVisitaAsync(int usuarioId, Guid oportunidadeId, CancellationToken ct)
        {
            await GarantirFuncionarioAsync(usuarioId, ct);
            var o = await OportunidadeAutorizadaAsync(usuarioId, oportunidadeId, ct);
            return new PreVisitaDto(o.Id, o.Empresa, o.Contato, o.ProximaAcao, await _crm.HistoricoAsync(o.Id, 15, ct));
        }

        private async Task<OportunidadeCrm> OportunidadeAutorizadaAsync(int usuarioId, Guid id, CancellationToken ct)
        {
            var o = await _crm.ObterAsync(id, ct);
            if (o == null || o.ResponsavelUsuarioId != usuarioId || !string.Equals(o.Status, "ativo", StringComparison.OrdinalIgnoreCase))
                throw new AssistenteAudioException(403, CodigoAudio.SemAcesso, "Voce so acessa oportunidades ativas em que e o responsavel.");
            return o;
        }

        // ---- confirmar / rejeitar / reprocessar ----

        public async Task<ResultadoConfirmacao> ConfirmarAsync(int usuarioId, Guid id, ConfirmarRequest? request, string? chave, CancellationToken ct)
        {
            request ??= new ConfirmarRequest();
            var e = await DaPessoaAsync(usuarioId, id, ct);
            chave = string.IsNullOrWhiteSpace(chave) ? null : chave.Trim();

            if (e.Estado == EstadoEntrada.Confirmada)
            {
                if (chave != null && chave == e.ChaveConfirmacao && e.OportunidadeId is { } opp)
                    return new ResultadoConfirmacao(e.Id, opp, LerResultados(e)); // repeticao do mesmo pedido: mesma resposta
                throw new AssistenteAudioException(409, chave != null && e.ChaveConfirmacao != null ? CodigoAudio.ChaveReutilizada : CodigoAudio.JaProcessada,
                    "Esta entrada ja foi confirmada.");
            }
            if (e.Estado is EstadoEntrada.Rejeitada or EstadoEntrada.Aplicando or EstadoEntrada.Duplicada or EstadoEntrada.Expirada)
            {
                if (!(e.Estado == EstadoEntrada.Aplicando && chave != null && chave == e.ChaveConfirmacao))
                    throw new AssistenteAudioException(409, CodigoAudio.JaProcessada, $"Esta entrada esta {e.Estado.Replace('_', ' ')}.");
            }
            else if (e.Estado != EstadoEntrada.AguardandoRevisao)
                throw new AssistenteAudioException(422, CodigoAudio.Incompleta, "Esta entrada ainda nao tem proposta para revisar.");

            var proposta = LerProposta(e) ?? throw new AssistenteAudioException(422, CodigoAudio.Incompleta, "Esta entrada nao tem proposta.");

            // Oportunidade: ambiguidade exige escolha explicita; a escolha precisa ser uma oportunidade do proprio vendedor.
            var oportunidadeId = request.OportunidadeId ?? (proposta.Oportunidade.Situacao == "unico" ? proposta.Oportunidade.OportunidadeId : null);
            if (oportunidadeId == null)
                throw new AssistenteAudioException(422, CodigoAudio.Ambigua, "Escolha a oportunidade antes de confirmar.",
                    new { situacao = proposta.Oportunidade.Situacao, candidatos = proposta.Oportunidade.Candidatos });
            var oportunidade = await OportunidadeAutorizadaAsync(usuarioId, oportunidadeId.Value, ct);

            // Aprovacao e edicao: so ids que existem na proposta, so tipos conhecidos, texto revalidado pelo servidor.
            var aprovadas = (request.AlteracoesAprovadas ?? new()).Distinct().ToList();
            var porId = proposta.Alteracoes.ToDictionary(a => a.Id);
            var inexistente = aprovadas.Where(a => !porId.ContainsKey(a)).ToList();
            if (inexistente.Count > 0)
                throw new AssistenteAudioException(422, CodigoAudio.AlteracaoInvalida, "Alteracao desconhecida: " + string.Join(", ", inexistente));
            var finais = new Dictionary<string, AlteracaoProposta>();
            foreach (var a in aprovadas)
            {
                var original = porId[a];
                if (!TipoAlteracao.Todos.Contains(original.Tipo))
                    throw new AssistenteAudioException(422, CodigoAudio.AlteracaoInvalida, $"Tipo de alteracao nao permitido: {original.Tipo}.");
                var edicao = request.Edicoes?.FirstOrDefault(x => x.Id == a);
                finais[a] = edicao == null ? original : original with { Valor = AudioRegras.ValidarEdicao(original, edicao.Valor) };
            }
            if (request.Edicoes?.Any(x => !finais.ContainsKey(x.Id)) == true)
                throw new AssistenteAudioException(422, CodigoAudio.AlteracaoInvalida, "So alteracoes aprovadas podem ser editadas.");

            // O dado mudou desde a proposta? Nao sobrescreve em silencio.
            foreach (var a in finais.Values.Where(x => x.Tipo == TipoAlteracao.ProximaAcao && x.Anterior != null && proposta.Oportunidade.OportunidadeId == oportunidadeId))
                if (AudioRegras.Normalizar(oportunidade.ProximaAcao) != AudioRegras.Normalizar(a.Anterior))
                    throw new AssistenteAudioException(409, CodigoAudio.OportunidadeAlterada, "A proxima acao da oportunidade mudou depois do audio. Reprocesse ou revise.",
                        new { atual = oportunidade.ProximaAcao, esperado = a.Anterior });

            var retomando = e.Estado == EstadoEntrada.Aplicando;
            if (!retomando && !await _store.TransicionarAsync(e.Id, Revisaveis, EstadoEntrada.Aplicando, chave, ct))
                throw new AssistenteAudioException(409, CodigoAudio.JaProcessada, "Esta entrada acabou de ser processada por outra requisicao.");

            var feitas = LerResultados(e).Where(r => r.Situacao == "aplicada").ToDictionary(r => r.Id);
            var resultados = new List<ResultadoAlteracao>(feitas.Values);
            try
            {
                var edicoes = finais.Values.Where(f => !porId[f.Id].Equals(f)).Select(f => new { f.Id, antes = porId[f.Id].Valor, depois = f.Valor }).ToList();
                if (edicoes.Count > 0) await _store.AuditarAsync(e.Id, "editada", usuarioId, edicoes, ct);

                foreach (var alt in finais.Values.Where(f => !feitas.ContainsKey(f.Id)))
                {
                    if (alt.Tipo == TipoAlteracao.NotaHistorico)
                        await _crm.AdicionarNotaAsync(oportunidade.Id, usuarioId, "Visita registrada por audio", $"{alt.Valor} [assistente audio {e.Id.ToString("N")[..8]}]", ct);
                    else
                        await _crm.DefinirProximaAcaoAsync(oportunidade.Id, usuarioId, alt.Valor, ct);
                    resultados.Add(new ResultadoAlteracao(alt.Id, alt.Tipo, "aplicada", alt.Valor));
                    e.ResultadoJson = JsonSerializer.Serialize(resultados, Json);
                    await _store.SalvarResultadoParcialAsync(e.Id, e.ResultadoJson, ct);
                }
            }
            catch
            {
                // Volta a revisao conservando o que ja foi aplicado: repetir a confirmacao pula essas e termina o resto.
                await _store.TransicionarAsync(e.Id, new[] { EstadoEntrada.Aplicando }, EstadoEntrada.AguardandoRevisao, null, CancellationToken.None);
                throw;
            }

            foreach (var a in aprovadas.Where(a => resultados.All(r => r.Id != a))) resultados.Add(new ResultadoAlteracao(a, porId[a].Tipo, "ignorada", null));
            foreach (var a in proposta.Alteracoes.Where(a => !aprovadas.Contains(a.Id))) resultados.Add(new ResultadoAlteracao(a.Id, a.Tipo, "nao_aprovada", null));

            var atual = await _store.ObterAsync(e.Id, ct) ?? e;
            atual.Estado = EstadoEntrada.Confirmada; atual.OportunidadeId = oportunidade.Id; atual.ChaveConfirmacao = chave;
            atual.ResultadoJson = JsonSerializer.Serialize(resultados, Json);
            if (!await _store.SalvarAsync(atual, ct))
                throw new AssistenteAudioException(409, CodigoAudio.JaProcessada, "A entrada mudou durante a confirmacao.");
            await _store.AuditarAsync(e.Id, "confirmada", usuarioId, new { oportunidade = oportunidade.Id, aprovadas, resultados = resultados.Select(r => new { r.Id, r.Situacao }) }, ct);
            return new ResultadoConfirmacao(e.Id, oportunidade.Id, resultados);
        }

        public async Task<EntradaDetalheDto> RejeitarAsync(int usuarioId, Guid id, string? motivo, CancellationToken ct)
        {
            var e = await DaPessoaAsync(usuarioId, id, ct);
            var de = new[] { EstadoEntrada.AguardandoRevisao, EstadoEntrada.Falha, EstadoEntrada.Bloqueada, EstadoEntrada.Recebido };
            if (!await _store.TransicionarAsync(e.Id, de, EstadoEntrada.Rejeitada, null, ct))
                throw new AssistenteAudioException(409, CodigoAudio.JaProcessada, "Esta entrada nao pode mais ser rejeitada.");
            await _store.AuditarAsync(e.Id, "rejeitada", usuarioId, new { motivo = AudioRegras.Limpar(motivo, 300) }, ct);
            return Detalhe((await _store.ObterAsync(e.Id, ct))!);
        }

        public async Task<EntradaDetalheDto> ReprocessarAsync(int usuarioId, Guid id, CancellationToken ct)
        {
            var e = await DaPessoaAsync(usuarioId, id, ct);
            if (e.Estado is not (EstadoEntrada.Falha or EstadoEntrada.Bloqueada or EstadoEntrada.AguardandoRevisao or EstadoEntrada.Expirada))
                throw new AssistenteAudioException(409, CodigoAudio.JaProcessada, $"Entrada {e.Estado.Replace('_', ' ')} nao pode ser reprocessada.");
            if (ConfigFaltando() is { } faltando)
                throw new AssistenteAudioException(503, CodigoAudio.IaIndisponivel, faltando);
            if (!await _store.ConsentimentoAtivoAsync(usuarioId, ct))
                throw new AssistenteAudioException(422, CodigoAudio.ConsentimentoAusente, "Autorize o uso de audio no assistente antes de reprocessar.");
            if (await _store.CustoDoMesAsync(Agora, ct) >= Opcoes.TetoMensalCusto!.Value)
                throw new AssistenteAudioException(503, CodigoAudio.TetoExcedido, "Teto mensal de custo do assistente atingido.");
            if (e.MidiaId == null && e.Transcricao == null)
                throw new AssistenteAudioException(422, CodigoAudio.MidiaInvalida, "A entrada nao guarda midia nem transcricao para reprocessar.");

            var de = new[] { EstadoEntrada.Falha, EstadoEntrada.Bloqueada, EstadoEntrada.AguardandoRevisao, EstadoEntrada.Expirada };
            if (!await _store.TransicionarAsync(e.Id, de, EstadoEntrada.Recebido, null, ct))
                throw new AssistenteAudioException(409, CodigoAudio.JaProcessada, "A entrada mudou de estado; atualize a lista.");
            await _store.AuditarAsync(e.Id, "reprocessada", usuarioId, new { estadoAnterior = e.Estado }, ct);
            return Detalhe((await _store.ObterAsync(e.Id, ct))!);
        }

        // ---- consentimento ----

        public async Task<bool> ConsentimentoAsync(int usuarioId, CancellationToken ct) { await GarantirFuncionarioAsync(usuarioId, ct); return await _store.ConsentimentoAtivoAsync(usuarioId, ct); }
        public async Task ConsentirAsync(int usuarioId, CancellationToken ct) { await GarantirFuncionarioAsync(usuarioId, ct); await _store.RegistrarConsentimentoAsync(usuarioId, VersaoTextoConsentimento, ct); }
        public async Task RevogarConsentimentoAsync(int usuarioId, CancellationToken ct) { await GarantirFuncionarioAsync(usuarioId, ct); await _store.RevogarConsentimentoAsync(usuarioId, ct); }

        // ---- resposta por WhatsApp ----

        public async Task<string?> ResponderTextoAsync(int usuarioId, Guid canalId, string texto, CancellationToken ct)
        {
            var resposta = AudioRegras.InterpretarResposta(texto);
            if (resposta == null) return null;
            await GarantirFuncionarioAsync(usuarioId, ct);
            var e = await _store.UltimaAguardandoAsync(usuarioId, canalId, Agora.AddHours(-24), ct);
            if (e == null) return null;
            if (resposta == "nao")
            {
                await RejeitarAsync(usuarioId, e.Id, "recusado por WhatsApp", ct);
                return "Ok, descartei. Nada foi alterado no CRM.";
            }
            var p = LerProposta(e);
            if (p == null || p.Oportunidade.Situacao != "unico" || p.Alteracoes.Count == 0)
                return "Ainda preciso que voce escolha a oportunidade no painel (CRM > Assistente). Nada foi alterado.";
            try
            {
                var r = await ConfirmarAsync(usuarioId, e.Id, new ConfirmarRequest { AlteracoesAprovadas = p.Alteracoes.Select(a => a.Id).ToList() }, "wa-" + e.WaMensagemId, ct);
                return $"Registrado em {p.Oportunidade.Candidatos.FirstOrDefault()?.Empresa ?? "a oportunidade"}: {r.Alteracoes.Count(x => x.Situacao == "aplicada")} alteracao(oes).";
            }
            catch (AssistenteAudioException ex)
            {
                return ex.Codigo == CodigoAudio.OportunidadeAlterada
                    ? "A proxima acao mudou desde o audio. Revise no painel (CRM > Assistente). Nada foi alterado."
                    : $"Nao apliquei: {ex.Message}";
            }
        }
    }
}
