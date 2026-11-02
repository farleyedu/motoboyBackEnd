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
        Task<bool> TemPropostaAsync(CancellationToken ct);
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
            var fatos = PassagemMotor.Fatos(tx.Estado, pendencias, await tx.CobrancaEmAbertoAsync(ct), await tx.TemPropostaAsync(ct));
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
            var fatos = PassagemMotor.Fatos(tx.Estado, pendencias, await tx.CobrancaEmAbertoAsync(ct), await tx.TemPropostaAsync(ct));
            var avaliacao = PassagemMotor.Avaliar(tx.Estado.Id, tx.Estado.Versao, fatos, para, dados, agora);
            var recusa = PassagemMotor.Recusar(avaliacao);
            if (recusa != null) throw recusa;

            var aplicacao = PassagemMotor.Planejar(fatos, para, dados, chave, usuarioId, autorTipo, agora);
            var registro = await tx.AplicarAsync(aplicacao, ct);
            await tx.CommitAsync(ct);
            return await ResultadoAsync(tx.Estado, registro, false, ct);
        }

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
