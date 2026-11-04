using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Service;
using APIBack.Service.Interface;

namespace APIBack.Acompanhamento.Fases
{
    /// <summary>
    /// Transacao de uma passagem: a linha do acompanhamento ja esta travada e conciliada com a situacao da loja.
    /// Sem <see cref="CommitAsync"/>, o dispose desfaz tudo.
    /// </summary>
    public interface IAcompanhamentoTransacao : IAsyncDisposable
    {
        AcompanhamentoEstado Estado { get; }
        Task<PassagemRegistroDto?> PassagemPorChaveAsync(string chave, CancellationToken ct);
        Task<bool> CobrancaEmAbertoAsync(CancellationToken ct);
        /// <summary>A versao enviada ou em negociacao. Rascunho nunca conta.</summary>
        Task<PropostaVersao?> PropostaVigenteAsync(CancellationToken ct);
        Task<IReadOnlyList<PropostaVersao>> PropostasAsync(CancellationToken ct);
        Task<PropostaContexto> ContextoPropostaAsync(CancellationToken ct);
        Task<PropostaVersao> CriarPropostaAsync(PropostaVersao nova, CancellationToken ct);
        /// <summary>Grava a linha e sobe a <c>edicao</c>.</summary>
        Task<PropostaVersao> SalvarPropostaAsync(PropostaVersao versao, CancellationToken ct);
        /// <summary>Espelha a versao enviada em crm_oportunidade (proposta_json, proposta_enviada_em) para o /crm legado.</summary>
        Task EspelharPropostaAsync(PropostaVersao versao, int? usuarioId, CancellationToken ct);
        /// <summary>A mensagem existe em gestao_mensagem, saiu de verdade e e desta oportunidade ou loja.</summary>
        Task<bool> MensagemConfereAsync(Guid mensagemId, CancellationToken ct);
        /// <summary>Linha na trilha unica (acompanhamento_relacionamento_evento), lida pela jornada.</summary>
        Task EventoAsync(string tipo, object detalhe, int? usuarioId, CancellationToken ct);
        /// <summary>Efeitos da passagem (loja, situacao, proposta), novo estado, trilha e chave de idempotencia, tudo junto.</summary>
        Task<PassagemRegistroDto> AplicarAsync(PassagemAplicacao aplicacao, CancellationToken ct);
        Task CommitAsync(CancellationToken ct);
    }

    public interface IAcompanhamentoStore
    {
        /// <summary>
        /// Acompanhamento pelo id dele, da oportunidade ou da loja. Na primeira vez cria a partir do que a Gestao ja sabe
        /// (coluna da oportunidade ou situacao da loja). Nulo = nada com esse id.
        /// </summary>
        Task<AcompanhamentoEstado?> ResolverAsync(Guid id, CancellationToken ct);
        Task<IAcompanhamentoTransacao> AbrirAsync(Guid acompanhamentoId, CancellationToken ct);
        Task<IReadOnlyList<HistoricoDto>> HistoricoAsync(Guid acompanhamentoId, CancellationToken ct);
    }

    public interface IAcompanhamentoService
    {
        PassagensCatalogoDto Catalogo();
        Task<AvaliacaoPassagemDto> AvaliarAsync(Guid id, string para, DadosPassagem? dados, CancellationToken ct = default);
        Task<PassagemResultadoDto> ExecutarAsync(Guid id, string para, DadosPassagem dados, int? usuarioId, string autorTipo, CancellationToken ct = default);
        Task<PropostasDto> PropostasAsync(Guid id, CancellationToken ct = default);
        Task<PropostaResultadoDto> CriarPropostaAsync(Guid id, PropostaRequest request, int? usuarioId, CancellationToken ct = default);
        Task<PropostaResultadoDto> EditarPropostaAsync(Guid id, Guid versaoId, PropostaRequest request, int? usuarioId, CancellationToken ct = default);
        Task<PropostaResultadoDto> EnviarPropostaAsync(Guid id, Guid versaoId, EnviarPropostaRequest request, int? usuarioId, CancellationToken ct = default);
        Task<PropostaResultadoDto> ResponderPropostaAsync(Guid id, Guid versaoId, ResponderPropostaRequest request, int? usuarioId, CancellationToken ct = default);
    }

    /// <summary>
    /// Passagens entre fases (item 2). Ordem: trava a linha, confere a chave de idempotencia, confere a fase que a tela viu,
    /// avalia os requisitos e aplica tudo numa transacao. Duas passagens ao mesmo tempo na mesma loja ficam em fila pela trava;
    /// a segunda ve a fase nova e recebe 409 (FASE_MUDOU) ou 422.
    /// </summary>
    public sealed class AcompanhamentoService : IAcompanhamentoService
    {
        private readonly IAcompanhamentoStore _store;
        private readonly IProntidaoService _prontidao;
        private readonly ICrmService _crm;
        private readonly TimeProvider _clock;

        public AcompanhamentoService(IAcompanhamentoStore store, IProntidaoService prontidao, ICrmService crm, TimeProvider? clock = null)
        {
            _store = store;
            _prontidao = prontidao;
            _crm = crm;
            _clock = clock ?? TimeProvider.System;
        }

        public PassagensCatalogoDto Catalogo() => PassagemMotor.Catalogo();

        public async Task<AvaliacaoPassagemDto> AvaliarAsync(Guid id, string para, DadosPassagem? dados, CancellationToken ct = default)
        {
            para = ValidarFase(para);
            var estado = await _store.ResolverAsync(id, ct) ?? throw NaoEncontrado();
            var pendencias = await PendenciasAsync(estado, ct);
            await using var tx = await _store.AbrirAsync(estado.Id, ct);
            var fatos = PassagemMotor.Fatos(tx.Estado, pendencias, await tx.CobrancaEmAbertoAsync(ct), await tx.PropostaVigenteAsync(ct));
            // So grava a conciliacao com a situacao da loja (quando houve); a avaliacao nao muda nada.
            await tx.CommitAsync(ct);
            return PassagemMotor.Avaliar(tx.Estado.Id, tx.Estado.Versao, fatos, para, dados ?? new DadosPassagem(), _clock.GetUtcNow());
        }

        public async Task<PassagemResultadoDto> ExecutarAsync(Guid id, string para, DadosPassagem dados, int? usuarioId, string autorTipo, CancellationToken ct = default)
        {
            para = ValidarFase(para);
            var chave = (dados.ChaveIdempotencia ?? string.Empty).Trim();
            if (chave.Length is 0 or > 120)
                throw new AcompanhamentoException(422, CodigosAcompanhamento.ChaveIdempotenciaObrigatoria,
                    "Informe a chave de idempotência (até 120 caracteres).");

            var estado = await _store.ResolverAsync(id, ct) ?? throw NaoEncontrado();
            var pendencias = await PendenciasAsync(estado, ct);
            await ValidarAssociacaoAsync(estado, para, dados, ct);

            await using var tx = await _store.AbrirAsync(estado.Id, ct);
            var repetida = await tx.PassagemPorChaveAsync(chave, ct);
            if (repetida != null)
            {
                if (repetida.Para != para)
                    throw new AcompanhamentoException(409, CodigosAcompanhamento.ChaveIdempotenciaReutilizada,
                        "Essa chave de idempotência já foi usada em outra passagem.",
                        new { repetida.De, repetida.Para, repetida.Id });
                return await ResultadoAsync(tx.Estado, repetida, true, ct);
            }

            var de = (dados.De ?? string.Empty).Trim().ToLowerInvariant();
            if (de.Length > 0 && de != tx.Estado.Fase)
                throw new AcompanhamentoException(409, CodigosAcompanhamento.FaseMudou,
                    $"A loja já está em {FasesAcompanhamento.Nome(tx.Estado.Fase)}. Atualize e tente de novo.",
                    new { esperada = de, atual = tx.Estado.Fase, versao = tx.Estado.Versao });

            var agora = _clock.GetUtcNow();
            var fatos = PassagemMotor.Fatos(tx.Estado, pendencias, await tx.CobrancaEmAbertoAsync(ct), await tx.PropostaVigenteAsync(ct));
            var avaliacao = PassagemMotor.Avaliar(tx.Estado.Id, tx.Estado.Versao, fatos, para, dados, agora);
            var recusa = PassagemMotor.Recusar(avaliacao);
            if (recusa != null) throw recusa;

            var aplicacao = PassagemMotor.Planejar(fatos, para, dados, chave, usuarioId, autorTipo, agora);
            var registro = await tx.AplicarAsync(aplicacao, ct);
            await tx.CommitAsync(ct);
            return await ResultadoAsync(tx.Estado, registro, false, ct);
        }

        // ---- propostas: cada escrita roda na trava do acompanhamento; o sistema nunca envia nem marca enviado sozinho

        private DateOnly Hoje => APIBack.Payments.Assinaturas.FusoComercial.Data(_clock.GetUtcNow());

        public async Task<PropostasDto> PropostasAsync(Guid id, CancellationToken ct = default)
        {
            var estado = await _store.ResolverAsync(id, ct) ?? throw NaoEncontrado();
            await using var tx = await _store.AbrirAsync(estado.Id, ct);
            var versoes = tx.Estado.OportunidadeId.HasValue ? await tx.PropostasAsync(ct) : Array.Empty<PropostaVersao>();
            await tx.CommitAsync(ct);

            var e = tx.Estado;
            var hoje = Hoje;
            var atual = PropostaRegras.Atual(versoes);
            return new PropostasDto(e.Id, e.OportunidadeId, e.Fase, e.OportunidadeId.HasValue && PropostaRegras.FaseAceita(e.Fase),
                atual == null ? null : PropostaRegras.Dto(atual, e, hoje),
                versoes.OrderByDescending(v => v.Versao).Select(v => PropostaRegras.Dto(v, e, hoje)).ToList());
        }

        public async Task<PropostaResultadoDto> CriarPropostaAsync(Guid id, PropostaRequest request, int? usuarioId, CancellationToken ct = default)
        {
            if (PropostaRegras.ValoresNegativos(request)) throw ValorInvalido();
            var estado = await _store.ResolverAsync(id, ct) ?? throw NaoEncontrado();
            await using var tx = await _store.AbrirAsync(estado.Id, ct);
            var e = tx.Estado;
            ExigirFaseDeProposta(e);

            var versoes = await tx.PropostasAsync(ct);
            // Ja ha rascunho aberto (duas abas, duplo clique): e ele que continua.
            var rascunho = versoes.FirstOrDefault(v => v.Status == StatusProposta.Rascunho);
            if (rascunho != null) return Resultado(e, rascunho, true, null);

            var anterior = request.RevisarVersaoId.HasValue
                ? versoes.FirstOrDefault(v => v.Id == request.RevisarVersaoId.Value) ?? throw PropostaNaoEncontrada()
                : versoes.OrderByDescending(v => v.Versao).FirstOrDefault();
            var nova = PropostaRegras.Nova(e.OportunidadeId!.Value, versoes.Select(v => v.Versao).DefaultIfEmpty(0).Max() + 1, anterior, e,
                await tx.ContextoPropostaAsync(ct), usuarioId);
            PropostaRegras.Sobrepor(nova, request);

            var salva = await tx.CriarPropostaAsync(nova, ct);
            await tx.EventoAsync("proposta_rascunho", new { salva.Versao, salva.RevisaVersao, salva.ValorMensal }, usuarioId, ct);
            await tx.CommitAsync(ct);
            return Resultado(tx.Estado, salva, false, null);
        }

        public async Task<PropostaResultadoDto> EditarPropostaAsync(Guid id, Guid versaoId, PropostaRequest request, int? usuarioId, CancellationToken ct = default)
        {
            if (PropostaRegras.ValoresNegativos(request)) throw ValorInvalido();
            var estado = await _store.ResolverAsync(id, ct) ?? throw NaoEncontrado();
            await using var tx = await _store.AbrirAsync(estado.Id, ct);
            var v = await ExigirVersaoAsync(tx, versaoId, ct);
            if (v.Status != StatusProposta.Rascunho) throw NaoEditavel(v);
            ChecarEdicao(v, request.VersaoEsperada, obrigatoria: true);
            ExigirFaseDeProposta(tx.Estado);

            PropostaRegras.Sobrepor(v, request);
            var salva = await tx.SalvarPropostaAsync(v, ct);
            await tx.CommitAsync(ct);
            return Resultado(tx.Estado, salva, false, null);
        }

        /// <summary>
        /// Registra o envio que uma pessoa declarou. Em Conversa a passagem para Proposta vai na mesma transacao; em Proposta a revisao
        /// substitui a anterior sem mudar a fase; em Teste a proposta nao mexe na fase.
        /// </summary>
        public async Task<PropostaResultadoDto> EnviarPropostaAsync(Guid id, Guid versaoId, EnviarPropostaRequest request, int? usuarioId, CancellationToken ct = default)
        {
            var chave = (request.ChaveIdempotencia ?? string.Empty).Trim();
            if (chave.Length is 0 or > 120)
                throw new AcompanhamentoException(422, CodigosAcompanhamento.ChaveIdempotenciaObrigatoria, "Informe a chave de idempotência (até 120 caracteres).");
            var canal = (request.Canal ?? string.Empty).Trim().ToLowerInvariant();
            if (Array.IndexOf(CanaisProposta.Registraveis, canal) < 0)
                throw new AcompanhamentoException(422, CodigosAcompanhamento.CanalInvalido, "Informe por onde a proposta foi enviada.", new { canais = CanaisProposta.Registraveis });
            var destinatario = (request.Destinatario ?? string.Empty).Trim();
            if (destinatario.Length is 0 or > 200)
                throw new AcompanhamentoException(422, CodigosAcompanhamento.DestinatarioObrigatorio, "Informe quem recebeu a proposta (até 200 caracteres).");
            var agora = _clock.GetUtcNow();
            var enviadaEm = request.EnviadaEm ?? agora;
            if (enviadaEm > agora.AddMinutes(5))
                throw new AcompanhamentoException(422, CodigosAcompanhamento.EnvioNoFuturo, "O envio não pode estar no futuro.");

            var estado = await _store.ResolverAsync(id, ct) ?? throw NaoEncontrado();
            var pendenciasChecklist = await PendenciasAsync(estado, ct);
            await using var tx = await _store.AbrirAsync(estado.Id, ct);
            var e = tx.Estado;
            if (!e.OportunidadeId.HasValue) throw SemOportunidade();

            var versoes = await tx.PropostasAsync(ct);
            var jaEnviada = versoes.FirstOrDefault(x => x.ChaveEnvio == chave);
            if (jaEnviada != null)
            {
                if (jaEnviada.Id != versaoId)
                    throw new AcompanhamentoException(409, CodigosAcompanhamento.ChaveIdempotenciaReutilizada, "Essa chave de idempotência já foi usada em outro envio.");
                return Resultado(e, jaEnviada, true, jaEnviada.PassagemId.HasValue ? await tx.PassagemPorChaveAsync(chave, ct) : null);
            }

            var v = versoes.FirstOrDefault(x => x.Id == versaoId) ?? throw PropostaNaoEncontrada();
            if (v.Status != StatusProposta.Rascunho) throw NaoEditavel(v);
            ChecarEdicao(v, request.VersaoEsperada, obrigatoria: true);
            ExigirFaseDeProposta(e);

            var faltam = PropostaRegras.Pendencias(v, e);
            if (faltam.Count > 0)
            {
                var motivo = "Falta: " + string.Join("; ", faltam.Select(p => p.Descricao)) + ".";
                throw new AcompanhamentoException(422, CodigosAcompanhamento.RequisitosPendentes, motivo,
                    new RecusaDetalhesDto(e.Fase, FasesAcompanhamento.Proposta, motivo, "Completar a proposta", new[] { e.Fase, FasesAcompanhamento.Proposta }, faltam));
            }
            if (request.MensagemId.HasValue && !await tx.MensagemConfereAsync(request.MensagemId.Value, ct))
                throw new AcompanhamentoException(422, CodigosAcompanhamento.EvidenciaInvalida, "Essa mensagem não é um envio real desta oportunidade.");

            var anterior = versoes.FirstOrDefault(x => StatusProposta.Vigente(x.Status));
            if (anterior != null)
            {
                anterior.Status = StatusProposta.Substituida;
                await tx.SalvarPropostaAsync(anterior, ct);
                v.RevisaVersao ??= anterior.Versao;
            }

            v.Status = StatusProposta.Enviada;
            v.Canal = canal;
            v.Destinatario = destinatario;
            v.ApresentadaPor = usuarioId;
            v.EvidenciaMensagemId = request.MensagemId;
            v.EnviadaEm = enviadaEm;
            v.ValidadeAte = PropostaRegras.Validade(enviadaEm);
            v.ChaveEnvio = chave;

            PassagemRegistroDto? passagem = null;
            if (e.Fase == FasesAcompanhamento.Conversa)
            {
                var fatos = PassagemMotor.Fatos(e, pendenciasChecklist, await tx.CobrancaEmAbertoAsync(ct), v) with { EnvioRegistrado = true };
                var dados = new DadosPassagem
                {
                    ChaveIdempotencia = chave, De = e.Fase, Motivo = $"Proposta v{v.Versao} enviada por {canal}" + (v.ValorMensal == 0 ? "; valor R$ 0 consciente" : string.Empty),
                    QuemDecide = v.Dados.QuemDecide, PedidosPorDia = v.Dados.PedidosPorDia, DorPrincipal = v.Dados.DorPrincipal,
                    ValorMensal = v.ValorMensal, ValorImplantacao = v.ValorImplantacao
                };
                var avaliacao = PassagemMotor.Avaliar(e.Id, e.Versao, fatos, FasesAcompanhamento.Proposta, dados, agora);
                if (PassagemMotor.Recusar(avaliacao) is { } recusa) throw recusa;
                passagem = await tx.AplicarAsync(PassagemMotor.Planejar(fatos, FasesAcompanhamento.Proposta, dados, chave, usuarioId, AutoresPassagem.Usuario, agora), ct);
                v.PassagemId = passagem.Id;
            }

            var salva = await tx.SalvarPropostaAsync(v, ct);
            await tx.EspelharPropostaAsync(salva, usuarioId, ct);
            await tx.EventoAsync("proposta_enviada", new
            {
                salva.Versao, salva.Canal, salva.Destinatario, salva.EnviadaEm, salva.ValidadeAte, mensagemId = salva.EvidenciaMensagemId, salva.ValorMensal,
                salva.ValorImplantacao, salva.ValorZeroConsciente, substituiu = anterior?.Versao, origem = OrigensProposta.Acompanhamento
            }, usuarioId, ct);
            await tx.CommitAsync(ct);
            return Resultado(tx.Estado, salva, false, passagem);
        }

        /// <summary>Resposta da loja. Nao muda a fase: depois de recusar, a saida e voltar a conversar, revisar ou perder.</summary>
        public async Task<PropostaResultadoDto> ResponderPropostaAsync(Guid id, Guid versaoId, ResponderPropostaRequest request, int? usuarioId, CancellationToken ct = default)
        {
            var resposta = (request.Resposta ?? string.Empty).Trim().ToLowerInvariant();
            if (Array.IndexOf(RespostasProposta.Todas, resposta) < 0)
                throw new AcompanhamentoException(422, CodigosAcompanhamento.RespostaInvalida, "Resposta inválida.", new { respostas = RespostasProposta.Todas });
            var nota = string.IsNullOrWhiteSpace(request.Nota) ? null : request.Nota.Trim();
            var chave = string.IsNullOrWhiteSpace(request.ChaveIdempotencia) ? null : request.ChaveIdempotencia.Trim();

            var estado = await _store.ResolverAsync(id, ct) ?? throw NaoEncontrado();
            await using var tx = await _store.AbrirAsync(estado.Id, ct);
            var v = await ExigirVersaoAsync(tx, versaoId, ct);

            // Mesma chave, ou a mesma resposta com a mesma nota: ja registrado.
            var mesmaChave = chave != null && v.ChaveResposta == chave;
            if (mesmaChave || (chave == null && v.Resposta == resposta && v.RespostaNota == nota))
            {
                if (mesmaChave && v.Resposta != resposta)
                    throw new AcompanhamentoException(409, CodigosAcompanhamento.ChaveIdempotenciaReutilizada, "Essa chave de idempotência já foi usada em outra resposta.");
                return Resultado(tx.Estado, v, true, null);
            }

            if (!StatusProposta.Vigente(v.Status))
                throw new AcompanhamentoException(422, CodigosAcompanhamento.PropostaNaoEditavel, "Só proposta enviada recebe resposta.", new { v.Status });
            ChecarEdicao(v, request.VersaoEsperada, obrigatoria: false);

            v.Resposta = resposta;
            v.RespostaNota = nota;
            v.RespostaEm = _clock.GetUtcNow();
            v.ChaveResposta = chave;
            if (resposta == RespostasProposta.PediuAjuste) v.Status = StatusProposta.EmNegociacao;
            else if (resposta == RespostasProposta.Recusou) v.Status = StatusProposta.Recusada;

            var salva = await tx.SalvarPropostaAsync(v, ct);
            await tx.EventoAsync("proposta_resposta", new { salva.Versao, resposta, nota, salva.Status }, usuarioId, ct);
            await tx.CommitAsync(ct);
            return Resultado(tx.Estado, salva, false, null);
        }

        private PropostaResultadoDto Resultado(AcompanhamentoEstado e, PropostaVersao v, bool repetida, PassagemRegistroDto? passagem) =>
            new(e.Id, e.Fase, e.Versao, repetida, PropostaRegras.Dto(v, e, Hoje), passagem);

        private static async Task<PropostaVersao> ExigirVersaoAsync(IAcompanhamentoTransacao tx, Guid versaoId, CancellationToken ct)
        {
            if (!tx.Estado.OportunidadeId.HasValue) throw SemOportunidade();
            return (await tx.PropostasAsync(ct)).FirstOrDefault(v => v.Id == versaoId) ?? throw PropostaNaoEncontrada();
        }

        private static void ExigirFaseDeProposta(AcompanhamentoEstado e)
        {
            if (!e.OportunidadeId.HasValue) throw SemOportunidade();
            if (!PropostaRegras.FaseAceita(e.Fase))
                throw new AcompanhamentoException(422, CodigosAcompanhamento.PropostaForaDaFase,
                    $"Em {FasesAcompanhamento.Nome(e.Fase)} não se registra proposta.", new { fases = new[] { FasesAcompanhamento.Conversa, FasesAcompanhamento.Proposta, FasesAcompanhamento.Teste } });
        }

        private static void ChecarEdicao(PropostaVersao v, long? esperada, bool obrigatoria)
        {
            if (esperada == null)
            {
                if (obrigatoria) throw new AcompanhamentoException(422, CodigosAcompanhamento.VersaoObrigatoria, "Informe a versão que você viu (versaoEsperada).");
                return;
            }
            if (esperada.Value != v.Edicao)
                throw new AcompanhamentoException(409, CodigosAcompanhamento.PropostaMudou, "Outra pessoa mexeu nesta proposta. Atualize e confira.", new { edicaoAtual = v.Edicao });
        }

        private static AcompanhamentoException SemOportunidade() =>
            new(422, CodigosAcompanhamento.PropostaSemOportunidade, "Não há oportunidade ligada a esta loja: crie uma oportunidade para registrar proposta.");

        private static AcompanhamentoException PropostaNaoEncontrada() =>
            new(404, CodigosAcompanhamento.PropostaNaoEncontrada, "Proposta não encontrada nesta loja.");

        private static AcompanhamentoException NaoEditavel(PropostaVersao v) =>
            new(409, CodigosAcompanhamento.PropostaNaoEditavel, "Só o rascunho muda: para alterar o que foi enviado, faça uma revisão.", new { v.Status, v.Versao });

        private static AcompanhamentoException ValorInvalido() =>
            new(422, CodigosAcompanhamento.PropostaValorInvalido, "Os valores da proposta não podem ser negativos.");

        private async Task<PassagemResultadoDto> ResultadoAsync(AcompanhamentoEstado estado, PassagemRegistroDto registro, bool repetida, CancellationToken ct)
        {
            var historico = await _store.HistoricoAsync(estado.Id, ct);
            return new PassagemResultadoDto(estado.Id, estado.OportunidadeId, estado.EstabelecimentoId, estado.Fase, estado.FaseDesde,
                estado.Versao, repetida, registro, historico);
        }

        /// <summary>O checklist e lido fora da transacao (como em "Comecar a operar"); sem loja nao ha checklist.</summary>
        private async Task<int?> PendenciasAsync(AcompanhamentoEstado estado, CancellationToken ct)
        {
            if (!estado.EstabelecimentoId.HasValue) return null;
            var prontidao = await _prontidao.ObterAsync(estado.EstabelecimentoId.Value, visaoGestao: true, ct);
            return prontidao?.PendenciasBloqueantes;
        }

        /// <summary>
        /// Fechamento que cria a loja: a mesma regra da Fase 0 (CrmAssociacaoRegras). Nunca liga por nome; CNPJ igual exige confirmar
        /// essa empresa; a loja confirmada tem de ser da empresa e ainda estar em implantacao (loja que ja opera e venda a mais).
        /// </summary>
        private async Task ValidarAssociacaoAsync(AcompanhamentoEstado estado, string para, DadosPassagem dados, CancellationToken ct)
        {
            var def = PassagensCatalogo.Obter(estado.Fase, para);
            if (def?.Efeito is not (EfeitoPassagem.FecharNaConversa or EfeitoPassagem.PropostaAceita or EfeitoPassagem.TesteSemCompromisso)
                || estado.EstabelecimentoId.HasValue || !estado.OportunidadeId.HasValue) return;

            var candidatos = await _crm.ListarCandidatosAssociacaoAsync(estado.OportunidadeId.Value, DocumentoRegras.Digitos(dados.Documento), null);
            var erro = CrmAssociacaoRegras.ValidarEscolha(
                candidatos.Select(c => new CrmCandidatoAssociacao(c.EmpresaId, c.Nome, c.Cnpj, c.Motivo)).ToList(), dados.EmpresaId);
            if (erro == null && dados.EstabelecimentoId.HasValue)
            {
                var loja = dados.EmpresaId.HasValue
                    ? candidatos.FirstOrDefault(c => c.EmpresaId == dados.EmpresaId.Value)?.Lojas.FirstOrDefault(l => l.Id == dados.EstabelecimentoId.Value)
                    : null;
                erro = loja == null
                    ? "A loja escolhida nao pertence a empresa confirmada."
                    : loja.Situacao is null or SituacoesComerciais.Implantacao
                        ? null
                        : "A loja escolhida ja passou da implantacao: use uma oportunidade de venda a mais.";
            }

            if (erro == null) return;
            var pendente = new RequisitoEstadoDto("empresa", "Empresa e loja confirmadas", false, erro);
            throw new AcompanhamentoException(422, CodigosAcompanhamento.RequisitosPendentes, erro,
                new RecusaDetalhesDto(estado.Fase, para, erro, def.Nome, new[] { estado.Fase, para }, new[] { pendente }));
        }

        private static string ValidarFase(string? para)
        {
            var fase = (para ?? string.Empty).Trim().ToLowerInvariant();
            if (!FasesAcompanhamento.Existe(fase))
                throw new AcompanhamentoException(422, CodigosAcompanhamento.FaseInvalida,
                    $"Fase desconhecida. Use: {string.Join(", ", FasesAcompanhamento.Todas)}.");
            return fase;
        }

        private static AcompanhamentoException NaoEncontrado() =>
            new(404, CodigosAcompanhamento.NaoEncontrado, "Loja ou oportunidade não encontrada.");
    }
}
