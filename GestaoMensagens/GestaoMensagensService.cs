using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using R = APIBack.GestaoMensagens.GestaoMensagensRegras;

namespace APIBack.GestaoMensagens
{
    public interface IGestaoMensagensStore
    {
        Task<AlvoMensagem?> ObterAlvoAsync(Guid? lojaId, Guid? oportunidadeId, CancellationToken ct);
        Task<IReadOnlyList<ContatoAlvo>> ContatosAsync(AlvoMensagem alvo, CancellationToken ct);
        /// <summary>Numeros da Gestao ativos, com o estado atual do canal na Meta e quem pode enviar.</summary>
        Task<IReadOnlyList<CanalGestao>> CanaisAsync(CancellationToken ct);
        /// <summary>Ultima mensagem recebida neste numero deste contato (base da janela de 24 h).</summary>
        Task<DateTimeOffset?> UltimaEntradaAsync(Guid canalId, string telefoneChave, CancellationToken ct);
        /// <summary>Somente modelos aprovados PELO MESMO numero (id_canal).</summary>
        Task<IReadOnlyList<ModeloAprovado>> ModelosAprovadosAsync(Guid canalId, CancellationToken ct);
        Task<bool> OptOutAsync(string telefoneChave, CancellationToken ct);
        Task<MensagemRegistro?> PorChaveAsync(int autor, string chave, CancellationToken ct);
        /// <summary>false = a chave de idempotencia ja existe (outra chamada igual gravou antes).</summary>
        Task<bool> InserirAsync(MensagemRegistro m, CancellationToken ct);
        Task<MensagemRegistro?> ObterAsync(Guid id, CancellationToken ct);
        /// <summary>Grava estado, referencia, erro e confirmacao. Com <paramref name="estadoEsperado"/>, so grava se o estado ainda for esse.</summary>
        Task<bool> AtualizarAsync(MensagemRegistro m, string? estadoEsperado, CancellationToken ct);
        Task<IReadOnlyList<MensagemRegistro>> HistoricoAsync(Guid? lojaId, Guid? oportunidadeId, int limite, CancellationToken ct);
        // ---- retorno (webhook) ----
        Task<bool> CanalDaGestaoAsync(Guid canalId, CancellationToken ct);
        Task<MensagemRegistro?> UltimaSaidaAsync(Guid canalId, string telefoneChave, CancellationToken ct);
        Task<MensagemRegistro?> PorReferenciaSaidaAsync(string referenciaExterna, CancellationToken ct);
    }

    /// <summary>Sender da Meta: usa o WhatsAppSender, o token e o canal que ja existem. Devolve o id da mensagem na Meta (wamid).</summary>
    public interface IGestaoEnviadorMeta
    {
        Task<string?> EnviarTextoAsync(CanalGestao canal, string destinoE164, string texto, CancellationToken ct);
        Task<string?> EnviarModeloAsync(CanalGestao canal, string destinoE164, ModeloAprovado modelo, IReadOnlyList<string> corpo, string? sufixoBotaoUrl, CancellationToken ct);
    }

    /// <summary>Painel da loja = Chamados da Zippy (um chamado aberto da loja, ou um novo). Devolve o id do chamado.</summary>
    public interface IGestaoPainel
    {
        Task<long> EnviarAsync(Guid lojaId, int autorUsuarioId, string texto, long? chamadoId, CancellationToken ct);
    }

    /// <summary>Marco de contato real (enviada / recebida) do relacionamento: unico gatilho de "aguardando loja" e "esperando resposta".</summary>
    public interface IGestaoMarcos
    {
        Task RegistrarAsync(Guid referencia, string direcao, DateTimeOffset em, string referenciaMensagem, CancellationToken ct);
    }

    public sealed record AtorGestao(int UsuarioId, string? Nome, bool EquipeZippy);

    public interface IGestaoMensagensService
    {
        Task<IReadOnlyList<CanalGestaoDto>> CanaisAsync(AtorGestao ator, CancellationToken ct);
        Task<OpcoesMensagemDto> OpcoesAsync(AtorGestao ator, Guid? lojaId, Guid? oportunidadeId, string? contatoId, CancellationToken ct);
        Task<MensagemDto> EnviarAsync(AtorGestao ator, EnviarMensagemRequest? request, CancellationToken ct);
        Task<MensagemDto> ConfirmarEnvioExternoAsync(AtorGestao ator, Guid id, CancellationToken ct);
        Task<IReadOnlyList<MensagemDto>> HistoricoAsync(AtorGestao ator, Guid? lojaId, Guid? oportunidadeId, CancellationToken ct);
    }

    public sealed class GestaoMensagensService : IGestaoMensagensService
    {
        private readonly IGestaoMensagensStore _store;
        private readonly IGestaoEnviadorMeta _meta;
        private readonly IGestaoPainel _painel;
        private readonly IGestaoMarcos _marcos;
        private readonly TimeProvider _clock;
        private readonly ILogger _logger;

        public GestaoMensagensService(IGestaoMensagensStore store, IGestaoEnviadorMeta meta, IGestaoPainel painel, IGestaoMarcos marcos,
            TimeProvider? clock = null, ILogger<GestaoMensagensService>? logger = null)
        {
            _store = store;
            _meta = meta;
            _painel = painel;
            _marcos = marcos;
            _clock = clock ?? TimeProvider.System;
            _logger = (ILogger?)logger ?? NullLogger.Instance;
        }

        private DateTimeOffset Agora => _clock.GetUtcNow();

        private static void ExigirEquipe(AtorGestao ator)
        {
            if (!ator.EquipeZippy)
                throw new GestaoMensagensException(403, "SEM_PERMISSAO", "So a equipe da Zippy envia mensagens da Gestao.", "Entre com um usuario da equipe.");
        }

        // ---- canais ---------------------------------------------------------------------------------------------------------

        public async Task<IReadOnlyList<CanalGestaoDto>> CanaisAsync(AtorGestao ator, CancellationToken ct)
        {
            ExigirEquipe(ator);
            var canais = await _store.CanaisAsync(ct);
            return canais.Where(c => !c.Pessoal || c.DonoUsuarioId == ator.UsuarioId)
                .Select(c => new CanalGestaoDto($"{CanaisMensagem.PrefixoNumero}{c.CanalId}", c.CanalId, c.Nome, c.NumeroE164, c.Finalidade, c.DonoUsuarioId,
                    c.ConectadoMeta, c.EstadoMeta, c.Remetentes, c.PodeEnviar(ator.UsuarioId)))
                .ToList();
        }

        // ---- opcoes ---------------------------------------------------------------------------------------------------------

        private async Task<(AlvoMensagem Alvo, IReadOnlyList<ContatoAlvo> Contatos)> ResolverAlvoAsync(Guid? lojaId, Guid? oportunidadeId, CancellationToken ct)
        {
            if (lojaId.HasValue == oportunidadeId.HasValue)
                throw new GestaoMensagensException(422, CodigosGestaoMensagem.ParametrosInvalidos, "Informe lojaId ou oportunidadeId (so um dos dois).");
            var alvo = await _store.ObterAlvoAsync(lojaId, oportunidadeId, ct)
                ?? throw new GestaoMensagensException(404, CodigosGestaoMensagem.AlvoNaoEncontrado,
                    lojaId.HasValue ? "Loja nao encontrada." : "Oportunidade nao encontrada.", "Confira o id na carteira.");
            return (alvo, await _store.ContatosAsync(alvo, ct));
        }

        private static ContatoAlvo EscolherContato(IReadOnlyList<ContatoAlvo> contatos, string? contatoId)
        {
            if (!string.IsNullOrWhiteSpace(contatoId))
                return contatos.FirstOrDefault(c => string.Equals(c.Id, contatoId.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new GestaoMensagensException(404, CodigosGestaoMensagem.ContatoNaoEncontrado, "Esse contato nao pertence a este destino.",
                        "Escolha um dos contatos listados em /api/gestao/mensagens/opcoes.", contatos);
            if (contatos.Count == 1) return contatos[0];
            if (contatos.Count == 0)
                throw new GestaoMensagensException(422, CodigosGestaoMensagem.ContatoNaoEncontrado, "Este destino nao tem nenhum contato com telefone.",
                    "Cadastre o telefone do contato e tente de novo.");
            var confirmados = contatos.Where(c => c.Confirmado).ToList();
            throw new GestaoMensagensException(409, CodigosGestaoMensagem.ContatoAmbiguo, "Ha mais de um contato: escolha para quem enviar.",
                "Envie de novo informando contatoId.", confirmados.Count > 0 ? confirmados : contatos);
        }

        public async Task<OpcoesMensagemDto> OpcoesAsync(AtorGestao ator, Guid? lojaId, Guid? oportunidadeId, string? contatoId, CancellationToken ct)
        {
            ExigirEquipe(ator);
            var (alvo, contatos) = await ResolverAlvoAsync(lojaId, oportunidadeId, ct);
            var contato = EscolherContato(contatos, contatoId);
            var canais = await _store.CanaisAsync(ct);
            var agora = Agora;
            var opcoes = new List<OpcaoCanalDto>();

            if (alvo.Tipo == "loja")
                opcoes.Add(new OpcaoCanalDto(CanaisMensagem.Painel, "Painel da loja (chamado)", "painel", true, "interno", null, null, false, Array.Empty<ModeloOpcaoDto>(),
                    null, null, Array.Empty<string>()));
            else
                opcoes.Add(new OpcaoCanalDto(CanaisMensagem.Painel, "Painel da loja (chamado)", "painel", false, "interno", null, null, false, Array.Empty<ModeloOpcaoDto>(),
                    "O lead ainda nao tem painel.", "Use um numero da Zippy ou o seu WhatsApp.", Array.Empty<string>()));

            var meu = canais.FirstOrDefault(c => c.Pessoal && c.DonoUsuarioId == ator.UsuarioId);
            foreach (var canal in canais.Where(c => !c.Pessoal).Concat(meu is { ConectadoMeta: true } ? new[] { meu } : Array.Empty<CanalGestao>()))
                opcoes.Add(await AvaliarNumeroAsync(canal, ator, contato, agora, ct));

            var disponiveis = opcoes.Where(o => o.Disponivel && o.Canal.StartsWith(CanaisMensagem.PrefixoNumero)).Select(o => o.Canal).ToList();
            for (var i = 0; i < opcoes.Count; i++)
                if (!opcoes[i].Disponivel && opcoes[i].Canal.StartsWith(CanaisMensagem.PrefixoNumero))
                    opcoes[i] = opcoes[i] with { Alternativas = disponiveis };

            if (meu is not { ConectadoMeta: true })
                opcoes.Add(new OpcaoCanalDto(CanaisMensagem.Pessoal, "Meu WhatsApp (envio por fora)", "pessoal", contato.Confirmado, "manual", null, null, false,
                    Array.Empty<ModeloOpcaoDto>(), contato.Confirmado ? null : "O contato nao tem telefone confirmado.",
                    contato.Confirmado ? "Abre o WhatsApp com o texto pronto; depois confirme que enviou. A resposta nao chega ao sistema." : "Cadastre o telefone do contato.",
                    Array.Empty<string>()));
            return new OpcoesMensagemDto(alvo, contato, contatos, opcoes);
        }

        private async Task<OpcaoCanalDto> AvaliarNumeroAsync(CanalGestao canal, AtorGestao ator, ContatoAlvo contato, DateTimeOffset agora, CancellationToken ct)
        {
            var id = $"{CanaisMensagem.PrefixoNumero}{canal.CanalId}";
            OpcaoCanalDto Indisponivel(string motivo, string proximo, bool? janela = null, DateTimeOffset? ate = null, bool exige = false, IReadOnlyList<ModeloOpcaoDto>? modelos = null) =>
                new(id, canal.Nome, canal.Finalidade, false, "completo", janela, ate, exige, modelos ?? Array.Empty<ModeloOpcaoDto>(), motivo, proximo, Array.Empty<string>());

            if (!canal.PodeEnviar(ator.UsuarioId))
                return Indisponivel("Voce nao esta na lista de quem pode enviar por este numero.", "Peca a um gestor para liberar seu usuario neste numero.");
            if (!canal.Ativo || !canal.ConectadoMeta)
                return Indisponivel($"O numero nao esta conectado na Meta ({canal.EstadoMeta ?? "sem estado"}).", "Reconecte o numero em Atendimento > Numeros de WhatsApp.");
            if (!contato.Confirmado)
                return Indisponivel("O contato nao tem telefone confirmado.", "Cadastre o telefone (E.164) do contato.");

            var chave = R.ChaveTelefone(contato.TelefoneE164)!;
            var ate = R.JanelaAte(await _store.UltimaEntradaAsync(canal.CanalId, chave, ct), agora);
            var modelos = (await _store.ModelosAprovadosAsync(canal.CanalId, ct))
                .Select(m => new ModeloOpcaoDto(m.Id, m.Nome, m.Idioma, m.Categoria, m.QtdVariaveis, m.BotaoUrlVariavel)).ToList();
            var aberta = ate.HasValue;
            if (!aberta && modelos.Count == 0)
                return Indisponivel("Janela de 24 horas fechada neste numero e ele nao tem modelo aprovado.",
                    "Crie e aprove um modelo neste numero (Atendimento > Modelos) ou use outro numero com a janela aberta.", false, null, true);
            return new OpcaoCanalDto(id, canal.Nome, canal.Finalidade, true, "completo", aberta, ate, !aberta, modelos, null,
                aberta ? null : "A janela esta fechada: envie um modelo aprovado.", Array.Empty<string>());
        }

        // ---- enviar ---------------------------------------------------------------------------------------------------------

        public async Task<MensagemDto> EnviarAsync(AtorGestao ator, EnviarMensagemRequest? request, CancellationToken ct)
        {
            ExigirEquipe(ator);
            if (request == null) throw new GestaoMensagensException(422, CodigosGestaoMensagem.ParametrosInvalidos, "Corpo da requisicao ausente.");
            var chave = request.ChaveIdempotencia?.Trim();
            if (string.IsNullOrEmpty(chave) || chave.Length > 120)
                throw new GestaoMensagensException(422, CodigosGestaoMensagem.ParametrosInvalidos, "Informe a chave de idempotencia (ate 120 caracteres).",
                    "Gere uma chave por clique em Enviar e reutilize-a so em nova tentativa do mesmo envio.");
            var canalId = (request.Canal ?? string.Empty).Trim().ToLowerInvariant();
            var finalidade = string.IsNullOrWhiteSpace(request.Finalidade) ? FinalidadeMensagem.Utilidade : request.Finalidade.Trim().ToLowerInvariant();
            if (finalidade is not (FinalidadeMensagem.Utilidade or FinalidadeMensagem.Marketing))
                throw new GestaoMensagensException(422, CodigosGestaoMensagem.ParametrosInvalidos, "Finalidade deve ser utilidade ou marketing.");
            var temTexto = !string.IsNullOrWhiteSpace(request.Texto);
            if (temTexto == (request.Modelo != null))
                throw new GestaoMensagensException(422, CodigosGestaoMensagem.ParametrosInvalidos, "Envie texto OU modelo com variaveis (um dos dois).");
            if (temTexto && request.Texto!.Length > 4096)
                throw new GestaoMensagensException(422, CodigosGestaoMensagem.ParametrosInvalidos, "O texto passa de 4096 caracteres.");

            var existente = await _store.PorChaveAsync(ator.UsuarioId, chave, ct);
            if (existente != null) return Repetida(existente, request);

            var (alvo, contatos) = await ResolverAlvoAsync(request.LojaId, request.OportunidadeId, ct);

            if (canalId == CanaisMensagem.Painel) return await EnviarPainelAsync(ator, alvo, request, chave, finalidade, ct);

            var contato = EscolherContato(contatos, request.ContatoId);
            if (!contato.Confirmado)
                throw new GestaoMensagensException(422, CodigosGestaoMensagem.ContatoNaoConfirmado, "O contato nao tem telefone confirmado.", "Cadastre o telefone (E.164) do contato.");

            if (canalId == CanaisMensagem.Pessoal)
            {
                var meu = (await _store.CanaisAsync(ct)).FirstOrDefault(c => c.Pessoal && c.DonoUsuarioId == ator.UsuarioId);
                if (meu is { ConectadoMeta: true, Ativo: true }) return await EnviarNumeroAsync(ator, alvo, contato, meu, request, chave, finalidade, ct);
                return await PrepararPessoalAsync(ator, alvo, contato, request, chave, finalidade, ct);
            }

            if (!canalId.StartsWith(CanaisMensagem.PrefixoNumero) || !Guid.TryParse(canalId[CanaisMensagem.PrefixoNumero.Length..], out var guid))
                throw new GestaoMensagensException(422, CodigosGestaoMensagem.CanalInvalido, "Canal deve ser painel, numero:{canalId} ou pessoal.");
            var canal = (await _store.CanaisAsync(ct)).FirstOrDefault(c => c.CanalId == guid && (!c.Pessoal || c.DonoUsuarioId == ator.UsuarioId))
                ?? throw new GestaoMensagensException(404, CodigosGestaoMensagem.CanalNaoEncontrado, "Esse numero nao existe na Gestao.", "Liste os numeros em GET /api/gestao/canais.");
            return await EnviarNumeroAsync(ator, alvo, contato, canal, request, chave, finalidade, ct);
        }

        private static MensagemDto Repetida(MensagemRegistro m, EnviarMensagemRequest request)
        {
            var mesmoAlvo = (m.EstabelecimentoId == request.LojaId && m.OportunidadeId == request.OportunidadeId);
            if (!mesmoAlvo)
                throw new GestaoMensagensException(409, CodigosGestaoMensagem.ChaveReutilizada, "Essa chave de idempotencia ja foi usada para outro destino.",
                    "Gere uma chave nova para este envio.");
            return Dto(m, repetida: true);
        }

        private async Task<MensagemDto> EnviarPainelAsync(AtorGestao ator, AlvoMensagem alvo, EnviarMensagemRequest request, string chave, string finalidade, CancellationToken ct)
        {
            if (alvo.Tipo != "loja")
                throw new GestaoMensagensException(422, CodigosGestaoMensagem.CanalInvalido, "O painel so serve para lojas: o lead ainda nao tem painel.",
                    "Use numero:{canalId} ou pessoal.");
            if (request.Modelo != null)
                throw new GestaoMensagensException(422, CodigosGestaoMensagem.CanalInvalido, "O painel nao usa modelo: envie texto.");
            var texto = request.Texto!.Trim();
            var m = Novo(ator, alvo, null, chave, ViaMensagem.Painel, null, null, finalidade);
            m.Texto = texto;
            if (!await _store.InserirAsync(m, ct)) return Repetida((await _store.PorChaveAsync(ator.UsuarioId, chave, ct))!, request);

            try
            {
                m.ChamadoId = await _painel.EnviarAsync(alvo.LojaId!.Value, ator.UsuarioId, texto, request.ChamadoId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await Falhar(m, "PAINEL_INDISPONIVEL", ex is GestaoMensagensException ? ex.Message : "Nao foi possivel gravar no painel da loja.", ct);
                if (ex is GestaoMensagensException) throw;
                _logger.LogWarning(ex, "[gestao.msg] ev=painel_falhou msg={Msg}", m.Id);
                throw new GestaoMensagensException(502, "PAINEL_INDISPONIVEL", "Nao foi possivel gravar no painel da loja.", "Tente de novo com uma chave nova.");
            }
            m.Estado = EstadoMensagem.Enviado;
            m.EnviadoEm = Agora;
            await _store.AtualizarAsync(m, EstadoMensagem.Enviando, ct);
            var aviso = await MarcarEnviadaAsync(alvo, m, $"painel:{m.Id:N}", ct);
            return Dto(m, aviso: aviso);
        }

        private async Task<MensagemDto> EnviarNumeroAsync(AtorGestao ator, AlvoMensagem alvo, ContatoAlvo contato, CanalGestao canal, EnviarMensagemRequest request,
            string chave, string finalidade, CancellationToken ct)
        {
            if (!canal.PodeEnviar(ator.UsuarioId))
                throw new GestaoMensagensException(403, CodigosGestaoMensagem.SemPermissaoNoCanal, $"Voce nao pode enviar pelo numero {canal.Nome}.",
                    "Peca a um gestor para liberar seu usuario neste numero ou escolha outro.");
            if (!canal.Ativo || !canal.ConectadoMeta)
                throw new GestaoMensagensException(409, CodigosGestaoMensagem.CanalIndisponivel, $"O numero {canal.Nome} nao esta conectado na Meta ({canal.EstadoMeta ?? "sem estado"}).",
                    "Reconecte o numero em Atendimento > Numeros de WhatsApp ou use outro numero.");

            var telefoneChave = R.ChaveTelefone(contato.TelefoneE164)!;
            var agora = Agora;
            var janelaAte = R.JanelaAte(await _store.UltimaEntradaAsync(canal.CanalId, telefoneChave, ct), agora);
            var modelos = await _store.ModelosAprovadosAsync(canal.CanalId, ct);

            var m = Novo(ator, alvo, contato, chave, ViaMensagem.Numero, canal.CanalId, telefoneChave, finalidade);
            ModeloAprovado? modelo = null;
            IReadOnlyList<string> variaveis = Array.Empty<string>();

            if (request.Modelo != null)
            {
                modelo = ResolverModelo(request.Modelo, modelos, canal);
                var erro = R.ErroVariaveis(modelo.QtdVariaveis, request.Modelo.Variaveis);
                if (erro != null)
                    throw new GestaoMensagensException(422, CodigosGestaoMensagem.VariaveisInvalidas, erro, $"Preencha as variaveis do modelo {modelo.Nome} deste numero.");
                variaveis = request.Modelo.Variaveis!;
                if (modelo.BotaoUrlVariavel && string.IsNullOrWhiteSpace(request.Modelo.SufixoBotaoUrl))
                    throw new GestaoMensagensException(422, CodigosGestaoMensagem.VariaveisInvalidas, "O botao de link do modelo precisa do sufixo (sufixoBotaoUrl).");
                if (string.Equals(modelo.Categoria, "MARKETING", StringComparison.OrdinalIgnoreCase)) finalidade = FinalidadeMensagem.Marketing;
                m.Finalidade = finalidade;
                m.TipoConteudo = "modelo";
                m.ModeloNome = modelo.Nome;
                m.ModeloIdioma = modelo.Idioma;
                m.ModeloCategoria = modelo.Categoria;
                m.VariaveisJson = JsonSerializer.Serialize(variaveis);
                m.Texto = R.Preencher(modelo.Corpo, variaveis);
            }
            else
            {
                if (finalidade == FinalidadeMensagem.Marketing)
                    throw new GestaoMensagensException(422, CodigosGestaoMensagem.ModeloNaoAprovado, "Marketing so sai por modelo aprovado de categoria marketing.",
                        "Escolha um modelo aprovado deste numero.");
                if (janelaAte == null)
                    throw await JanelaFechadaAsync(ator, canal, contato, modelos, agora, ct);
                m.Texto = request.Texto!.Trim();
            }

            if (finalidade == FinalidadeMensagem.Marketing)
            {
                if (await _store.OptOutAsync(telefoneChave, ct))
                    throw new GestaoMensagensException(422, CodigosGestaoMensagem.OptOut, "Este contato pediu para nao receber mensagens de marketing.",
                        "Use um modelo de utilidade ligado a um assunto real ou nao envie.");
                if (!request.ConsentimentoMarketing)
                    throw new GestaoMensagensException(422, CodigosGestaoMensagem.ConsentimentoObrigatorio, "Marketing exige consentimento do contato.",
                        "Confirme o consentimento (consentimentoMarketing=true) so se o contato autorizou esse tipo de mensagem.");
            }

            if (!await _store.InserirAsync(m, ct)) return Repetida((await _store.PorChaveAsync(ator.UsuarioId, chave, ct))!, request);

            string? wamid;
            try
            {
                wamid = modelo == null
                    ? await _meta.EnviarTextoAsync(canal, contato.TelefoneE164!, m.Texto!, ct)
                    : await _meta.EnviarModeloAsync(canal, contato.TelefoneE164!, modelo, variaveis, request.Modelo!.SufixoBotaoUrl, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[gestao.msg] ev=meta_falhou msg={Msg} canal={Canal}", m.Id, canal.CanalId);
                await Falhar(m, CodigosGestaoMensagem.FalhaNaMeta, "A Meta nao confirmou o envio.", ct);
                throw new GestaoMensagensException(502, CodigosGestaoMensagem.FalhaNaMeta, $"A Meta nao confirmou o envio pelo numero {canal.Nome}. Nada foi marcado como enviado.",
                    "Veja o estado do numero e tente de novo com uma chave nova.", new { mensagemId = m.Id, estado = EstadoMensagem.Falhou });
            }

            if (string.IsNullOrWhiteSpace(wamid))
            {
                await Falhar(m, "SEM_CONFIRMACAO_META", "A Meta nao devolveu o id da mensagem.", ct);
                throw new GestaoMensagensException(502, CodigosGestaoMensagem.FalhaNaMeta, "A Meta nao devolveu o id da mensagem; o envio nao foi confirmado.",
                    "Tente de novo com uma chave nova.", new { mensagemId = m.Id, estado = EstadoMensagem.Falhou });
            }

            m.ReferenciaExterna = wamid;
            m.Estado = EstadoMensagem.Enviado;
            m.EnviadoEm = Agora;
            await _store.AtualizarAsync(m, EstadoMensagem.Enviando, ct);
            var aviso = await MarcarEnviadaAsync(alvo, m, wamid, ct);
            return Dto(m, aviso: aviso, canalNome: canal.Nome);
        }

        private static ModeloAprovado ResolverModelo(ModeloEnvio pedido, IReadOnlyList<ModeloAprovado> modelos, CanalGestao canal)
        {
            var achado = pedido.Id.HasValue
                ? modelos.FirstOrDefault(m => m.Id == pedido.Id.Value)
                : modelos.FirstOrDefault(m => string.Equals(m.Nome, pedido.Nome?.Trim(), StringComparison.OrdinalIgnoreCase));
            return achado ?? throw new GestaoMensagensException(422, CodigosGestaoMensagem.ModeloNaoAprovado,
                $"Esse modelo nao esta aprovado no numero {canal.Nome}. Modelo de outro numero nao serve.",
                modelos.Count == 0 ? "Crie e aprove um modelo neste numero (Atendimento > Modelos)." : "Escolha um dos modelos aprovados deste numero.",
                modelos.Select(m => new ModeloOpcaoDto(m.Id, m.Nome, m.Idioma, m.Categoria, m.QtdVariaveis, m.BotaoUrlVariavel)).ToList());
        }

        private async Task<GestaoMensagensException> JanelaFechadaAsync(AtorGestao ator, CanalGestao canal, ContatoAlvo contato, IReadOnlyList<ModeloAprovado> modelos,
            DateTimeOffset agora, CancellationToken ct)
        {
            var alternativas = new List<string>();
            foreach (var outro in (await _store.CanaisAsync(ct)).Where(c => c.CanalId != canal.CanalId && !c.Pessoal && c.Ativo && c.ConectadoMeta && c.PodeEnviar(ator.UsuarioId)))
            {
                var aberta = R.JanelaAte(await _store.UltimaEntradaAsync(outro.CanalId, R.ChaveTelefone(contato.TelefoneE164)!, ct), agora).HasValue;
                if (aberta || (await _store.ModelosAprovadosAsync(outro.CanalId, ct)).Count > 0) alternativas.Add($"{CanaisMensagem.PrefixoNumero}{outro.CanalId}");
            }
            var passo = modelos.Count > 0
                ? $"Envie um dos modelos aprovados deste numero ({string.Join(", ", modelos.Take(5).Select(m => m.Nome))})."
                : "Este numero nao tem modelo aprovado: crie um em Atendimento > Modelos" + (alternativas.Count > 0 ? " ou use outro numero." : ".");
            return new GestaoMensagensException(422, CodigosGestaoMensagem.JanelaFechadaSemModelo,
                $"A janela de 24 horas do numero {canal.Nome} com este contato esta fechada; so modelo aprovado pode iniciar a conversa.", passo,
                new
                {
                    janelaAberta = false,
                    modelos = modelos.Select(m => new ModeloOpcaoDto(m.Id, m.Nome, m.Idioma, m.Categoria, m.QtdVariaveis, m.BotaoUrlVariavel)).ToList(),
                    alternativas
                });
        }

        private async Task<MensagemDto> PrepararPessoalAsync(AtorGestao ator, AlvoMensagem alvo, ContatoAlvo contato, EnviarMensagemRequest request, string chave,
            string finalidade, CancellationToken ct)
        {
            if (request.Modelo != null)
                throw new GestaoMensagensException(422, CodigosGestaoMensagem.CanalInvalido, "Modelo so existe em numero conectado na Meta; no WhatsApp pessoal envie texto.");
            var m = Novo(ator, alvo, contato, chave, ViaMensagem.Pessoal, null, R.ChaveTelefone(contato.TelefoneE164), finalidade);
            m.Texto = request.Texto!.Trim();
            m.Estado = EstadoMensagem.AguardandoConfirmacao;
            if (!await _store.InserirAsync(m, ct)) return Repetida((await _store.PorChaveAsync(ator.UsuarioId, chave, ct))!, request);
            return Dto(m, link: R.LinkWhatsapp(contato.TelefoneE164!, m.Texto),
                aviso: "Nada foi enviado ainda. Envie pelo seu WhatsApp e confirme; a resposta do contato nao chega ao sistema.");
        }

        public async Task<MensagemDto> ConfirmarEnvioExternoAsync(AtorGestao ator, Guid id, CancellationToken ct)
        {
            ExigirEquipe(ator);
            var m = await _store.ObterAsync(id, ct) ?? throw new GestaoMensagensException(404, CodigosGestaoMensagem.MensagemNaoEncontrada, "Mensagem nao encontrada.");
            if (m.Via != ViaMensagem.Pessoal)
                throw new GestaoMensagensException(409, CodigosGestaoMensagem.EstadoInvalido, "So envio pelo WhatsApp pessoal precisa de confirmacao manual.");
            if (m.AutorUsuarioId != ator.UsuarioId)
                throw new GestaoMensagensException(403, "SEM_PERMISSAO", "So quem preparou a mensagem confirma que a enviou.");
            if (m.Estado == EstadoMensagem.EnviadoPorFora) return Dto(m, repetida: true);
            if (m.Estado != EstadoMensagem.AguardandoConfirmacao)
                throw new GestaoMensagensException(409, CodigosGestaoMensagem.EstadoInvalido, $"Esta mensagem esta '{m.Estado}' e nao aceita confirmacao.");

            var agora = Agora;
            m.Estado = EstadoMensagem.EnviadoPorFora;
            m.ConfirmadoPor = ator.UsuarioId;
            m.ConfirmadoEm = agora;
            m.EnviadoEm = agora;
            if (!await _store.AtualizarAsync(m, EstadoMensagem.AguardandoConfirmacao, ct))
                return Dto((await _store.ObterAsync(id, ct))!, repetida: true);
            var alvo = m.EstabelecimentoId.HasValue ? new AlvoMensagem("loja", m.EstabelecimentoId, null, string.Empty) : new AlvoMensagem("oportunidade", null, m.OportunidadeId, string.Empty);
            var aviso = await MarcarEnviadaAsync(alvo, m, $"externo:{m.Id:N}", ct, agora) ?? "Registrado como enviado por fora, confirmado por voce. Sem recibo da Meta.";
            return Dto(m, aviso: aviso);
        }

        public async Task<IReadOnlyList<MensagemDto>> HistoricoAsync(AtorGestao ator, Guid? lojaId, Guid? oportunidadeId, CancellationToken ct)
        {
            ExigirEquipe(ator);
            await ResolverAlvoAsync(lojaId, oportunidadeId, ct);
            return (await _store.HistoricoAsync(lojaId, oportunidadeId, 200, ct)).Select(m => Dto(m)).ToList();
        }

        // ---- apoio ----------------------------------------------------------------------------------------------------------

        private MensagemRegistro Novo(AtorGestao ator, AlvoMensagem alvo, ContatoAlvo? contato, string chave, string via, Guid? canalId, string? telefoneChave, string finalidade) => new()
        {
            Id = Guid.NewGuid(),
            Direcao = DirecaoMensagem.Saida,
            Via = via,
            CanalId = canalId,
            AlvoTipo = alvo.Tipo,
            EstabelecimentoId = alvo.LojaId,
            OportunidadeId = alvo.OportunidadeId,
            ContatoRef = contato?.Id,
            TelefoneE164 = contato?.TelefoneE164,
            TelefoneChave = telefoneChave,
            AutorUsuarioId = ator.UsuarioId,
            Finalidade = finalidade,
            Estado = EstadoMensagem.Enviando,
            ChaveIdempotencia = chave,
            CriadoEm = Agora
        };

        private async Task Falhar(MensagemRegistro m, string codigo, string detalhe, CancellationToken ct)
        {
            m.Estado = EstadoMensagem.Falhou;
            m.ErroCodigo = codigo;
            m.ErroDetalhe = detalhe.Length > 300 ? detalhe[..300] : detalhe;
            await _store.AtualizarAsync(m, EstadoMensagem.Enviando, CancellationToken.None);
        }

        /// <summary>So depois de envio real (ou confirmacao humana): grava o marco "enviada" que vira "aguardando loja". Falha aqui nao desfaz o envio.</summary>
        private async Task<string?> MarcarEnviadaAsync(AlvoMensagem alvo, MensagemRegistro m, string referencia, CancellationToken ct, DateTimeOffset? em = null)
        {
            try
            {
                await _marcos.RegistrarAsync(alvo.ReferenciaRelacionamento, "enviada", em ?? m.EnviadoEm ?? Agora, referencia, ct);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[gestao.msg] ev=marco_falhou msg={Msg}", m.Id);
                return "Mensagem enviada, mas o estado 'aguardando loja' nao foi atualizado; avise o suporte.";
            }
        }

        public static MensagemDto Dto(MensagemRegistro m, bool repetida = false, string? aviso = null, string? link = null, string? canalNome = null) => new(
            m.Id, m.Direcao, m.Via, m.Estado, m.CanalId.HasValue ? $"{CanaisMensagem.PrefixoNumero}{m.CanalId}" : m.Via, m.Texto,
            m.ModeloNome, m.Finalidade, m.ReferenciaExterna, m.ErroCodigo, m.ErroDetalhe, m.ChamadoId, link, repetida,
            aviso ?? (m.Estado == EstadoMensagem.EnviadoPorFora ? "Enviado por fora, confirmado por uma pessoa. Sem recibo da Meta." : null), m.CriadoEm);
    }
}
