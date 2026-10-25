using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Atendimento.Templates;
using APIBack.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento.ConexaoMeta
{
    public interface IConexaoMetaService
    {
        /// <summary>O que o painel precisa para abrir o Embedded Signup (App ID, Configuration ID, modo). Nunca segredos.</summary>
        Task<ConfiguracaoConexaoMetaView> ConfiguracaoAsync(AtendimentoAtor ator, Guid estabelecimentoId);
        /// <summary>Cria a tentativa (state contra CSRF, prazo) para esta loja. <paramref name="origem"/> e o cabecalho Origin.</summary>
        Task<TentativaIniciadaView> IniciarAsync(AtendimentoAtor ator, Guid estabelecimentoId, IniciarConexaoMetaRequest request, string? origem);
        /// <summary>
        /// Recebe o fim do Embedded Signup: confere state/loja/usuario/prazo, troca o codigo (so aqui, no servidor), confere token,
        /// permissoes e WABA na Meta e grava a autorizacao. Com um numero claro, ja ativa; senao devolve os numeros para escolher.
        /// </summary>
        Task<EstadoConexaoMetaView> ConcluirAsync(AtendimentoAtor ator, Guid estabelecimentoId, ConcluirConexaoMetaRequest request, string? origem);
        /// <summary>Escolhe (ou tenta de novo) o numero de uma tentativa ja autorizada: sem repetir a autorizacao na Meta.</summary>
        Task<EstadoConexaoMetaView> AtivarAsync(AtendimentoAtor ator, Guid estabelecimentoId, AtivarNumeroMetaRequest request);
        /// <summary>Andamento da tentativa (a informada ou a mais recente da loja).</summary>
        Task<EstadoConexaoMetaView> EstadoAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid? tentativaId);
        /// <summary>Diagnostico real do canal (qualquer origem); com Reparar, refaz so as etapas seguras que faltam.</summary>
        Task<DiagnosticoConexaoView> RevalidarAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid canalId, RevalidarCanalRequest request);
        /// <summary>Nova autorizacao para um canal existente: mesmo id, conversas e configuracoes.</summary>
        Task<TentativaIniciadaView> ReconectarAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid canalId, string? origem);
        /// <summary>Desconecta do Zippy sem excluir nada na Meta e sem afetar outra loja que use a mesma WABA.</summary>
        Task<DesconexaoView> DesconectarAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid canalId);
    }

    public sealed class ConexaoMetaService : IConexaoMetaService
    {
        private const string ModuloWhatsapp = "WHATSAPP";
        private const string FeatureCoexistencia = "whatsapp_business_app_onboarding";

        private readonly ICanalRepository _canais;
        private readonly ICatalogoRepository _catalogo;
        private readonly IConexaoMetaRepository _repo;
        private readonly IMetaOnboardingApi _meta;
        private readonly ITokenProtector _token;
        private readonly ICanaisWhatsappService _canaisService;
        private readonly ICanalWebhookService _webhook;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ConexaoMetaService> _logger;
        private readonly Func<DateTime> _agora;

        public ConexaoMetaService(
            ICanalRepository canais, ICatalogoRepository catalogo, IConexaoMetaRepository repo, IMetaOnboardingApi meta, ITokenProtector token,
            ICanaisWhatsappService canaisService, ICanalWebhookService webhook, IConfiguration configuration, ILogger<ConexaoMetaService> logger,
            Func<DateTime>? agora = null)
        {
            _canais = canais;
            _catalogo = catalogo;
            _repo = repo;
            _meta = meta;
            _token = token;
            _canaisService = canaisService;
            _webhook = webhook;
            _configuration = configuration;
            _logger = logger;
            _agora = agora ?? (() => DateTime.UtcNow);
        }

        private ConfigEmbeddedSignup Config => ConfigEmbeddedSignup.Ler(_configuration);

        // ---- configuracao -------------------------------------------------------------------------------------------

        public Task<ConfiguracaoConexaoMetaView> ConfiguracaoAsync(AtendimentoAtor ator, Guid estabelecimentoId)
        {
            ServicosDaLojaService.ExigirLeitura(ator, estabelecimentoId);
            var cfg = Config;
            var disponivel = cfg.Configurado && _token.Configurado;
            var pendencias = cfg.Pendencias().ToList();
            if (!_token.Configurado) pendencias.Add("Atendimento__ChaveToken nao configurada: o servidor nao consegue guardar o token com seguranca.");
            var modo = !disponivel ? "indisponivel" : cfg.PermissoesAprovadas ? "producao" : "teste";

            return Task.FromResult(new ConfiguracaoConexaoMetaView(
                disponivel, modo, disponivel ? cfg.AppId : null, disponivel ? cfg.ConfigurationId : null, cfg.SdkVersion,
                disponivel && cfg.CoexistenciaHabilitada, ConfigEmbeddedSignup.PermissoesExigidas,
                // A loja ve o que a impede; detalhes de variavel de ambiente so para a Gestao.
                ator.Gestao || pendencias.Count == 0 ? pendencias : new List<string> { ResumoPendencia(cfg, disponivel) },
                PodeConfigurar(ator, estabelecimentoId)));
        }

        private static string ResumoPendencia(ConfigEmbeddedSignup cfg, bool disponivel) =>
            !disponivel
                ? "A conexao pela Meta ainda nao foi liberada pela Zippy. Use a conexao manual ou fale com a Zippy."
                : "A aprovacao da Meta para o app ZippyGo ainda esta em andamento: a conexao funciona so para contas de teste autorizadas.";

        // ---- iniciar / reconectar ------------------------------------------------------------------------------------

        public Task<TentativaIniciadaView> IniciarAsync(AtendimentoAtor ator, Guid estabelecimentoId, IniciarConexaoMetaRequest request, string? origem) =>
            CriarTentativaAsync(ator, estabelecimentoId, request.CanalId, request.Coexistencia, origem);

        public Task<TentativaIniciadaView> ReconectarAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid canalId, string? origem) =>
            CriarTentativaAsync(ator, estabelecimentoId, canalId, null, origem);

        private async Task<TentativaIniciadaView> CriarTentativaAsync(
            AtendimentoAtor ator, Guid estabelecimentoId, Guid? canalId, bool? coexistenciaPedida, string? origem)
        {
            ExigirDonoOuGestao(ator, "meta_iniciar", estabelecimentoId);
            var cfg = ExigirDisponivel(origem);
            if (!await _catalogo.LojaExisteAsync(estabelecimentoId)) throw new KeyNotFoundException("Estabelecimento nao encontrado.");

            CanalWhatsapp? canal = null;
            if (canalId.HasValue)
            {
                canal = await CanalDaLojaAsync(estabelecimentoId, canalId.Value);
                // O central de testes e da conta da Zippy: nunca vira autorizacao de cliente.
                if (canal.Central) throw ConexaoMetaErros.Excecao(ConexaoMetaErros.ConexaoExistente(canal.Origem), 422);
            }

            // Reconectar mantem a escolha de coexistencia do numero (desligar a coexistencia nao e algo que o Zippy faca sozinho).
            var coexistencia = coexistenciaPedida ?? canal?.Coexistencia ?? false;
            if (coexistencia && !cfg.CoexistenciaHabilitada) throw ConexaoMetaErros.Excecao(ConexaoMetaErros.CoexistenciaIndisponivel(), 422);

            var state = ConexaoMetaRegras.GerarState();
            var tentativa = new TentativaConexao
            {
                Id = Guid.NewGuid(), IdEstabelecimento = estabelecimentoId, UsuarioId = ator.UsuarioId, StateHash = ConexaoMetaRegras.HashState(state),
                Modo = canal != null ? "reconectar" : "conectar", IdCanal = canal?.Id, Coexistencia = coexistencia,
                ExpiraEm = _agora().AddMinutes(cfg.MinutosParaAutorizar)
            };

            if (!await _repo.CriarTentativaAsync(tentativa)) throw ConexaoMetaErros.Excecao(ConexaoMetaErros.TentativaEmUso(), 409);

            await _canais.RegistrarAuditoriaAsync(canal?.Id, canal != null ? "meta_reconexao_iniciada" : "meta_conexao_iniciada", null, estabelecimentoId,
                ator.UsuarioId, JsonSerializer.Serialize(new { tentativa = tentativa.Id, coexistencia }));
            _logger.LogInformation("[wa.meta] ev=tentativa_iniciada tentativa={Tentativa} loja={Loja} usuario={Usuario} modo={Modo} coexistencia={Coexistencia}",
                tentativa.Id, estabelecimentoId, ator.UsuarioId, tentativa.Modo, coexistencia);

            var extras = new Dictionary<string, object> { ["setup"] = new Dictionary<string, object>() };
            if (coexistencia) extras["featureType"] = FeatureCoexistencia;
            return new TentativaIniciadaView(tentativa.Id, state, tentativa.ExpiraEm, tentativa.Modo, extras, cfg.AppId!, cfg.ConfigurationId!, cfg.SdkVersion);
        }

        // ---- concluir --------------------------------------------------------------------------------------------------

        public async Task<EstadoConexaoMetaView> ConcluirAsync(
            AtendimentoAtor ator, Guid estabelecimentoId, ConcluirConexaoMetaRequest request, string? origem)
        {
            ExigirDonoOuGestao(ator, "meta_concluir", estabelecimentoId);
            var cfg = ExigirDisponivel(origem);
            var t = await TentativaDaLojaAsync(estabelecimentoId, request.TentativaId);

            // Mesma loja, mesmo usuario e o state que so este navegador recebeu: sem isso nada acontece (CSRF/troca de loja).
            if (t.UsuarioId != ator.UsuarioId || !ConexaoMetaRegras.StateConfere(t.StateHash, request.State))
            {
                _logger.LogWarning("[wa.meta] ev=concluir_recusado motivo=state_ou_usuario tentativa={Tentativa} loja={Loja} usuario={Usuario}",
                    t.Id, estabelecimentoId, ator.UsuarioId);
                throw ConexaoMetaErros.Excecao(ConexaoMetaErros.TentativaInvalida(), 403);
            }

            if (t.Estado != EstadoTentativa.Iniciada)
            {
                _logger.LogWarning("[wa.meta] ev=concluir_recusado motivo=repetido tentativa={Tentativa} estado={Estado}", t.Id, t.Estado);
                throw ConexaoMetaErros.Excecao(ConexaoMetaErros.TentativaEmUso(), 409);
            }

            if (t.ExpiraEm <= _agora())
            {
                await EncerrarAsync(t, EstadoTentativa.Expirada, ConexaoMetaCodigos.TentativaExpirada, "Prazo para autorizar acabou.");
                throw ConexaoMetaErros.Excecao(ConexaoMetaErros.TentativaExpirada(), 410);
            }

            var evento = request.Evento?.Trim().ToUpperInvariant();
            if (!EventoFluxo.Valido(evento))
            {
                throw new RequestValidationException("Dados invalidos.", new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["evento"] = new() { "Evento do Embedded Signup invalido." }
                });
            }

            t.EventoFluxo = evento;
            if (evento is EventoFluxo.Cancel or EventoFluxo.Error)
            {
                // Cancelar ou erro do SDK nao e falha do Zippy: registra onde parou e deixa a loja tentar de novo.
                var cancelou = evento == EventoFluxo.Cancel;
                t.Etapa = ConexaoMetaRegras.Curto(request.EtapaCancelada, 60);
                await EncerrarAsync(t, cancelou ? EstadoTentativa.Cancelada : EstadoTentativa.Erro,
                    cancelou ? ConexaoMetaCodigos.FluxoCancelado : ConexaoMetaCodigos.CodigoInvalido,
                    cancelou ? "A autorizacao foi cancelada na Meta." : ConexaoMetaRegras.Curto(request.ErroMensagem) ?? "A Meta informou um erro no fluxo.");
                await _canais.RegistrarAuditoriaAsync(t.IdCanal, cancelou ? "meta_cancelado" : "meta_erro_fluxo", null, estabelecimentoId, ator.UsuarioId,
                    JsonSerializer.Serialize(new { tentativa = t.Id, etapa = t.Etapa, codigo = ConexaoMetaRegras.Curto(request.ErroCodigo, 40),
                        sessao = ConexaoMetaRegras.Curto(request.SessaoMeta, 80) }));
                _logger.LogInformation("[wa.meta] ev=fluxo_{Fim} tentativa={Tentativa} loja={Loja} etapa={Etapa}",
                    cancelou ? "cancelado" : "erro", t.Id, estabelecimentoId, t.Etapa);
                return await MontarEstadoAsync(t, null);
            }

            if (!await _repo.ReservarParaTrocaAsync(t.Id)) throw ConexaoMetaErros.Excecao(ConexaoMetaErros.TentativaEmUso(), 409);
            t.Estado = EstadoTentativa.Trocando;

            try
            {
                return await TrocarEAutorizarAsync(ator, cfg, t, request);
            }
            catch (WhatsappException ex)
            {
                await EncerrarAsync(t, EstadoTentativa.Erro, ex.Erro.Codigo, ex.Erro.Mensagem);
                await _canais.RegistrarAuditoriaAsync(t.IdCanal, "meta_autorizacao_falhou", null, estabelecimentoId, ator.UsuarioId,
                    JsonSerializer.Serialize(new { tentativa = t.Id, codigo = ex.Erro.Codigo }));
                throw;
            }
            catch (Exception ex) when (ex is not RequestValidationException)
            {
                _logger.LogError(ex, "[wa.meta] ev=concluir_erro tentativa={Tentativa} loja={Loja}", t.Id, estabelecimentoId);
                await EncerrarAsync(t, EstadoTentativa.Erro, WhatsappCodigos.MetaErro, "Falha inesperada ao concluir a conexao.");
                throw;
            }
        }

        private async Task<EstadoConexaoMetaView> TrocarEAutorizarAsync(
            AtendimentoAtor ator, ConfigEmbeddedSignup cfg, TentativaConexao t, ConcluirConexaoMetaRequest request)
        {
            var code = request.Code?.Trim();
            if (string.IsNullOrEmpty(code) || code.Length > 2048) throw ConexaoMetaErros.Excecao(ConexaoMetaErros.CodigoInvalido(), 422);

            var coexistencia = t.Coexistencia || t.EventoFluxo == EventoFluxo.FinishCoexistencia;
            if (coexistencia && !cfg.CoexistenciaHabilitada) throw ConexaoMetaErros.Excecao(ConexaoMetaErros.CoexistenciaIndisponivel(), 422);

            // 1) codigo -> token (30 s de validade: e a primeira coisa a fazer).
            var troca = await _meta.TrocarCodigoAsync(cfg.AppId!, cfg.AppSecret!, code);
            if (!troca.Ok)
            {
                throw troca.Falha!.SemResposta
                    ? ConexaoMetaErros.Excecao(WhatsappErros.MetaIndisponivel(), 503)
                    : ConexaoMetaErros.Excecao(ConexaoMetaErros.CodigoInvalido(), 422);
            }

            var token = troca.Valor!.AccessToken;

            // 2) o token e do ZippyGo, esta valido e tem as permissoes e a WABA.
            var info = await _meta.DepurarTokenAsync(cfg.AppId!, cfg.AppSecret!, token);
            if (!info.Ok) throw ExcecaoDaFalha(info.Falha!, EtapaConexao.Autorizacao);
            if (!info.Valor!.Valido) throw ConexaoMetaErros.Excecao(ConexaoMetaErros.CodigoInvalido(), 422);
            if (info.Valor.AppId != cfg.AppId)
            {
                _logger.LogWarning("[wa.meta] ev=token_de_outro_app tentativa={Tentativa} loja={Loja}", t.Id, t.IdEstabelecimento);
                throw ConexaoMetaErros.Excecao(ConexaoMetaErros.AppDiferente(), 422);
            }

            var faltando = ConexaoMetaRegras.PermissoesFaltando(info.Valor.Permissoes);
            if (faltando.Count > 0) throw ConexaoMetaErros.Excecao(ConexaoMetaErros.AutorizacaoIncompleta(string.Join(", ", faltando)), 422);

            var (waba, confirmar, erroWaba) = ConexaoMetaRegras.EscolherWaba(info.Valor.WabasGerenciaveis, request.WabaId);
            if (erroWaba != null) throw ConexaoMetaErros.Excecao(erroWaba, 422);

            // 3) a WABA responde com este token (e de quem e o portfolio, direto da Meta).
            var wabaInfo = await _meta.ObterWabaAsync(waba!, token);
            if (!wabaInfo.Ok && (confirmar || ConexaoMetaRegras.TokenPerdido(wabaInfo.Falha) || wabaInfo.Falha!.SemResposta))
            {
                throw confirmar && wabaInfo.Falha is { SemResposta: false, Codigo: not 190 }
                    ? ConexaoMetaErros.Excecao(ConexaoMetaErros.WabaNaoAutorizada(), 422)
                    : ExcecaoDaFalha(wabaInfo.Falha!, EtapaConexao.Waba);
            }

            var businessMeta = wabaInfo.Valor?.BusinessId;
            var businessNavegador = request.BusinessId?.Trim() is { Length: > 0 and <= 32 } b && b.All(char.IsAsciiDigit) ? b : null;

            var autorizacaoId = await _repo.GravarAutorizacaoAsync(new AutorizacaoMeta
            {
                Id = Guid.NewGuid(), IdEstabelecimento = t.IdEstabelecimento, AppId = cfg.AppId!, WabaId = waba!,
                BusinessId = businessMeta ?? businessNavegador, BusinessIdConfirmado = businessMeta != null,
                TokenCifrado = _token.Proteger(token), TokenExpiraEm = info.Valor.ExpiraEm,
                Permissoes = info.Valor.Permissoes.ToArray(), TipoFluxo = t.EventoFluxo, CriadoPor = ator.UsuarioId
            });

            t.IdAutorizacao = autorizacaoId;
            t.WabaId = waba;
            t.BusinessId = businessMeta ?? businessNavegador;
            t.Coexistencia = coexistencia;
            t.Estado = EstadoTentativa.Autorizada;
            t.Etapa = EtapaConexao.Numero;
            t.ErroCodigo = null;
            t.ErroMensagem = null;
            t.ExpiraEm = _agora().AddHours(cfg.HorasParaAtivar);

            await _canais.RegistrarAuditoriaAsync(t.IdCanal, "meta_autorizado", null, t.IdEstabelecimento, ator.UsuarioId,
                JsonSerializer.Serialize(new { tentativa = t.Id, autorizacao = autorizacaoId, waba, evento = t.EventoFluxo, coexistencia,
                    businessConfirmado = businessMeta != null }));
            _logger.LogInformation(
                "[wa.meta] ev=autorizado tentativa={Tentativa} loja={Loja} autorizacao={Autorizacao} waba={Waba} evento={Evento} coexistencia={Coexistencia}",
                t.Id, t.IdEstabelecimento, autorizacaoId, waba, t.EventoFluxo, coexistencia);

            // 4) numeros da WABA, classificados para esta loja.
            var numeros = await ClassificarNumerosAsync(t, token);
            await _repo.AtualizarTentativaAsync(t);
            if (numeros == null) return await MontarEstadoAsync(t, null);

            CanalWhatsapp? reconectado = t.IdCanal.HasValue ? await _canais.ObterAsync(t.IdCanal.Value) : null;
            var navegador = request.PhoneNumberId?.Trim();
            var alvo = ConexaoMetaRegras.EscolherNumeroAutomatico(numeros, navegador, reconectado?.PhoneNumberId);
            if (alvo == null) return await MontarEstadoAsync(t, null);

            if (!await _repo.ReservarParaAtivacaoAsync(t.Id)) return await MontarEstadoAsync(t, null);
            try
            {
                return await AtivarReservadaAsync(ator, cfg, t, alvo, null);
            }
            catch (WhatsappException)
            {
                // A autorizacao ja esta gravada: devolve o andamento (com o erro da etapa) em vez de falhar o "concluir" inteiro.
                return await MontarEstadoAsync(t, null);
            }
        }

        /// <returns>Os numeros, ou nulo se a Meta nao respondeu (a tentativa guarda o erro; a escolha fica para depois).</returns>
        private async Task<IReadOnlyList<NumeroAutorizadoView>?> ClassificarNumerosAsync(TentativaConexao t, string token)
        {
            var lista = await _meta.ListarNumerosAsync(t.WabaId!, token);
            if (!lista.Ok)
            {
                var etapa = ConexaoMetaRegras.EtapaDaFalha(EtapaConexao.Numero, lista.Falha!);
                t.ErroCodigo = etapa.CodigoErro;
                t.ErroMensagem = etapa.Mensagem;
                return null;
            }

            var vistos = new List<NumeroAutorizadoView>();
            foreach (var n in lista.Valor!)
            {
                vistos.Add(ConexaoMetaRegras.Classificar(n, await _canais.ObterPorPhoneNumberIdAsync(n.Id), t.IdEstabelecimento));
            }

            t.NumerosJson = JsonSerializer.Serialize(vistos);
            if (vistos.Count == 0)
            {
                t.ErroCodigo = ConexaoMetaCodigos.SemNumero;
                t.ErroMensagem = ConexaoMetaErros.SemNumero().Mensagem;
            }

            return vistos;
        }

        // ---- ativar (escolha do numero / repetir etapa) -----------------------------------------------------------------

        public async Task<EstadoConexaoMetaView> AtivarAsync(AtendimentoAtor ator, Guid estabelecimentoId, AtivarNumeroMetaRequest request)
        {
            ExigirDonoOuGestao(ator, "meta_ativar", estabelecimentoId);
            var cfg = ExigirDisponivel(null);
            var t = await TentativaDaLojaAsync(estabelecimentoId, request.TentativaId);
            if (t.UsuarioId != ator.UsuarioId && !ator.Gestao) throw ConexaoMetaErros.Excecao(ConexaoMetaErros.TentativaInvalida(), 403);

            var phoneNumberId = request.PhoneNumberId?.Trim() ?? string.Empty;
            if (!AtendimentoRegras.PhoneNumberIdValido(phoneNumberId)) throw new WhatsappException(WhatsappErros.IdVazio());
            if (request.Pin != null && !ConexaoMetaRegras.PinValido(request.Pin.Trim()))
            {
                throw new WhatsappException(new WhatsappErro(WhatsappCodigos.CampoInvalido, "O PIN tem 6 digitos.", "Informe os 6 digitos do PIN.", "pin"));
            }

            if (t.ExpiraEm <= _agora() && t.Estado is EstadoTentativa.Autorizada or EstadoTentativa.Erro)
            {
                await EncerrarAsync(t, EstadoTentativa.Expirada, ConexaoMetaCodigos.TentativaExpirada, "Prazo para escolher o numero acabou.");
                throw ConexaoMetaErros.Excecao(ConexaoMetaErros.TentativaExpirada(), 410);
            }

            if (!await _repo.ReservarParaAtivacaoAsync(t.Id)) throw ConexaoMetaErros.Excecao(ConexaoMetaErros.TentativaEmUso(), 409);
            return await AtivarReservadaAsync(ator, cfg, t, phoneNumberId, request.Pin?.Trim());
        }

        /// <summary>
        /// Com a tentativa ja reservada (ativando): confere de novo o numero na WABA autorizada, grava o canal e roda as etapas. Falha
        /// em qualquer ponto volta a tentativa para "autorizada" com o erro, para repetir so o que faltou.
        /// </summary>
        private async Task<EstadoConexaoMetaView> AtivarReservadaAsync(
            AtendimentoAtor ator, ConfigEmbeddedSignup cfg, TentativaConexao t, string phoneNumberId, string? pin)
        {
            t.Estado = EstadoTentativa.Ativando;
            t.PhoneNumberId = phoneNumberId;
            try
            {
                var autorizacao = t.IdAutorizacao.HasValue ? await _repo.ObterAutorizacaoAsync(t.IdAutorizacao.Value) : null;
                var token = autorizacao?.Estado == EstadoAutorizacao.Ativa ? _token.Revelar(autorizacao.TokenCifrado) : null;
                if (autorizacao == null || string.IsNullOrWhiteSpace(token)) throw ConexaoMetaErros.Excecao(ConexaoMetaErros.TokenRevogado(), 422);

                // Nunca confia na lista guardada nem no navegador: o numero tem de estar na WABA agora, com este token.
                var numero = await _meta.ObterNumeroAsync(phoneNumberId, token);
                var daWaba = await _meta.ListarNumerosAsync(autorizacao.WabaId, token);
                if (!daWaba.Ok) throw ExcecaoDaFalha(daWaba.Falha!, EtapaConexao.Numero);
                if (!numero.Ok) throw ExcecaoDaFalha(numero.Falha!, EtapaConexao.Numero);
                if (daWaba.Valor!.All(n => n.Id != phoneNumberId)) throw ConexaoMetaErros.Excecao(ConexaoMetaErros.NumeroNaoAutorizado(), 422);

                var canal = await GravarCanalAsync(ator, cfg, t, autorizacao, numero.Valor!);
                var (etapas, atualizado) = await ExecutarEtapasAsync(cfg, canal, autorizacao, token, permitirMudancas: true, pin);

                var pendenteDaAtivacao = etapas.Any(e => e.Codigo is EtapaConexao.Registro or EtapaConexao.Inscricao
                                                         && e.Estado is EstadoEtapa.Erro or EstadoEtapa.Pendente);
                t.IdCanal = atualizado.Id;
                t.Etapa = etapas.FirstOrDefault(e => e.Estado != EstadoEtapa.Ok && e.Estado != EstadoEtapa.NaoAplicavel)?.Codigo ?? EtapaConexao.Recebimento;
                var problema = etapas.FirstOrDefault(e => e.Estado is EstadoEtapa.Erro or EstadoEtapa.Pendente && e.Codigo != EtapaConexao.Recebimento);
                t.ErroCodigo = problema?.CodigoErro;
                t.ErroMensagem = problema?.Mensagem;
                t.Estado = pendenteDaAtivacao ? EstadoTentativa.Autorizada : EstadoTentativa.Concluida;
                t.ConcluidaEm = pendenteDaAtivacao ? null : _agora();
                await _repo.AtualizarTentativaAsync(t);

                await _canais.RegistrarAuditoriaAsync(atualizado.Id, t.Modo == "reconectar" ? "meta_reconectado" : "meta_conectado", null, t.IdEstabelecimento,
                    ator.UsuarioId, JsonSerializer.Serialize(new
                    {
                        tentativa = t.Id, autorizacao = autorizacao.Id, waba = autorizacao.WabaId, atualizado.Coexistencia,
                        etapas = etapas.ToDictionary(e => e.Codigo, e => e.Estado)
                    }));
                _logger.LogInformation(
                    "[wa.meta] ev=ativacao tentativa={Tentativa} canal={Canal} loja={Loja} status={Status} registro={Registro} etapa={Etapa}",
                    t.Id, atualizado.Id, t.IdEstabelecimento, atualizado.Status, atualizado.RegistroEstado, t.Etapa);

                return await MontarEstadoAsync(t, (atualizado, etapas));
            }
            catch (WhatsappException ex)
            {
                t.Estado = EstadoTentativa.Autorizada;
                t.ErroCodigo = ex.Erro.Codigo;
                t.ErroMensagem = ex.Erro.Mensagem;
                await _repo.AtualizarTentativaAsync(t);
                throw;
            }
            catch (RequestValidationException)
            {
                t.Estado = EstadoTentativa.Autorizada;
                await _repo.AtualizarTentativaAsync(t);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[wa.meta] ev=ativacao_erro tentativa={Tentativa} loja={Loja}", t.Id, t.IdEstabelecimento);
                t.Estado = EstadoTentativa.Autorizada;
                t.ErroCodigo = WhatsappCodigos.MetaErro;
                t.ErroMensagem = "Falha inesperada ao configurar o numero. Tente de novo.";
                await _repo.AtualizarTentativaAsync(t);
                throw;
            }
        }

        /// <summary>Isolamento: numero de outra loja nunca e apropriado; conexao valida de outra origem so muda por "Reconectar".</summary>
        private async Task<CanalWhatsapp> GravarCanalAsync(
            AtendimentoAtor ator, ConfigEmbeddedSignup cfg, TentativaConexao t, AutorizacaoMeta autorizacao, NumeroMeta numero)
        {
            var numeroE164 = AtendimentoRegras.NormalizarNumero(numero.Telefone)
                ?? throw new WhatsappException(new WhatsappErro(WhatsappCodigos.CampoInvalido,
                    "A Meta nao devolveu um telefone valido para este numero.", "Confira o numero no WhatsApp Manager.", "phoneNumberId"));

            var porId = await _canais.ObterPorPhoneNumberIdAsync(numero.Id);
            var porTelefone = await _canais.ObterPorNumeroAsync(numeroE164.TrimStart('+'));
            if ((porId != null && porId.IdEstabelecimento != t.IdEstabelecimento) || (porTelefone != null && porTelefone.Id != porId?.Id))
            {
                _logger.LogWarning("[wa.meta] ev=ativacao_recusada motivo=numero_de_outra_loja tentativa={Tentativa} loja={Loja}", t.Id, t.IdEstabelecimento);
                throw new WhatsappException(WhatsappErros.NumeroOutraLoja());
            }

            CanalWhatsapp? canal;
            if (t.Modo == "reconectar")
            {
                canal = t.IdCanal.HasValue ? await _canais.ObterAsync(t.IdCanal.Value) : null;
                if (canal == null || canal.IdEstabelecimento != t.IdEstabelecimento) throw new KeyNotFoundException("Numero de WhatsApp nao encontrado.");
                if (canal.PhoneNumberId != numero.Id)
                {
                    throw new WhatsappException(new WhatsappErro(ConexaoMetaCodigos.NumeroNaoAutorizado,
                        "Reconectar mantem o mesmo numero, mas a Meta autorizou outro.",
                        "Na tela da Meta, escolha o mesmo numero. Para trocar de numero, desconecte este e conecte o novo.", "phoneNumberId"));
                }
            }
            else
            {
                canal = porId;
                if (canal != null && canal.Origem != OrigemCanal.Autorizado && canal.Status != StatusCanal.Inativo)
                {
                    throw ConexaoMetaErros.Excecao(ConexaoMetaErros.ConexaoExistente(canal.Origem), 409);
                }

                if (canal == null)
                {
                    var outros = (await _canais.ListarPorLojaAsync(t.IdEstabelecimento)).Where(c => !c.Central && c.Status != StatusCanal.Inativo).ToList();
                    if (outros.Count > 0) throw ConexaoMetaErros.Excecao(ConexaoMetaErros.LojaJaTemNumero(), 409);
                }
            }

            var coexistencia = t.Coexistencia || numero.NoAppBusiness == true;
            var novo = canal == null;
            canal ??= new CanalWhatsapp
            {
                Id = Guid.NewGuid(), IdEstabelecimento = t.IdEstabelecimento, ModoAtendimento = ModoAtendimento.Hibrido,
                RegistroEstado = RegistroEstado.Pendente, WebhookEstado = WebhookEstado.Pendente
            };

            var mudouAutorizacao = canal.IdAutorizacaoMeta != autorizacao.Id;
            canal.PhoneNumberId = numero.Id;
            canal.NumeroE164 = numeroE164;
            canal.Nome = Limpar(numero.Nome) ?? canal.Nome;
            canal.Origem = OrigemCanal.Autorizado;
            canal.WabaId = autorizacao.WabaId;
            canal.AppId = cfg.AppId;
            canal.IdAutorizacaoMeta = autorizacao.Id;
            canal.TokenAutorizacaoCifrado = autorizacao.TokenCifrado;
            canal.AutorizacaoEstado = autorizacao.Estado;
            canal.Coexistencia = coexistencia;
            canal.EstadoMeta = numero.Estado;
            canal.Status = StatusCanal.Configurando;
            canal.UltimoErro = null;
            canal.Central = false;
            if (canal.WebhookEstado is WebhookEstado.NaoAplicavel or WebhookEstado.Erro) canal.WebhookEstado = WebhookEstado.Pendente;
            if (canal.RegistroEstado == RegistroEstado.NaoAplicavel) canal.RegistroEstado = RegistroEstado.Pendente;
            if (mudouAutorizacao) canal.InscricaoWabaEm = null;

            try
            {
                if (novo)
                {
                    var ativos = (await _catalogo.ListarServicosDaLojaAsync(t.IdEstabelecimento)).Where(s => s.Ativo).Select(s => s.Codigo).ToList();
                    canal.Servicos = ativos;
                    await _canais.CriarAsync(canal, ativos);
                }
                else
                {
                    await _canais.GravarAutorizadoAsync(canal);
                }
            }
            catch (CanalConflitoException)
            {
                // Corrida: outra loja gravou o mesmo numero entre a conferencia e a escrita. A unicidade do banco decide.
                throw new WhatsappException(WhatsappErros.NumeroOutraLoja());
            }

            await _catalogo.GarantirModulosAsync(t.IdEstabelecimento, new[] { ModuloWhatsapp });
            return (await _canais.ObterAsync(canal.Id))!;
        }

        // ---- etapas (ativacao e revalidacao) ----------------------------------------------------------------------------

        /// <summary>
        /// Confere (e, com <paramref name="permitirMudancas"/>, faz) cada etapa: inscricao do app na WABA, registro do numero, sincronizacao
        /// da coexistencia, webhook global do app e recebimento. So operacoes seguras ou explicitamente permitidas mudam algo na Meta.
        /// </summary>
        private async Task<(List<EtapaConexaoView> Etapas, CanalWhatsapp Canal)> ExecutarEtapasAsync(
            ConfigEmbeddedSignup cfg, CanalWhatsapp canal, AutorizacaoMeta autorizacao, string token, bool permitirMudancas, string? pin)
        {
            var etapas = new List<EtapaConexaoView> { ConexaoMetaRegras.Ok(EtapaConexao.Autorizacao, "Acesso autorizado pela Meta.") };
            var agora = _agora();

            // Inscricao do app ZippyGo na WABA (sem override de URL: usa o webhook global ja configurado no app).
            var inscritos = await _meta.AppsInscritosAsync(autorizacao.WabaId, token);
            if (!inscritos.Ok)
            {
                etapas.Add(ConexaoMetaRegras.EtapaDaFalha(EtapaConexao.Inscricao, inscritos.Falha!));
            }
            else if (inscritos.Valor!.Contains(cfg.AppId!))
            {
                canal.InscricaoWabaEm ??= agora;
                etapas.Add(ConexaoMetaRegras.Ok(EtapaConexao.Inscricao, "O Zippy recebe os eventos desta conta."));
            }
            else if (permitirMudancas)
            {
                var inscricao = await _meta.InscreverAppAsync(autorizacao.WabaId, token);
                if (inscricao.Ok)
                {
                    canal.InscricaoWabaEm = agora;
                    etapas.Add(ConexaoMetaRegras.Ok(EtapaConexao.Inscricao, "O Zippy passou a receber os eventos desta conta."));
                }
                else
                {
                    etapas.Add(ConexaoMetaRegras.EtapaDaFalha(EtapaConexao.Inscricao, inscricao.Falha!));
                }
            }
            else
            {
                canal.InscricaoWabaEm = null;
                etapas.Add(new EtapaConexaoView(EtapaConexao.Inscricao, EstadoEtapa.Pendente, "O Zippy nao esta inscrito para receber eventos desta conta.",
                    "Use \"Corrigir\" para inscrever de novo.", ResponsavelPendencia.Zippy, "reparar"));
            }

            // Numero e registro.
            var numero = await _meta.ObterNumeroAsync(canal.PhoneNumberId, token);
            if (!numero.Ok)
            {
                etapas.Add(ConexaoMetaRegras.EtapaDaFalha(EtapaConexao.Numero, numero.Falha!));
            }
            else
            {
                canal.EstadoMeta = numero.Valor!.Estado;
                if (numero.Valor.NoAppBusiness == true) canal.Coexistencia = true;
                etapas.Add(ConexaoMetaRegras.Ok(EtapaConexao.Numero, $"Numero {numero.Valor.Telefone ?? canal.NumeroE164} encontrado na conta autorizada."));
                etapas.Add(await EtapaRegistroAsync(canal, numero.Valor, token, permitirMudancas, pin, agora));
            }

            // Coexistencia: contatos e historico so podem ser sincronizados uma vez, nas 24 h depois da conexao.
            if (canal.Coexistencia)
            {
                etapas.Add(await EtapaCoexistenciaAsync(canal, token, permitirMudancas, agora));
            }

            etapas.Add(await EtapaWebhookGlobalAsync(cfg, canal.Coexistencia));
            etapas.Add(canal.UltimoRecebimentoEm.HasValue
                ? ConexaoMetaRegras.Ok(EtapaConexao.Recebimento, "Mensagens estao chegando ao Zippy.")
                : new EtapaConexaoView(EtapaConexao.Recebimento, EstadoEtapa.Pendente, "Ainda nao chegou nenhuma mensagem por este numero.",
                    "Mande um \"oi\" de outro telefone para este numero e atualize.", ResponsavelPendencia.Loja, "enviar_teste"));

            if (etapas.Any(e => e.CodigoErro == ConexaoMetaCodigos.TokenRevogado))
            {
                await _repo.MarcarAutorizacaoAsync(autorizacao.Id, EstadoAutorizacao.Revogada, "A Meta recusou o token (190).");
                canal.AutorizacaoEstado = EstadoAutorizacao.Revogada;
            }

            var operacional = ConexaoMetaRegras.Operacional(etapas);
            canal.Status = operacional ? StatusCanal.Ativo
                : etapas.Any(e => e.Estado == EstadoEtapa.Erro) ? StatusCanal.Erro
                : StatusCanal.Configurando;
            var problema = etapas.FirstOrDefault(e => e.Estado is EstadoEtapa.Erro or EstadoEtapa.Atencao);
            canal.UltimoErro = operacional ? null : problema?.Mensagem;
            canal.WebhookEstado = canal.UltimoRecebimentoEm.HasValue ? WebhookEstado.Recebendo
                : canal.InscricaoWabaEm.HasValue ? WebhookEstado.Registrado : WebhookEstado.Pendente;
            canal.WebhookErro = null;
            if (canal.WebhookEstado == WebhookEstado.Registrado) canal.WebhookRegistradoEm ??= agora;

            await _canais.GravarAutorizadoAsync(canal);
            await _canais.MarcarVerificadoAsync(canal.Id, !etapas.Any(e => e.Estado == EstadoEtapa.Erro), canal.UltimoErro);
            return (etapas, (await _canais.ObterAsync(canal.Id))!);
        }

        private async Task<EtapaConexaoView> EtapaRegistroAsync(
            CanalWhatsapp canal, NumeroMeta numero, string token, bool permitirMudancas, string? pin, DateTime agora)
        {
            switch (ConexaoMetaRegras.DecidirRegistro(numero, canal.Coexistencia))
            {
                case DecisaoRegistro.Dispensado:
                    canal.RegistroEstado = RegistroEstado.Dispensado;
                    return new EtapaConexaoView(EtapaConexao.Registro, EstadoEtapa.NaoAplicavel,
                        "O numero continua no app WhatsApp Business (coexistencia): nao e registrado de novo.");
                case DecisaoRegistro.JaRegistrado:
                    canal.RegistroEstado = RegistroEstado.Registrado;
                    return ConexaoMetaRegras.Ok(EtapaConexao.Registro, "Numero registrado na plataforma do WhatsApp.");
                case DecisaoRegistro.RegistradoComRestricao:
                    canal.RegistroEstado = RegistroEstado.Registrado;
                    return new EtapaConexaoView(EtapaConexao.Registro, EstadoEtapa.Atencao, $"A Meta limitou este numero ({numero.Estado}).",
                        "Confira a qualidade e os avisos do numero no WhatsApp Manager.", ResponsavelPendencia.Loja, "abrir_whatsapp_manager",
                        ConexaoMetaErros.LinkWhatsappManager);
                case DecisaoRegistro.Bloqueado:
                    canal.RegistroEstado = RegistroEstado.Erro;
                    return new EtapaConexaoView(EtapaConexao.Registro, EstadoEtapa.Erro, "A Meta bloqueou este numero.",
                        "Veja o motivo no WhatsApp Manager e solicite a revisao por la.", ResponsavelPendencia.Loja, "abrir_whatsapp_manager",
                        ConexaoMetaErros.LinkWhatsappManager);
                case DecisaoRegistro.NaoVerificado:
                    canal.RegistroEstado = RegistroEstado.Pendente;
                    return ConexaoMetaRegras.DoErro(EtapaConexao.Registro, EstadoEtapa.Pendente, ConexaoMetaErros.NumeroNaoVerificado(),
                        ResponsavelPendencia.Loja, "tentar_de_novo");
            }

            if (!permitirMudancas)
            {
                canal.RegistroEstado = RegistroEstado.Pendente;
                return new EtapaConexaoView(EtapaConexao.Registro, EstadoEtapa.Pendente, "O numero ainda nao esta registrado na plataforma.",
                    "Use \"Corrigir\" para registrar.", ResponsavelPendencia.Zippy, "reparar");
            }

            if (!ConexaoMetaRegras.PodeTentarRegistro(canal.RegistroTentativas, canal.RegistroTentadoEm, agora))
            {
                return ConexaoMetaRegras.DoErro(EtapaConexao.Registro, EstadoEtapa.Atencao, ConexaoMetaErros.RegistroLimite(), ResponsavelPendencia.Meta, "aguardar");
            }

            // PIN de duas etapas: o informado pela loja, o que o Zippy ja usou neste numero ou um novo (passa a ser o PIN do numero).
            var usar = pin ?? _token.Revelar(canal.RegistroPinCifrado) ?? ConexaoMetaRegras.GerarPin();
            canal.RegistroTentativas = ConexaoMetaRegras.ContarTentativa(canal.RegistroTentativas, canal.RegistroTentadoEm, agora);
            canal.RegistroTentadoEm = agora;
            var registro = await _meta.RegistrarNumeroAsync(canal.PhoneNumberId, token, usar);
            if (registro.Ok)
            {
                canal.RegistroEstado = RegistroEstado.Registrado;
                canal.RegistroPinCifrado = _token.Proteger(usar);
                canal.EstadoMeta = "CONNECTED";
                _logger.LogInformation("[wa.meta] ev=numero_registrado canal={Canal} loja={Loja}", canal.Id, canal.IdEstabelecimento);
                return ConexaoMetaRegras.Ok(EtapaConexao.Registro, "Numero registrado na plataforma do WhatsApp.");
            }

            var etapa = ConexaoMetaRegras.EtapaDaFalha(EtapaConexao.Registro, registro.Falha!);
            canal.RegistroEstado = etapa.Estado == EstadoEtapa.Erro ? RegistroEstado.Erro : RegistroEstado.Pendente;
            // Sem resposta: nao se sabe se a Meta registrou; guarda o PIN tentado para a proxima tentativa usar o mesmo.
            if (registro.Falha!.SemResposta) canal.RegistroPinCifrado = _token.Proteger(usar);
            return etapa;
        }

        private async Task<EtapaConexaoView> EtapaCoexistenciaAsync(CanalWhatsapp canal, string token, bool permitirMudancas, DateTime agora)
        {
            if (canal.CoexistenciaSincronizadaEm.HasValue)
            {
                return ConexaoMetaRegras.Ok(EtapaConexao.Coexistencia, "Contatos e historico do app WhatsApp Business solicitados a Meta.");
            }

            if (!permitirMudancas)
            {
                return new EtapaConexaoView(EtapaConexao.Coexistencia, EstadoEtapa.Pendente, "A sincronizacao com o app WhatsApp Business ainda nao foi pedida.",
                    "Use \"Corrigir\" nas primeiras 24 horas depois da conexao.", ResponsavelPendencia.Zippy, "reparar");
            }

            var contatos = await _meta.SincronizarAppBusinessAsync(canal.PhoneNumberId, token, "smb_app_state_sync");
            var historico = contatos.Ok ? await _meta.SincronizarAppBusinessAsync(canal.PhoneNumberId, token, "history") : contatos;
            if (contatos.Ok && historico.Ok)
            {
                canal.CoexistenciaSincronizadaEm = agora;
                return ConexaoMetaRegras.Ok(EtapaConexao.Coexistencia,
                    "Sincronizacao pedida. Deixe o app WhatsApp Business aberto no celular por alguns minutos.");
            }

            var falha = ConexaoMetaRegras.EtapaDaFalha(EtapaConexao.Coexistencia, (contatos.Falha ?? historico.Falha)!);
            return falha with
            {
                Estado = EstadoEtapa.Atencao,
                ComoResolver = "A Meta so aceita a sincronizacao uma vez e nas 24 horas depois da conexao. Tente \"Corrigir\" de novo; " +
                               "as mensagens novas funcionam mesmo sem o historico."
            };
        }

        /// <summary>So le a configuracao do webhook do app ZippyGo (nunca a altera ao conectar uma loja).</summary>
        private async Task<EtapaConexaoView> EtapaWebhookGlobalAsync(ConfigEmbeddedSignup cfg, bool coexistencia)
        {
            var assinaturas = await _meta.AssinaturasDoAppAsync(cfg.AppId!, cfg.AppSecret!);
            if (!assinaturas.Ok)
            {
                return new EtapaConexaoView(EtapaConexao.Webhook, EstadoEtapa.Atencao, "Nao foi possivel conferir o webhook do app ZippyGo agora.",
                    "Isto e verificado pela Zippy; a conexao pode estar funcionando. Tente de novo em instantes.", ResponsavelPendencia.Zippy, "tentar_de_novo");
            }

            var conta = assinaturas.Valor!.FirstOrDefault(a => a.Objeto == "whatsapp_business_account" && a.Ativa);
            var exigidos = coexistencia
                ? new[] { "messages", "account_update", "smb_message_echoes", "history", "smb_app_state_sync" }
                : new[] { "messages", "account_update" };
            var faltando = conta == null ? exigidos : exigidos.Except(conta.Campos, StringComparer.Ordinal).ToArray();
            if (faltando.Length == 0) return ConexaoMetaRegras.Ok(EtapaConexao.Webhook, "Webhook do app ZippyGo ativo.");

            var essencial = conta == null || faltando.Contains("messages");
            return new EtapaConexaoView(EtapaConexao.Webhook, essencial ? EstadoEtapa.Erro : EstadoEtapa.Atencao,
                $"O webhook do app ZippyGo nao assina: {string.Join(", ", faltando)}.",
                "A Zippy precisa assinar estes campos em App Dashboard > WhatsApp > Configuracao. Nao e algo que o restaurante resolva.",
                ResponsavelPendencia.Zippy, "contatar_suporte");
        }

        // ---- estado ----------------------------------------------------------------------------------------------------

        public async Task<EstadoConexaoMetaView> EstadoAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid? tentativaId)
        {
            ExigirDonoOuGestao(ator, "meta_estado", estabelecimentoId);
            var t = tentativaId.HasValue
                ? await TentativaDaLojaAsync(estabelecimentoId, tentativaId.Value)
                : await _repo.UltimaTentativaAsync(estabelecimentoId);
            if (t == null)
            {
                return new EstadoConexaoMetaView(null, "nenhuma", EstadoConexao.AguardandoAutorizacao, null, null, false, null, null,
                    Array.Empty<string>(), Array.Empty<NumeroAutorizadoView>(), Array.Empty<EtapaConexaoView>(), new[] { "conectar" }, null, null, null);
            }

            if (t.Estado is EstadoTentativa.Iniciada or EstadoTentativa.Autorizada && t.ExpiraEm <= _agora())
            {
                await EncerrarAsync(t, EstadoTentativa.Expirada, ConexaoMetaCodigos.TentativaExpirada, "Prazo acabou.");
            }

            return await MontarEstadoAsync(t, null);
        }

        private async Task<EstadoConexaoMetaView> MontarEstadoAsync(TentativaConexao t, (CanalWhatsapp Canal, List<EtapaConexaoView> Etapas)? ativacao)
        {
            var numeros = LerNumeros(t.NumerosJson);
            var autorizacao = t.IdAutorizacao.HasValue ? await _repo.ObterAutorizacaoAsync(t.IdAutorizacao.Value) : null;
            var canal = ativacao?.Canal ?? (t.IdCanal.HasValue ? await _canais.ObterAsync(t.IdCanal.Value) : null);
            if (canal != null && canal.IdEstabelecimento != t.IdEstabelecimento) canal = null;

            var etapas = ativacao?.Etapas ?? EtapasDaTentativa(t, autorizacao, numeros, canal);
            var revogado = autorizacao != null && autorizacao.Estado != EstadoAutorizacao.Ativa;
            var estado = t.Estado switch
            {
                EstadoTentativa.Iniciada or EstadoTentativa.Trocando => EstadoConexao.AguardandoAutorizacao,
                EstadoTentativa.Cancelada or EstadoTentativa.Expirada or EstadoTentativa.Substituida => EstadoConexao.AguardandoAutorizacao,
                _ => ConexaoMetaRegras.EstadoGeral(etapas, revogado)
            };

            var acoes = ConexaoMetaRegras.Acoes(etapas).ToList();
            if (t.Estado is EstadoTentativa.Cancelada or EstadoTentativa.Expirada or EstadoTentativa.Substituida
                || (t.Estado == EstadoTentativa.Erro && t.IdAutorizacao == null)) acoes.Add("conectar");
            if (t.Estado == EstadoTentativa.Autorizada && canal == null && numeros.Any(n => n.Selecionavel)) acoes.Add("escolher_numero");

            return new EstadoConexaoMetaView(t.Id, t.Estado, estado, t.ExpiraEm, t.EventoFluxo, t.Coexistencia, t.WabaId, t.BusinessId,
                autorizacao?.Permissoes ?? Array.Empty<string>(), numeros, etapas, acoes.Distinct().ToList(),
                canal == null ? null : CanaisWhatsappService.Montar(canal, _webhook.Visao(canal, comSegredos: false)), t.ErroCodigo, t.ErroMensagem);
        }

        /// <summary>Etapas conhecidas sem chamar a Meta (o diagnostico real e o revalidar).</summary>
        private static List<EtapaConexaoView> EtapasDaTentativa(
            TentativaConexao t, AutorizacaoMeta? autorizacao, IReadOnlyList<NumeroAutorizadoView> numeros, CanalWhatsapp? canal)
        {
            var etapas = new List<EtapaConexaoView>();
            if (t.Estado is EstadoTentativa.Iniciada or EstadoTentativa.Trocando)
            {
                etapas.Add(new EtapaConexaoView(EtapaConexao.Autorizacao, EstadoEtapa.Pendente, "Aguardando a autorizacao na Meta.", null,
                    ResponsavelPendencia.Loja, null));
                return etapas;
            }

            if (autorizacao == null)
            {
                if (t.Estado is EstadoTentativa.Erro)
                {
                    etapas.Add(new EtapaConexaoView(EtapaConexao.Autorizacao, EstadoEtapa.Erro, t.ErroMensagem ?? "A autorizacao nao foi concluida.",
                        "Clique em \"Conectar com a Meta\" de novo.", ResponsavelPendencia.Loja, "conectar", null, t.ErroCodigo));
                }

                return etapas;
            }

            etapas.Add(autorizacao.Estado == EstadoAutorizacao.Ativa
                ? ConexaoMetaRegras.Ok(EtapaConexao.Autorizacao, "Acesso autorizado pela Meta.")
                : ConexaoMetaRegras.DoErro(EtapaConexao.Autorizacao, EstadoEtapa.Erro, ConexaoMetaErros.TokenRevogado(), ResponsavelPendencia.Loja, "reconectar"));

            if (canal == null)
            {
                etapas.Add(numeros.Count == 0
                    ? ConexaoMetaRegras.DoErro(EtapaConexao.Numero, EstadoEtapa.Pendente,
                        t.ErroCodigo == ConexaoMetaCodigos.SemNumero || t.ErroCodigo == null ? ConexaoMetaErros.SemNumero()
                            : new WhatsappErro(t.ErroCodigo, t.ErroMensagem ?? "Nao foi possivel listar os numeros.", "Tente de novo em instantes.", null),
                        ResponsavelPendencia.Loja, "tentar_de_novo")
                    : new EtapaConexaoView(EtapaConexao.Numero, EstadoEtapa.Pendente,
                        t.ErroMensagem ?? "Escolha qual numero desta conta este restaurante vai usar.", null, ResponsavelPendencia.Loja,
                        "escolher_numero", null, t.ErroCodigo));
                return etapas;
            }

            etapas.Add(ConexaoMetaRegras.Ok(EtapaConexao.Numero, $"Numero {canal.NumeroE164} ligado a esta loja."));
            etapas.Add(canal.RegistroEstado switch
            {
                RegistroEstado.Registrado => ConexaoMetaRegras.Ok(EtapaConexao.Registro, "Numero registrado na plataforma do WhatsApp."),
                RegistroEstado.Dispensado => new EtapaConexaoView(EtapaConexao.Registro, EstadoEtapa.NaoAplicavel, "Numero segue no app WhatsApp Business."),
                _ => new EtapaConexaoView(EtapaConexao.Registro, t.ErroCodigo == null ? EstadoEtapa.Pendente : EstadoEtapa.Erro,
                    t.ErroMensagem ?? "Registro do numero pendente.", null, ResponsavelPendencia.Loja,
                    t.ErroCodigo == ConexaoMetaCodigos.PinNecessario ? "informar_pin" : "tentar_de_novo", null, t.ErroCodigo)
            });
            etapas.Add(canal.InscricaoWabaEm.HasValue
                ? ConexaoMetaRegras.Ok(EtapaConexao.Inscricao, "O Zippy recebe os eventos desta conta.")
                : new EtapaConexaoView(EtapaConexao.Inscricao, EstadoEtapa.Pendente, "Inscricao do Zippy na conta pendente.", null, ResponsavelPendencia.Zippy, "tentar_de_novo"));
            etapas.Add(canal.UltimoRecebimentoEm.HasValue
                ? ConexaoMetaRegras.Ok(EtapaConexao.Recebimento, "Mensagens estao chegando ao Zippy.")
                : new EtapaConexaoView(EtapaConexao.Recebimento, EstadoEtapa.Pendente, "Ainda nao chegou nenhuma mensagem por este numero.",
                    "Mande um \"oi\" de outro telefone para este numero e atualize.", ResponsavelPendencia.Loja, "enviar_teste"));
            return etapas;
        }

        // ---- revalidar -------------------------------------------------------------------------------------------------

        public async Task<DiagnosticoConexaoView> RevalidarAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid canalId, RevalidarCanalRequest request)
        {
            var canal = await CanalDaLojaAsync(estabelecimentoId, canalId);
            if (canal.Central) ExigirGestao(ator, "revalidar_central");
            else ExigirDonoOuGestao(ator, "revalidar", estabelecimentoId);
            var pin = request.Pin?.Trim();
            if (pin != null && !ConexaoMetaRegras.PinValido(pin))
            {
                throw new WhatsappException(new WhatsappErro(WhatsappCodigos.CampoInvalido, "O PIN tem 6 digitos.", "Informe os 6 digitos do PIN.", "pin"));
            }

            List<EtapaConexaoView> etapas;
            if (canal.Origem == OrigemCanal.Autorizado) (etapas, canal) = await RevalidarAutorizadoAsync(canal, request.Reparar, pin);
            else etapas = await RevalidarManualAsync(ator, canal);

            var desconectado = canal.Status == StatusCanal.Inativo;
            var revogado = canal.Origem == OrigemCanal.Autorizado && canal.AutorizacaoEstado != EstadoAutorizacao.Ativa;
            var estado = ConexaoMetaRegras.EstadoGeral(etapas, revogado, desconectado);
            await _canais.RegistrarAuditoriaAsync(canal.Id, request.Reparar ? "revalidado_com_reparo" : "revalidado", canal.IdEstabelecimento,
                canal.IdEstabelecimento, ator.UsuarioId, JsonSerializer.Serialize(new { estado, etapas = etapas.ToDictionary(e => e.Codigo, e => e.Estado) }));
            _logger.LogInformation("[wa.meta] ev=revalidado canal={Canal} loja={Loja} origem={Origem} estado={Estado} reparar={Reparar}",
                canal.Id, canal.IdEstabelecimento, canal.Origem, estado, request.Reparar);

            var acoes = ConexaoMetaRegras.Acoes(etapas).ToList();
            if (desconectado) acoes.Insert(0, canal.Origem == OrigemCanal.Autorizado ? "reconectar" : "conectar_manual");
            return new DiagnosticoConexaoView(canal.Id, canal.Origem, estado, _agora(), canal.AppId, canal.WabaId, canal.PhoneNumberId, etapas,
                acoes.Distinct().ToList(), CanaisWhatsappService.Montar(canal, _webhook.Visao(canal, comSegredos: false)));
        }

        private async Task<(List<EtapaConexaoView>, CanalWhatsapp)> RevalidarAutorizadoAsync(CanalWhatsapp canal, bool reparar, string? pin)
        {
            var cfg = Config;
            var autorizacao = canal.IdAutorizacaoMeta.HasValue ? await _repo.ObterAutorizacaoAsync(canal.IdAutorizacaoMeta.Value) : null;
            var revogada = new List<EtapaConexaoView>
            {
                ConexaoMetaRegras.DoErro(EtapaConexao.Autorizacao, EstadoEtapa.Erro, ConexaoMetaErros.TokenRevogado(), ResponsavelPendencia.Loja, "reconectar")
            };
            if (autorizacao == null || autorizacao.Estado != EstadoAutorizacao.Ativa) return (revogada, canal);
            if (!cfg.Configurado) return (new List<EtapaConexaoView> { ConexaoMetaRegras.DoErro(EtapaConexao.Autorizacao, EstadoEtapa.Atencao,
                ConexaoMetaErros.Indisponivel(), ResponsavelPendencia.Zippy, "contatar_suporte") }, canal);

            var token = _token.Revelar(autorizacao.TokenCifrado);
            if (string.IsNullOrWhiteSpace(token))
            {
                // Cifrado com outra chave (Atendimento__ChaveToken trocada): a loja precisa autorizar de novo.
                await _repo.MarcarAutorizacaoAsync(autorizacao.Id, EstadoAutorizacao.Erro, "Token nao pode ser aberto com a chave atual.");
                return (revogada, canal);
            }

            var info = await _meta.DepurarTokenAsync(cfg.AppId!, cfg.AppSecret!, token);
            if (!info.Ok)
            {
                return (new List<EtapaConexaoView> { ConexaoMetaRegras.EtapaDaFalha(EtapaConexao.Autorizacao, info.Falha!) }, canal);
            }

            if (!info.Valor!.Valido || info.Valor.AppId != cfg.AppId)
            {
                await _repo.MarcarAutorizacaoAsync(autorizacao.Id,
                    info.Valor.ExpiraEm is { } e && e <= _agora() ? EstadoAutorizacao.Expirada : EstadoAutorizacao.Revogada, "Token invalido no debug_token.");
                await _canais.MarcarErroAsync(canal.Id, ConexaoMetaErros.TokenRevogado().Mensagem, desativar: canal.Status != StatusCanal.Inativo);
                canal = (await _canais.ObterAsync(canal.Id))!;
                return (revogada, canal);
            }

            var faltando = ConexaoMetaRegras.PermissoesFaltando(info.Valor.Permissoes);
            var semWaba = info.Valor.WabasGerenciaveis.Count > 0 && !info.Valor.WabasGerenciaveis.Contains(autorizacao.WabaId);
            await _repo.MarcarAutorizacaoValidadaAsync(autorizacao.Id, info.Valor.ExpiraEm, info.Valor.Permissoes);
            if (faltando.Count > 0 || semWaba)
            {
                var erro = ConexaoMetaErros.AutorizacaoIncompleta(semWaba ? "conta do WhatsApp Business" : string.Join(", ", faltando));
                await _canais.MarcarErroAsync(canal.Id, erro.Mensagem, desativar: canal.Status != StatusCanal.Inativo);
                return (new List<EtapaConexaoView>
                {
                    ConexaoMetaRegras.DoErro(EtapaConexao.Permissoes, EstadoEtapa.Erro, erro, ResponsavelPendencia.Loja, "reconectar")
                }, (await _canais.ObterAsync(canal.Id))!);
            }

            if (canal.Status == StatusCanal.Inativo)
            {
                return (new List<EtapaConexaoView> { ConexaoMetaRegras.Ok(EtapaConexao.Autorizacao, "A autorizacao continua valida na Meta.") }, canal);
            }

            var (etapas, atualizado) = await ExecutarEtapasAsync(cfg, canal, autorizacao, token, reparar, pin);
            etapas.Insert(1, ConexaoMetaRegras.Ok(EtapaConexao.Permissoes, "Permissoes de mensagens e de gerenciamento concedidas."));
            return (etapas, atualizado);
        }

        /// <summary>Numeros manuais (proprio) e da Zippy: o mesmo diagnostico, com as verificacoes que cabem a cada origem.</summary>
        private async Task<List<EtapaConexaoView>> RevalidarManualAsync(AtendimentoAtor ator, CanalWhatsapp canal)
        {
            var etapas = new List<EtapaConexaoView>();
            if (canal.Status == StatusCanal.Inativo)
            {
                etapas.Add(new EtapaConexaoView(EtapaConexao.Numero, EstadoEtapa.Pendente, "Este numero esta desconectado do Zippy.", null,
                    ResponsavelPendencia.Loja, "conectar_manual"));
                return etapas;
            }

            var verificacao = await _canaisService.VerificarAsync(ator, canal.Id);
            if (verificacao.Ok)
            {
                etapas.Add(ConexaoMetaRegras.Ok(EtapaConexao.Numero, verificacao.Mensagem));
            }
            else
            {
                var erro = WhatsappErros.DaVerificacao(verificacao);
                var indisponivel = erro.Codigo == WhatsappCodigos.MetaIndisponivel;
                etapas.Add(ConexaoMetaRegras.DoErro(EtapaConexao.Numero, indisponivel ? EstadoEtapa.Atencao : EstadoEtapa.Erro, erro,
                    indisponivel ? ResponsavelPendencia.Meta : erro.Codigo == WhatsappCodigos.TokenGlobalAusente ? ResponsavelPendencia.Zippy : ResponsavelPendencia.Loja,
                    "tentar_de_novo"));
            }

            var atual = (await _canais.ObterAsync(canal.Id))!;
            if (atual.Origem == OrigemCanal.Proprio)
            {
                var w = _webhook.Visao(atual, comSegredos: false);
                etapas.Add(w.Estado switch
                {
                    WebhookEstado.Recebendo => ConexaoMetaRegras.Ok(EtapaConexao.Webhook, "Webhook do app do cliente entregando mensagens."),
                    WebhookEstado.Registrado when w.Codigo == null => new EtapaConexaoView(EtapaConexao.Webhook, EstadoEtapa.Pendente,
                        "Webhook registrado; aguardando a primeira mensagem.", null, ResponsavelPendencia.Loja, "enviar_teste"),
                    _ => new EtapaConexaoView(EtapaConexao.Webhook, w.Estado == WebhookEstado.Erro ? EstadoEtapa.Erro : EstadoEtapa.Atencao,
                        w.Erro ?? "Webhook ainda nao registrado no app do cliente.", w.ComoResolver, ResponsavelPendencia.Loja, "registrar_webhook", null, w.Codigo)
                });
            }
            else
            {
                etapas.Add(new EtapaConexaoView(EtapaConexao.Webhook, EstadoEtapa.NaoAplicavel, "Usa o webhook global da Zippy."));
            }

            etapas.Add(atual.UltimoRecebimentoEm.HasValue || atual.UltimoEnvioOkEm.HasValue
                ? ConexaoMetaRegras.Ok(EtapaConexao.Recebimento, "O numero ja trocou mensagens pelo Zippy.")
                : new EtapaConexaoView(EtapaConexao.Recebimento, EstadoEtapa.Pendente, "Ainda nao chegou nenhuma mensagem por este numero.",
                    "Mande um \"oi\" de outro telefone para este numero e atualize.", ResponsavelPendencia.Loja, "enviar_teste"));
            return etapas;
        }

        // ---- desconectar -----------------------------------------------------------------------------------------------

        public async Task<DesconexaoView> DesconectarAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid canalId)
        {
            var canal = await CanalDaLojaAsync(estabelecimentoId, canalId);
            if (canal.Origem != OrigemCanal.Autorizado)
            {
                await _canaisService.DesconectarAsync(ator, canalId);
                return new DesconexaoView(canal.Id, canal.Origem, true, false, null,
                    "Numero desconectado do Zippy. Conversas e configuracoes ficam guardadas; nada foi alterado na Meta.");
            }

            ExigirDonoOuGestao(ator, "desconectar", estabelecimentoId);
            await _canais.DesativarAsync(canal.Id);

            var (removida, motivo) = await TalvezRemoverInscricaoAsync(canal);
            await _canais.RegistrarAuditoriaAsync(canal.Id, "meta_desconectado", canal.IdEstabelecimento, null, ator.UsuarioId,
                JsonSerializer.Serialize(new { canal.WabaId, autorizacao = canal.IdAutorizacaoMeta, inscricaoRemovida = removida, motivo }));
            _logger.LogInformation("[wa.meta] ev=desconectado canal={Canal} loja={Loja} usuario={Usuario} inscricao_removida={Removida} motivo={Motivo}",
                canal.Id, canal.IdEstabelecimento, ator.UsuarioId, removida, motivo);

            return new DesconexaoView(canal.Id, canal.Origem, true, removida, motivo,
                "Numero desconectado do Zippy. O numero e a conta continuam do restaurante na Meta; conversas e configuracoes ficam guardadas. " +
                "Para tirar o acesso do app ZippyGo de vez, remova-o em Configuracoes do negocio > Integracoes na Meta.");
        }

        /// <summary>
        /// Tira a inscricao do app da WABA so quando ninguem mais depende dela: WABA que nao e da Zippy, nenhum outro numero (de qualquer
        /// loja) usando a WABA e nenhuma outra loja com autorizacao ativa nela. Na duvida, mantem (e diz por que).
        /// </summary>
        private async Task<(bool Removida, string? Motivo)> TalvezRemoverInscricaoAsync(CanalWhatsapp canal)
        {
            var cfg = Config;
            var autorizacao = canal.IdAutorizacaoMeta.HasValue ? await _repo.ObterAutorizacaoAsync(canal.IdAutorizacaoMeta.Value) : null;
            if (autorizacao == null || string.IsNullOrWhiteSpace(canal.WabaId)) return (false, "sem_autorizacao");

            var outrosDaAutorizacao = await _repo.ContarCanaisDaAutorizacaoAsync(autorizacao.Id, canal.Id);
            if (outrosDaAutorizacao == 0 && autorizacao.Estado == EstadoAutorizacao.Ativa)
            {
                // A loja nao usa mais esta autorizacao: o Zippy para de usar o token (a Meta continua com o que o cliente decidir).
                await _repo.MarcarAutorizacaoAsync(autorizacao.Id, EstadoAutorizacao.Revogada, "Desconectado pela loja.");
            }

            if (cfg.WabasDaZippy.Contains(canal.WabaId)) return (false, "waba_da_zippy");
            if (!autorizacao.BusinessIdConfirmado) return (false, "portfolio_nao_confirmado");
            if (cfg.BusinessIdIntegrador != null && autorizacao.BusinessId == cfg.BusinessIdIntegrador) return (false, "waba_da_zippy");
            if (await _canais.ContarCanaisDaWabaAsync(canal.WabaId, canal.Id) > 0) return (false, "waba_em_uso_por_outro_numero");
            if (await _repo.ContarOutrasAutorizacoesAtivasAsync(canal.WabaId, autorizacao.Id) > 0) return (false, "waba_autorizada_por_outra_loja");

            var token = _token.Revelar(autorizacao.TokenCifrado);
            if (string.IsNullOrWhiteSpace(token)) return (false, "sem_token");

            var remover = await _meta.DesinscreverAppAsync(canal.WabaId, token);
            return remover.Ok ? (true, null) : (false, remover.Falha!.SemResposta ? "meta_indisponivel" : "meta_recusou");
        }

        // ---- internos --------------------------------------------------------------------------------------------------

        private ConfigEmbeddedSignup ExigirDisponivel(string? origem)
        {
            var cfg = Config;
            if (!cfg.Configurado) throw ConexaoMetaErros.Excecao(ConexaoMetaErros.Indisponivel(), 503);
            if (!_token.Configurado) throw new WhatsappException(WhatsappErros.ChaveCifragemAusente());
            if (origem != null && !ConexaoMetaRegras.OrigemPermitida(cfg.OrigensPermitidas, origem))
            {
                _logger.LogWarning("[wa.meta] ev=origem_recusada");
                throw ConexaoMetaErros.Excecao(ConexaoMetaErros.OrigemInvalida(), 403);
            }

            return cfg;
        }

        private async Task<TentativaConexao> TentativaDaLojaAsync(Guid estabelecimentoId, Guid tentativaId)
        {
            var t = await _repo.ObterTentativaAsync(tentativaId);
            // Tentativa de outra loja responde como inexistente (nao confirma que existe).
            if (t == null || t.IdEstabelecimento != estabelecimentoId) throw ConexaoMetaErros.Excecao(ConexaoMetaErros.TentativaInvalida(), 404);
            return t;
        }

        private async Task<CanalWhatsapp> CanalDaLojaAsync(Guid estabelecimentoId, Guid canalId)
        {
            var canal = await _canais.ObterAsync(canalId);
            if (canal == null || canal.IdEstabelecimento != estabelecimentoId) throw new KeyNotFoundException("Numero de WhatsApp nao encontrado.");
            return canal;
        }

        private async Task EncerrarAsync(TentativaConexao t, string estado, string? codigo, string? mensagem)
        {
            t.Estado = estado;
            t.ErroCodigo = codigo;
            t.ErroMensagem = mensagem;
            if (estado != EstadoTentativa.Erro) t.ConcluidaEm = _agora();
            await _repo.AtualizarTentativaAsync(t);
        }

        private static WhatsappException ExcecaoDaFalha(MetaFalha falha, string etapa)
        {
            var e = ConexaoMetaRegras.EtapaDaFalha(etapa, falha);
            var http = falha.SemResposta ? 503 : e.CodigoErro == WhatsappCodigos.MetaErro ? 502 : 422;
            return new WhatsappException(new WhatsappErro(e.CodigoErro ?? WhatsappCodigos.MetaErro, e.Mensagem, e.ComoResolver ?? string.Empty, null, e.Link), http);
        }

        private static IReadOnlyList<NumeroAutorizadoView> LerNumeros(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<List<NumeroAutorizadoView>>(json) ?? new List<NumeroAutorizadoView>();
            }
            catch (JsonException)
            {
                return Array.Empty<NumeroAutorizadoView>();
            }
        }

        private static bool PodeConfigurar(AtendimentoAtor ator, Guid estabelecimentoId) =>
            ator.Gestao || (ator.EstabelecimentoId == estabelecimentoId && ator.TemPermissao("WhatsApp", "configurar"));

        private void ExigirDonoOuGestao(AtendimentoAtor ator, string acao, Guid estabelecimentoId)
        {
            if (PodeConfigurar(ator, estabelecimentoId)) return;

            _logger.LogWarning("[wa.meta] ev=negado acao={Acao} loja={Loja} usuario={Usuario}", acao, estabelecimentoId, ator.UsuarioId);
            throw new UnauthorizedAccessException("Voce nao tem permissao para conectar o WhatsApp desta loja.");
        }

        private void ExigirGestao(AtendimentoAtor ator, string acao)
        {
            if (ator.Gestao) return;

            _logger.LogWarning("[wa.meta] ev=negado acao={Acao} motivo=so_gestao usuario={Usuario}", acao, ator.UsuarioId);
            throw new UnauthorizedAccessException("Somente a Gestao mexe no numero central de testes.");
        }

        private static string? Limpar(string? texto)
        {
            var limpo = texto?.Trim();
            if (string.IsNullOrEmpty(limpo)) return null;
            return limpo.Length > 120 ? limpo[..120] : limpo;
        }
    }
}
