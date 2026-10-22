using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Service;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento
{
    /// <param name="Token">Token da Meta deste numero. Vazio = nao mexe no token atual.</param>
    /// <param name="Servicos">Servicos que o numero atende. Nulo = todos os que a loja tem ligados.</param>
    /// <param name="Origem">Nulo = cadastro com numero e token (como sempre). "zippy" = numero da conta Meta da Zippy: so o Phone Number ID, token global.</param>
    public sealed record CanalRequest(
        string? PhoneNumberId, string? Numero, string? Nome, string? Token, string? ModoAtendimento, IReadOnlyList<string>? Servicos, string? Origem = null);

    /// <summary>O dono cola o ID da Meta e o token; o telefone vem da propria Meta.</summary>
    public sealed record ConectarCanalRequest(string? PhoneNumberId, string? Token);

    /// <summary>O cliente usa o Meta dele: 5 dados colados no painel; o sistema registra o webhook no app dele.</summary>
    public sealed record ConectarProprioRequest(string? PhoneNumberId, string? WabaId, string? Token, string? AppId, string? AppSecret);

    public sealed record CanalView(
        Guid Id, Guid EstabelecimentoId, string PhoneNumberId, string NumeroE164, string? Nome, string Status, string ModoAtendimento,
        IReadOnlyList<string> Servicos, bool TemToken, DateTime? VerificadoEm, DateTime? UltimoRecebimentoEm,
        DateTime? UltimoEnvioOkEm, string? UltimoErro, DateTime? UltimoErroEm, bool Central = false,
        string Origem = OrigemCanal.Zippy, string? WabaId = null, string? AppId = null, CanalWebhookView? Webhook = null);

    public interface ICanaisWhatsappService
    {
        Task<IReadOnlyList<CanalView>> ListarAsync(AtendimentoAtor ator, Guid estabelecimentoId);
        Task<CanalView> CriarAsync(AtendimentoAtor ator, Guid estabelecimentoId, CanalRequest request);
        Task<CanalView> AtualizarAsync(AtendimentoAtor ator, Guid canalId, CanalRequest request);
        /// <summary>O dono do restaurante escolhe quem atende (bot, humano ou hibrido) no proprio numero.</summary>
        Task<CanalView> DefinirModoAsync(AtendimentoAtor ator, Guid canalId, string? modo);
        Task<CanalView> DefinirServicosAsync(AtendimentoAtor ator, Guid canalId, IEnumerable<string>? servicos);
        /// <summary>Passa o numero para outra loja (um numero pertence a uma loja so). Zera os servicos do numero.</summary>
        Task<CanalView> MoverAsync(AtendimentoAtor ator, Guid canalId, Guid destinoEstabelecimentoId);
        Task RemoverAsync(AtendimentoAtor ator, Guid canalId);
        /// <summary>
        /// A propria loja conecta o numero dela: confere o ID e o token na Meta antes de gravar, pega o telefone de la e deixa o canal
        /// ativo. Recusa um ID que ja e de outra loja. So a Gestao ou quem configura o WhatsApp da loja.
        /// </summary>
        Task<CanalView> ConectarAsync(AtendimentoAtor ator, Guid estabelecimentoId, ConectarCanalRequest request);
        /// <summary>A propria loja desconecta o numero dela (o central de testes continua so da Gestao).</summary>
        Task DesconectarAsync(AtendimentoAtor ator, Guid canalId);
        /// <summary>Confere na Meta se o ID existe e e do telefone cadastrado. A Gestao ou quem configura o WhatsApp da propria loja.</summary>
        Task<VerificacaoCanal> VerificarAsync(AtendimentoAtor ator, Guid canalId);
        /// <summary>
        /// Numero proprio: o cliente cola 5 dados (Phone Number ID, WABA, token, App ID, App Secret) e se conecta sem a Zippy. Confere o numero
        /// na Meta, guarda os segredos cifrados e tenta registrar o webhook no app dele. Registro que falha NAO derruba a conexao: o canal
        /// fica com webhook "pendente" e os dados para registro manual. So vira "ativo" quando chega uma mensagem de verdade.
        /// </summary>
        Task<CanalView> ConectarProprioAsync(AtendimentoAtor ator, Guid estabelecimentoId, ConectarProprioRequest request);
        /// <summary>Tenta de novo o registro do webhook do numero proprio (App + WABA na Meta).</summary>
        Task<CanalView> RegistrarWebhookAsync(AtendimentoAtor ator, Guid canalId);
        /// <summary>Estado do webhook com URL e verify token para exibir (o app secret e o token nunca saem).</summary>
        Task<CanalWebhookView> ObterWebhookAsync(AtendimentoAtor ator, Guid canalId);
        /// <summary>Antes de criar uma loja com numero da Zippy: confere o Phone Number ID na Meta e se esta livre. Nao grava nada.</summary>
        Task PreverNumeroZippyAsync(AtendimentoAtor ator, string? phoneNumberId);
    }

    public sealed class CanaisWhatsappService : ICanaisWhatsappService
    {
        private const string ModuloWhatsapp = "WHATSAPP";

        private readonly ICanalRepository _canais;
        private readonly ICatalogoRepository _catalogo;
        private readonly ITokenProtector _token;
        private readonly ICanalVerificador _verificador;
        private readonly ICanalWebhookService _webhook;
        private readonly ILogger<CanaisWhatsappService> _logger;

        public CanaisWhatsappService(
            ICanalRepository canais, ICatalogoRepository catalogo, ITokenProtector token, ICanalVerificador verificador,
            ICanalWebhookService webhook, ILogger<CanaisWhatsappService> logger)
        {
            _canais = canais;
            _catalogo = catalogo;
            _token = token;
            _verificador = verificador;
            _webhook = webhook;
            _logger = logger;
        }

        public async Task<IReadOnlyList<CanalView>> ListarAsync(AtendimentoAtor ator, Guid estabelecimentoId)
        {
            ServicosDaLojaService.ExigirLeitura(ator, estabelecimentoId);
            // URL e verify token do webhook so para quem configura o WhatsApp da loja; quem so le enxerga o estado.
            var detalhado = ator.Gestao || (ator.EstabelecimentoId == estabelecimentoId && ator.TemPermissao("WhatsApp", "configurar"));
            return (await _canais.ListarPorLojaAsync(estabelecimentoId)).Select(c => Ver(c, detalhado)).ToList();
        }

        public async Task<CanalView> CriarAsync(AtendimentoAtor ator, Guid estabelecimentoId, CanalRequest request)
        {
            ExigirGestao(ator, "criar_canal", estabelecimentoId);
            if (!await _catalogo.LojaExisteAsync(estabelecimentoId)) throw new KeyNotFoundException("Estabelecimento nao encontrado.");

            if (request.Origem != null)
            {
                if (request.Origem != OrigemCanal.Zippy)
                {
                    throw new WhatsappException(new WhatsappErro(WhatsappCodigos.CampoInvalido,
                        "Origem invalida para este cadastro.",
                        "Aqui so se cria numero da Zippy (origem \"zippy\"). O numero do cliente se conecta em canais/proprio e o de testes no numero central.",
                        "origem"));
                }

                return await CriarZippyAsync(ator, estabelecimentoId, request);
            }

            var erros = AtendimentoRegras.ValidarCanal(request.PhoneNumberId, request.Numero, request.ModoAtendimento);
            var tokenCifrado = CifrarToken(request.Token);
            if (erros.Count > 0) throw new RequestValidationException("Dados invalidos.", erros);

            var ativos = (await _catalogo.ListarServicosDaLojaAsync(estabelecimentoId)).Where(s => s.Ativo).Select(s => s.Codigo).ToList();
            var servicos = request.Servicos == null
                ? ativos
                : AtendimentoRegras.ValidarServicosDoCanal(request.Servicos, ativos).ToList();

            var canal = new CanalWhatsapp
            {
                Id = Guid.NewGuid(),
                IdEstabelecimento = estabelecimentoId,
                PhoneNumberId = request.PhoneNumberId!.Trim(),
                NumeroE164 = AtendimentoRegras.NormalizarNumero(request.Numero)!,
                Nome = LimparTexto(request.Nome),
                TokenCifrado = tokenCifrado,
                Status = StatusCanal.Configurando,
                ModoAtendimento = request.ModoAtendimento ?? ModoAtendimento.Hibrido,
                Servicos = servicos
            };

            await ExecutarAsync("criar_canal", canal.Id, () => _canais.CriarAsync(canal, servicos));
            await _catalogo.GarantirModulosAsync(estabelecimentoId, new[] { ModuloWhatsapp });
            await _canais.RegistrarAuditoriaAsync(canal.Id, "criado", null, estabelecimentoId, ator.UsuarioId,
                JsonSerializer.Serialize(new { numero = canal.NumeroE164, servicos }));

            _logger.LogInformation(
                "[canal] ev=criado canal={Canal} loja={Loja} usuario={Usuario} numero={Numero} servicos={Servicos} modo={Modo} token={Token}",
                canal.Id, estabelecimentoId, ator.UsuarioId, canal.NumeroE164, string.Join(",", servicos), canal.ModoAtendimento, canal.TemToken);

            return Ver((await _canais.ObterAsync(canal.Id))!);
        }

        public async Task<CanalView> AtualizarAsync(AtendimentoAtor ator, Guid canalId, CanalRequest request)
        {
            var canal = await ObterOuFalharAsync(canalId);
            ExigirGestao(ator, "atualizar_canal", canal.IdEstabelecimento);

            var erros = AtendimentoRegras.ValidarCanal(request.PhoneNumberId, request.Numero, request.ModoAtendimento);
            var novoToken = CifrarToken(request.Token);
            if (erros.Count > 0) throw new RequestValidationException("Dados invalidos.", erros);

            var phoneNumberId = request.PhoneNumberId!.Trim();
            var numero = AtendimentoRegras.NormalizarNumero(request.Numero)!;
            var identidadeMudou = phoneNumberId != canal.PhoneNumberId || numero != canal.NumeroE164;

            if (canal.Origem == OrigemCanal.Proprio && (identidadeMudou || novoToken != null))
            {
                // O webhook, o app e a WABA do numero proprio dependem destes dados: trocar so uma parte deixaria tudo desencontrado.
                throw new WhatsappException(new WhatsappErro(WhatsappCodigos.CampoInvalido,
                    "Este e um numero proprio do cliente: ID e token nao se alteram por aqui.",
                    "Peca ao cliente para conectar de novo (Numeros de WhatsApp > Conectar numero proprio) ou desconecte o numero e conecte outro.",
                    "phoneNumberId"));
            }

            canal.PhoneNumberId = phoneNumberId;
            canal.NumeroE164 = numero;
            canal.Nome = LimparTexto(request.Nome);
            if (novoToken != null) canal.TokenCifrado = novoToken;
            if (identidadeMudou || novoToken != null)
            {
                // Dado novo precisa ser verificado de novo na Meta antes de valer como "ativo".
                canal.Status = StatusCanal.Configurando;
                canal.UltimoErro = null;
            }

            await ExecutarAsync("atualizar_canal", canal.Id, () => _canais.AtualizarDadosAsync(canal));
            if (request.ModoAtendimento != null) await _canais.AtualizarModoAsync(canal.Id, request.ModoAtendimento);
            if (request.Servicos != null)
            {
                var ativos = (await _catalogo.ListarServicosDaLojaAsync(canal.IdEstabelecimento)).Where(s => s.Ativo).Select(s => s.Codigo).ToList();
                await _canais.DefinirServicosAsync(canal.Id, AtendimentoRegras.ValidarServicosDoCanal(request.Servicos, ativos).ToList());
            }

            await _canais.RegistrarAuditoriaAsync(canal.Id, "atualizado", canal.IdEstabelecimento, canal.IdEstabelecimento, ator.UsuarioId,
                JsonSerializer.Serialize(new { identidadeMudou, tokenNovo = novoToken != null }));
            _logger.LogInformation(
                "[canal] ev=atualizado canal={Canal} loja={Loja} usuario={Usuario} identidade_mudou={Mudou} token_novo={TokenNovo}",
                canal.Id, canal.IdEstabelecimento, ator.UsuarioId, identidadeMudou, novoToken != null);

            return Ver((await _canais.ObterAsync(canal.Id))!);
        }

        public async Task<CanalView> DefinirModoAsync(AtendimentoAtor ator, Guid canalId, string? modo)
        {
            var canal = await ObterOuFalharAsync(canalId);
            var donoComPermissao = ator.EstabelecimentoId == canal.IdEstabelecimento && ator.TemPermissao("WhatsApp", "configurar");
            if (!ator.Gestao && !donoComPermissao)
            {
                _logger.LogWarning("[canal] ev=negado acao=definir_modo canal={Canal} usuario={Usuario}", canalId, ator.UsuarioId);
                throw new UnauthorizedAccessException("Voce nao tem permissao para configurar o atendimento deste numero.");
            }

            if (!ModoAtendimento.Valido(modo))
            {
                throw new RequestValidationException("Dados invalidos.", new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["modoAtendimento"] = new List<string> { "Modo invalido. Use bot, humano ou hibrido." }
                });
            }

            await _canais.AtualizarModoAsync(canalId, modo!);
            await _canais.RegistrarAuditoriaAsync(canalId, "modo_alterado", canal.IdEstabelecimento, canal.IdEstabelecimento, ator.UsuarioId,
                JsonSerializer.Serialize(new { de = canal.ModoAtendimento, para = modo }));
            _logger.LogInformation(
                "[canal] ev=modo_alterado canal={Canal} loja={Loja} usuario={Usuario} de={De} para={Para}",
                canalId, canal.IdEstabelecimento, ator.UsuarioId, canal.ModoAtendimento, modo);

            return Ver((await _canais.ObterAsync(canalId))!);
        }

        public async Task<CanalView> DefinirServicosAsync(AtendimentoAtor ator, Guid canalId, IEnumerable<string>? servicos)
        {
            var canal = await ObterOuFalharAsync(canalId);
            ExigirGestao(ator, "definir_servicos_canal", canal.IdEstabelecimento);

            var ativos = (await _catalogo.ListarServicosDaLojaAsync(canal.IdEstabelecimento)).Where(s => s.Ativo).Select(s => s.Codigo).ToList();
            var escolhidos = AtendimentoRegras.ValidarServicosDoCanal(servicos, ativos);
            await _canais.DefinirServicosAsync(canalId, escolhidos.ToList());
            await _canais.RegistrarAuditoriaAsync(canalId, "servicos_alterados", canal.IdEstabelecimento, canal.IdEstabelecimento, ator.UsuarioId,
                JsonSerializer.Serialize(new { de = canal.Servicos, para = escolhidos }));
            _logger.LogInformation(
                "[canal] ev=servicos_alterados canal={Canal} loja={Loja} usuario={Usuario} servicos={Servicos}",
                canalId, canal.IdEstabelecimento, ator.UsuarioId, string.Join(",", escolhidos));

            return Ver((await _canais.ObterAsync(canalId))!);
        }

        public async Task<CanalView> MoverAsync(AtendimentoAtor ator, Guid canalId, Guid destinoEstabelecimentoId)
        {
            var canal = await ObterOuFalharAsync(canalId);
            ExigirGestao(ator, "mover_canal", canal.IdEstabelecimento);
            if (!await _catalogo.LojaExisteAsync(destinoEstabelecimentoId)) throw new KeyNotFoundException("Estabelecimento de destino nao encontrado.");

            if (canal.IdEstabelecimento == destinoEstabelecimentoId)
            {
                throw new RequestValidationException("Dados invalidos.", new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["destino"] = new List<string> { "O numero ja pertence a este estabelecimento." }
                });
            }

            await _canais.MoverAsync(canalId, destinoEstabelecimentoId);
            await _catalogo.GarantirModulosAsync(destinoEstabelecimentoId, new[] { ModuloWhatsapp });
            await _canais.RegistrarAuditoriaAsync(canalId, "movido", canal.IdEstabelecimento, destinoEstabelecimentoId, ator.UsuarioId,
                JsonSerializer.Serialize(new { servicosRemovidos = canal.Servicos }));
            _logger.LogInformation(
                "[canal] ev=movido canal={Canal} de_loja={Origem} para_loja={Destino} usuario={Usuario} servicos_removidos={Servicos}",
                canalId, canal.IdEstabelecimento, destinoEstabelecimentoId, ator.UsuarioId, string.Join(",", canal.Servicos));

            return Ver((await _canais.ObterAsync(canalId))!);
        }

        public async Task RemoverAsync(AtendimentoAtor ator, Guid canalId)
        {
            var canal = await ObterOuFalharAsync(canalId);
            ExigirGestao(ator, "remover_canal", canal.IdEstabelecimento);

            await _canais.RegistrarAuditoriaAsync(canalId, "removido", canal.IdEstabelecimento, null, ator.UsuarioId,
                JsonSerializer.Serialize(new { numero = canal.NumeroE164, phoneNumberId = canal.PhoneNumberId }));
            await _canais.RemoverAsync(canalId);
            _logger.LogInformation("[canal] ev=removido canal={Canal} loja={Loja} usuario={Usuario}", canalId, canal.IdEstabelecimento, ator.UsuarioId);
        }

        public async Task<CanalView> ConectarAsync(AtendimentoAtor ator, Guid estabelecimentoId, ConectarCanalRequest request)
        {
            ExigirDonoOuGestao(ator, "conectar_canal", estabelecimentoId);
            if (!await _catalogo.LojaExisteAsync(estabelecimentoId)) throw new KeyNotFoundException("Estabelecimento nao encontrado.");

            var phoneNumberId = request.PhoneNumberId?.Trim() ?? string.Empty;
            var token = request.Token?.Trim() ?? string.Empty;
            ExigirIdDoNumero(phoneNumberId);
            if (token.Length == 0) throw new WhatsappException(WhatsappErros.TokenInvalido() with { Mensagem = "Informe o token da Meta." });
            var tokenCifrado = CifrarToken(token);

            var existente = await _canais.ObterPorPhoneNumberIdAsync(phoneNumberId);
            if (existente != null && existente.IdEstabelecimento != estabelecimentoId)
            {
                _logger.LogWarning("[canal] ev=conectar_recusado motivo=de_outra_loja loja={Loja} usuario={Usuario}", estabelecimentoId, ator.UsuarioId);
                throw new WhatsappException(WhatsappErros.NumeroOutraLoja());
            }

            if (existente == null)
            {
                var proprios = (await _canais.ListarPorLojaAsync(estabelecimentoId)).Where(c => !c.Central && c.Status != StatusCanal.Inativo).ToList();
                if (proprios.Count > 0) throw Invalido("phoneNumberId", "Esta loja ja tem um numero conectado. Desconecte antes de conectar outro.");
            }

            var meta = await _verificador.ConsultarCredenciaisAsync(phoneNumberId, token);
            if (!meta.Ok) throw new WhatsappException(WhatsappErros.DaVerificacao(meta));

            var numero = AtendimentoRegras.NormalizarNumero(meta.NumeroNaMeta);
            if (numero == null) throw Invalido("phoneNumberId", "A Meta nao devolveu um telefone valido para este numero.");

            var donoDoNumero = await _canais.ObterPorNumeroAsync(numero.TrimStart('+'));
            if (donoDoNumero != null && donoDoNumero.Id != existente?.Id)
            {
                _logger.LogWarning("[canal] ev=conectar_recusado motivo=telefone_ja_cadastrado loja={Loja} usuario={Usuario}", estabelecimentoId, ator.UsuarioId);
                throw new WhatsappException(WhatsappErros.NumeroOutraLoja());
            }

            CanalWhatsapp canal;
            if (existente != null)
            {
                canal = existente;
                canal.NumeroE164 = numero;
                canal.Nome = LimparTexto(meta.NomeVerificado) ?? canal.Nome;
                canal.TokenCifrado = tokenCifrado;
                canal.Status = StatusCanal.Configurando;
                canal.UltimoErro = null;
                await ExecutarAsync("conectar_canal", canal.Id, () => _canais.AtualizarDadosAsync(canal));
            }
            else
            {
                var ativos = (await _catalogo.ListarServicosDaLojaAsync(estabelecimentoId)).Where(sv => sv.Ativo).Select(sv => sv.Codigo).ToList();
                canal = new CanalWhatsapp
                {
                    Id = Guid.NewGuid(),
                    IdEstabelecimento = estabelecimentoId,
                    PhoneNumberId = phoneNumberId,
                    NumeroE164 = numero,
                    Nome = LimparTexto(meta.NomeVerificado),
                    TokenCifrado = tokenCifrado,
                    Status = StatusCanal.Configurando,
                    ModoAtendimento = ModoAtendimento.Hibrido,
                    Servicos = ativos
                };
                await ExecutarAsync("conectar_canal", canal.Id, () => _canais.CriarAsync(canal, ativos));
            }

            await _canais.MarcarVerificadoAsync(canal.Id, true, null);
            await _canais.RegistrarAuditoriaAsync(canal.Id, "conectado", null, estabelecimentoId, ator.UsuarioId,
                JsonSerializer.Serialize(new { numero, pelaLoja = !ator.Gestao }));
            _logger.LogInformation("[canal] ev=conectado canal={Canal} loja={Loja} usuario={Usuario} numero={Numero}", canal.Id, estabelecimentoId, ator.UsuarioId, numero);

            return Ver((await _canais.ObterAsync(canal.Id))!);
        }

        public async Task DesconectarAsync(AtendimentoAtor ator, Guid canalId)
        {
            var canal = await ObterOuFalharAsync(canalId);
            if (canal.Central) ExigirGestao(ator, "desconectar_canal", canal.IdEstabelecimento);
            else ExigirDonoOuGestao(ator, "desconectar_canal", canal.IdEstabelecimento);

            await _canais.RegistrarAuditoriaAsync(canalId, "desconectado", canal.IdEstabelecimento, null, ator.UsuarioId,
                JsonSerializer.Serialize(new { numero = canal.NumeroE164, phoneNumberId = canal.PhoneNumberId }));
            await _canais.RemoverAsync(canalId);
            _logger.LogInformation("[canal] ev=desconectado canal={Canal} loja={Loja} usuario={Usuario}", canalId, canal.IdEstabelecimento, ator.UsuarioId);
        }

        public async Task<VerificacaoCanal> VerificarAsync(AtendimentoAtor ator, Guid canalId)
        {
            var canal = await ObterOuFalharAsync(canalId);
            if (canal.Central) ExigirGestao(ator, "verificar_canal", canal.IdEstabelecimento);
            else ExigirDonoOuGestao(ator, "verificar_canal", canal.IdEstabelecimento);
            return await _verificador.VerificarAsync(canal);
        }

        // ---- numero da Zippy (somente a Gestao) -----------------------------------------------------------------

        public async Task PreverNumeroZippyAsync(AtendimentoAtor ator, string? phoneNumberId)
        {
            ExigirGestao(ator, "prever_numero_zippy", Guid.Empty);
            await ValidarNumeroZippyAsync(null, phoneNumberId);
        }

        /// <summary>
        /// O numero ja nasce na conta Meta da Zippy: a Gestao informa so o Phone Number ID, o token e o global (WhatsApp__AccessToken) e o
        /// telefone vem da Meta. Confere na Meta antes de gravar e deixa o canal ativo (o webhook global ja entrega as mensagens).
        /// </summary>
        private async Task<CanalView> CriarZippyAsync(AtendimentoAtor ator, Guid estabelecimentoId, CanalRequest request)
        {
            if (request.ModoAtendimento != null && !ModoAtendimento.Valido(request.ModoAtendimento))
            {
                throw Invalido("modoAtendimento", "Modo invalido. Use bot, humano ou hibrido.");
            }

            var (phoneNumberId, numero, meta, existente) = await ValidarNumeroZippyAsync(estabelecimentoId, request.PhoneNumberId);

            var ativos = (await _catalogo.ListarServicosDaLojaAsync(estabelecimentoId)).Where(s => s.Ativo).Select(s => s.Codigo).ToList();
            var servicos = request.Servicos == null
                ? ativos
                : AtendimentoRegras.ValidarServicosDoCanal(request.Servicos, ativos).ToList();

            CanalWhatsapp canal;
            if (existente != null)
            {
                // Ja era desta loja (cadastro repetido): confirma e segue, sem duplicar.
                canal = existente;
                canal.NumeroE164 = numero;
                canal.Nome = LimparTexto(request.Nome) ?? LimparTexto(meta.NomeVerificado) ?? canal.Nome;
                canal.Status = StatusCanal.Configurando;
                canal.UltimoErro = null;
                await ExecutarAsync("criar_canal_zippy", canal.Id, () => _canais.AtualizarDadosAsync(canal));
            }
            else
            {
                canal = new CanalWhatsapp
                {
                    Id = Guid.NewGuid(),
                    IdEstabelecimento = estabelecimentoId,
                    PhoneNumberId = phoneNumberId,
                    NumeroE164 = numero,
                    Nome = LimparTexto(request.Nome) ?? LimparTexto(meta.NomeVerificado),
                    TokenCifrado = null,
                    Origem = OrigemCanal.Zippy,
                    Status = StatusCanal.Configurando,
                    ModoAtendimento = request.ModoAtendimento ?? ModoAtendimento.Hibrido,
                    Servicos = servicos
                };
                await ExecutarAsync("criar_canal_zippy", canal.Id, () => _canais.CriarAsync(canal, servicos));
            }

            await _canais.MarcarVerificadoAsync(canal.Id, true, null);
            await _catalogo.GarantirModulosAsync(estabelecimentoId, new[] { ModuloWhatsapp });
            await _canais.RegistrarAuditoriaAsync(canal.Id, "criado_zippy", null, estabelecimentoId, ator.UsuarioId,
                JsonSerializer.Serialize(new { numero, servicos }));
            _logger.LogInformation(
                "[canal] ev=criado origem=zippy canal={Canal} loja={Loja} usuario={Usuario} numero={Numero} servicos={Servicos}",
                canal.Id, estabelecimentoId, ator.UsuarioId, numero, string.Join(",", servicos));

            return Ver((await _canais.ObterAsync(canal.Id))!);
        }

        /// <param name="estabelecimentoId">Loja que vai receber o numero; nulo antes de a loja existir (todo numero ja cadastrado e de outra loja).</param>
        private async Task<(string PhoneNumberId, string Numero, VerificacaoCanal Meta, CanalWhatsapp? Existente)> ValidarNumeroZippyAsync(
            Guid? estabelecimentoId, string? phoneNumberIdInformado)
        {
            var phoneNumberId = phoneNumberIdInformado?.Trim() ?? string.Empty;
            ExigirIdDoNumero(phoneNumberId);

            var existente = await _canais.ObterPorPhoneNumberIdAsync(phoneNumberId);
            if (existente != null && existente.IdEstabelecimento != estabelecimentoId)
            {
                throw new WhatsappException(WhatsappErros.NumeroOutraLoja());
            }

            if (existente != null && existente.Origem != OrigemCanal.Zippy)
            {
                throw Invalido("phoneNumberId", "Este numero ja esta conectado a esta loja de outra forma. Desconecte antes de cadastrar de novo.");
            }

            var meta = await _verificador.ConsultarComTokenGlobalAsync(phoneNumberId);
            if (!meta.Ok) throw new WhatsappException(WhatsappErros.DaVerificacao(meta));

            var numero = AtendimentoRegras.NormalizarNumero(meta.NumeroNaMeta)
                ?? throw Invalido("phoneNumberId", "A Meta nao devolveu um telefone valido para este numero.");
            var donoDoNumero = await _canais.ObterPorNumeroAsync(numero.TrimStart('+'));
            if (donoDoNumero != null && donoDoNumero.Id != existente?.Id)
            {
                throw new WhatsappException(WhatsappErros.NumeroOutraLoja());
            }

            return (phoneNumberId, numero, meta, existente);
        }

        // ---- numero proprio do cliente ----------------------------------------------------------------------------

        public async Task<CanalView> ConectarProprioAsync(AtendimentoAtor ator, Guid estabelecimentoId, ConectarProprioRequest request)
        {
            ExigirDonoOuGestao(ator, "conectar_proprio", estabelecimentoId);
            if (!await _catalogo.LojaExisteAsync(estabelecimentoId)) throw new KeyNotFoundException("Estabelecimento nao encontrado.");
            if (!_token.Configurado) throw new WhatsappException(WhatsappErros.ChaveCifragemAusente());

            var phoneNumberId = request.PhoneNumberId?.Trim() ?? string.Empty;
            var wabaId = request.WabaId?.Trim() ?? string.Empty;
            var token = request.Token?.Trim() ?? string.Empty;
            var appId = request.AppId?.Trim() ?? string.Empty;
            var appSecret = request.AppSecret?.Trim() ?? string.Empty;
            ValidarFormatoProprio(phoneNumberId, wabaId, token, appId, appSecret);

            var existente = await _canais.ObterPorPhoneNumberIdAsync(phoneNumberId);
            if (existente != null && existente.IdEstabelecimento != estabelecimentoId)
            {
                _logger.LogWarning("[canal] ev=conectar_recusado motivo=de_outra_loja loja={Loja} usuario={Usuario}", estabelecimentoId, ator.UsuarioId);
                throw new WhatsappException(WhatsappErros.NumeroOutraLoja());
            }

            if (existente != null && existente.Origem != OrigemCanal.Proprio)
            {
                throw Invalido("phoneNumberId", "Este numero ja esta conectado a esta loja pela Zippy. Peca a Zippy para remove-lo antes de conectar o seu.");
            }

            if (existente == null)
            {
                var conectados = (await _canais.ListarPorLojaAsync(estabelecimentoId)).Where(c => !c.Central && c.Status != StatusCanal.Inativo).ToList();
                if (conectados.Count > 0) throw Invalido("phoneNumberId", "Esta loja ja tem um numero conectado. Desconecte antes de conectar outro.");
            }

            var meta = await _verificador.ConsultarCredenciaisAsync(phoneNumberId, token);
            if (!meta.Ok) throw new WhatsappException(WhatsappErros.DaVerificacao(meta));

            var numero = AtendimentoRegras.NormalizarNumero(meta.NumeroNaMeta)
                ?? throw Invalido("phoneNumberId", "A Meta nao devolveu um telefone valido para este numero.");
            var donoDoNumero = await _canais.ObterPorNumeroAsync(numero.TrimStart('+'));
            if (donoDoNumero != null && donoDoNumero.Id != existente?.Id)
            {
                _logger.LogWarning("[canal] ev=conectar_recusado motivo=telefone_ja_cadastrado loja={Loja} usuario={Usuario}", estabelecimentoId, ator.UsuarioId);
                throw new WhatsappException(WhatsappErros.NumeroOutraLoja());
            }

            var canal = existente ?? new CanalWhatsapp { Id = Guid.NewGuid(), IdEstabelecimento = estabelecimentoId, PhoneNumberId = phoneNumberId };
            canal.NumeroE164 = numero;
            canal.Nome = LimparTexto(meta.NomeVerificado) ?? canal.Nome;
            canal.TokenCifrado = _token.Proteger(token);
            canal.Origem = OrigemCanal.Proprio;
            canal.WabaId = wabaId;
            canal.AppId = appId;
            canal.AppSecretCifrado = _token.Proteger(appSecret);
            if (string.IsNullOrWhiteSpace(canal.WebhookChave) || string.IsNullOrWhiteSpace(canal.VerifyTokenCifrado))
            {
                // Reconectar mantem a URL e o verify token: quem ja colou na Meta na mao nao precisa refazer.
                var (chave, verifyToken) = _webhook.GerarCredenciais();
                canal.WebhookChave = chave;
                canal.VerifyTokenCifrado = _token.Proteger(verifyToken);
            }

            canal.WebhookEstado = WebhookEstado.Pendente;
            canal.WebhookErro = null;
            canal.WebhookRegistradoEm = null;
            canal.Status = StatusCanal.Configurando;
            canal.UltimoErro = null;

            if (existente == null)
            {
                var ativos = (await _catalogo.ListarServicosDaLojaAsync(estabelecimentoId)).Where(sv => sv.Ativo).Select(sv => sv.Codigo).ToList();
                canal.ModoAtendimento = ModoAtendimento.Hibrido;
                canal.Servicos = ativos;
                await ExecutarAsync("conectar_proprio", canal.Id, () => _canais.CriarAsync(canal, ativos));
            }
            else
            {
                await ExecutarAsync("conectar_proprio", canal.Id, () => _canais.AtualizarDadosAsync(canal));
                await _canais.GravarProprioAsync(canal);
            }

            // O GET na Meta so prova que o ID e o token servem: o canal continua "configurando" ate chegar uma mensagem de verdade.
            await _canais.MarcarVerificadoAsync(canal.Id, true, null);

            // A URL ja existe no banco antes de a Meta ser chamada: o registro dispara a validacao (GET) nela.
            var registrado = await _webhook.RegistrarAsync((await _canais.ObterAsync(canal.Id))!, primeiraTentativa: true);

            await _canais.RegistrarAuditoriaAsync(canal.Id, "conectado_proprio", null, estabelecimentoId, ator.UsuarioId,
                JsonSerializer.Serialize(new { numero, pelaLoja = !ator.Gestao, webhook = registrado.WebhookEstado }));
            _logger.LogInformation(
                "[canal] ev=conectado origem=proprio canal={Canal} loja={Loja} usuario={Usuario} numero={Numero} webhook={Webhook}",
                canal.Id, estabelecimentoId, ator.UsuarioId, numero, registrado.WebhookEstado);

            return Ver(registrado);
        }

        public async Task<CanalView> RegistrarWebhookAsync(AtendimentoAtor ator, Guid canalId)
        {
            var canal = await ObterOuFalharAsync(canalId);
            ExigirDonoOuGestao(ator, "registrar_webhook", canal.IdEstabelecimento);
            ExigirProprio(canal);

            var atualizado = await _webhook.RegistrarAsync(canal, primeiraTentativa: false);
            await _canais.RegistrarAuditoriaAsync(canalId, "webhook_registrar", canal.IdEstabelecimento, canal.IdEstabelecimento, ator.UsuarioId,
                JsonSerializer.Serialize(new { estado = atualizado.WebhookEstado }));
            return Ver(atualizado);
        }

        public async Task<CanalWebhookView> ObterWebhookAsync(AtendimentoAtor ator, Guid canalId)
        {
            var canal = await ObterOuFalharAsync(canalId);
            ExigirDonoOuGestao(ator, "ver_webhook", canal.IdEstabelecimento);
            return _webhook.Visao(canal, comSegredos: true);
        }

        private static void ExigirProprio(CanalWhatsapp canal)
        {
            if (canal.Origem == OrigemCanal.Proprio) return;

            throw new WhatsappException(new WhatsappErro(WhatsappCodigos.CampoInvalido,
                "Este numero nao usa webhook proprio.",
                "So o numero proprio do cliente tem webhook por canal; os numeros da Zippy e o de testes usam o webhook global.",
                null));
        }

        /// <summary>Formato dos 5 dados antes de gastar uma chamada na Meta. Para no primeiro campo errado, na ordem da tela.</summary>
        internal static void ValidarFormatoProprio(string phoneNumberId, string wabaId, string token, string appId, string appSecret)
        {
            ExigirIdDoNumero(phoneNumberId);

            if (!SoDigitos(wabaId, 10, 32) || wabaId == phoneNumberId) throw new WhatsappException(WhatsappErros.WabaFormato());
            if (token.Length == 0) throw new WhatsappException(WhatsappErros.TokenInvalido() with { Mensagem = "Informe o token da Meta." });
            if (!SoDigitos(appId, 8, 32)) throw new WhatsappException(WhatsappErros.AppIdFormato());
            if (appSecret.Length is < 16 or > 128 || appSecret.Any(char.IsWhiteSpace)) throw new WhatsappException(WhatsappErros.AppSecretFormato());
        }

        private static bool SoDigitos(string valor, int min, int max) => valor.Length >= min && valor.Length <= max && valor.All(char.IsAsciiDigit);

        // ---- internos --------------------------------------------------------------------------------------

        private async Task<CanalWhatsapp> ObterOuFalharAsync(Guid canalId) =>
            await _canais.ObterAsync(canalId) ?? throw new KeyNotFoundException("Numero de WhatsApp nao encontrado.");

        private void ExigirGestao(AtendimentoAtor ator, string acao, Guid estabelecimentoId)
        {
            if (ator.Gestao) return;

            _logger.LogWarning("[canal] ev=negado acao={Acao} motivo=so_gestao loja={Loja} usuario={Usuario}", acao, estabelecimentoId, ator.UsuarioId);
            throw new UnauthorizedAccessException("Somente a Gestao cadastra e altera numeros de WhatsApp.");
        }

        // Quem conecta o numero da propria loja: a Gestao ou quem tem a permissao de configurar o WhatsApp nela.
        private void ExigirDonoOuGestao(AtendimentoAtor ator, string acao, Guid estabelecimentoId)
        {
            if (ator.Gestao) return;
            if (ator.EstabelecimentoId == estabelecimentoId && ator.TemPermissao("WhatsApp", "configurar")) return;

            _logger.LogWarning("[canal] ev=negado acao={Acao} motivo=sem_permissao loja={Loja} usuario={Usuario}", acao, estabelecimentoId, ator.UsuarioId);
            throw new UnauthorizedAccessException("Voce nao tem permissao para conectar o WhatsApp desta loja.");
        }

        private static WhatsappException Invalido(string campo, string mensagem) =>
            new(new WhatsappErro(WhatsappCodigos.CampoInvalido, mensagem, "Corrija o campo indicado e tente de novo.", campo));

        private string? CifrarToken(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            if (!_token.Configurado) throw new WhatsappException(WhatsappErros.ChaveCifragemAusente());

            return _token.Proteger(token.Trim());
        }

        private static void ExigirIdDoNumero(string phoneNumberId)
        {
            if (string.IsNullOrWhiteSpace(phoneNumberId)) throw new WhatsappException(WhatsappErros.IdVazio());
            if (!AtendimentoRegras.PhoneNumberIdValido(phoneNumberId)) throw new WhatsappException(WhatsappErros.IdParecePhone());
        }

        private async Task ExecutarAsync(string acao, Guid canalId, Func<Task> operacao)
        {
            try
            {
                await operacao();
            }
            catch (CanalConflitoException ex)
            {
                _logger.LogWarning("[canal] ev=conflito acao={Acao} canal={Canal} campo={Campo}", acao, canalId, ex.Campo);
                throw new RequestValidationException("Dados invalidos.", new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [ex.Campo] = new List<string> { ex.Message }
                });
            }
        }

        private static string? LimparTexto(string? texto)
        {
            var limpo = texto?.Trim();
            if (string.IsNullOrEmpty(limpo)) return null;
            return limpo.Length > 120 ? limpo[..120] : limpo;
        }

        // Depois de uma escrita: quem escreveu pode configurar o WhatsApp da loja, entao ve URL e verify token.
        private CanalView Ver(CanalWhatsapp c) => Ver(c, detalhado: true);

        private CanalView Ver(CanalWhatsapp c, bool detalhado) => new(
            c.Id, c.IdEstabelecimento, c.PhoneNumberId, c.NumeroE164, c.Nome, c.Status, c.ModoAtendimento, c.Servicos, c.TemToken,
            c.VerificadoEm, c.UltimoRecebimentoEm, c.UltimoEnvioOkEm, c.UltimoErro, c.UltimoErroEm, c.Central,
            c.Origem, c.WabaId, c.AppId, _webhook.Visao(c, detalhado));
    }
}
