using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using R = APIBack.Acompanhamento.Relacionamento.RelacionamentoRegras;

namespace APIBack.Acompanhamento.Relacionamento
{
    public sealed class RelacionamentoEstado
    {
        public Guid AcompanhamentoId { get; set; }
        public Guid? OportunidadeId { get; set; }
        public Guid? EstabelecimentoId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Fase { get; set; } = string.Empty;
        public long Versao { get; set; }
        public int? ResponsavelUsuarioId { get; set; }
        public MarcacaoDto? Marcacao { get; set; }
        public ProximoPassoDto? ProximoPasso { get; set; }
    }

    public sealed class LojaCarteiraRow
    {
        public Guid AcompanhamentoId { get; set; }
        public Guid? OportunidadeId { get; set; }
        public Guid? EstabelecimentoId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Fase { get; set; } = string.Empty;
        public int? ResponsavelUsuarioId { get; set; }
        public string? ResponsavelNome { get; set; }
        public long Versao { get; set; }
        public int? AnteriorUsuarioId { get; set; }
        public DateTimeOffset? VisivelAnteriorAte { get; set; }
        public Guid? ProximoPassoId { get; set; }
        public string? ProximoPassoTipo { get; set; }
        public DateOnly? ProximoPassoData { get; set; }
        public string? ProximoPassoNota { get; set; }
        public int? ProximoPassoCriadoPor { get; set; }
        public DateTimeOffset? ProximoPassoCriadoEm { get; set; }
        public DateTimeOffset? UltimaEnviada { get; set; }
        public DateTimeOffset? UltimaRecebida { get; set; }
        public string? Marcacao { get; set; }
    }

    /// <summary>Uma linha de acompanhamento_proximo_passo, aberta ou encerrada.</summary>
    public sealed class PassoRegistro
    {
        public Guid Id { get; set; }
        public string Tipo { get; set; } = string.Empty;
        public DateOnly Data { get; set; }
        public string? Nota { get; set; }
        public string Estado { get; set; } = EstadosPasso.Aberto;
        public string? Resultado { get; set; }
    }

    /// <summary>Encerramento de um passo: o estado final e o que a pessoa registrou.</summary>
    public sealed class PassoEncerramento
    {
        public string Estado { get; set; } = string.Empty;
        public string? Resultado { get; set; }
        public string? Nota { get; set; }
        public DateTimeOffset? RealizadoEm { get; set; }
        public string? Motivo { get; set; }
    }

    public sealed class RetencaoRegistro
    {
        public string Chave { get; set; } = string.Empty;
        public string Pedido { get; set; } = string.Empty;
        public DateTimeOffset PedidoEm { get; set; }
        public string Leitura { get; set; } = string.Empty;
        public string OfertaTipo { get; set; } = TiposOferta.Nenhuma;
        public string? OfertaDescricao { get; set; }
        public decimal? OfertaValor { get; set; }
        public bool OfertaAprovada { get; set; }
        public int? OfertaAprovadaPor { get; set; }
        public string Canal { get; set; } = string.Empty;
        public string Resultado { get; set; } = string.Empty;
        public string? SaidaQuando { get; set; }
        public DateOnly? SaidaEm { get; set; }
        public DateOnly? PausaAte { get; set; }
        public bool CobrancaEmAberto { get; set; }
        public string? Observacao { get; set; }
        public int CriadoPor { get; set; }
    }

    public interface IRelacionamentoStore
    {
        /// <summary>Id do acompanhamento a partir do id dele, da oportunidade ou da loja (cria o acompanhamento na primeira vez). Nulo = nada com esse id.</summary>
        Task<Guid?> ResolverAsync(Guid id, CancellationToken ct);
        /// <summary>Usuario da equipe Zippy (super admin ativo). Nulo = nao e da equipe.</summary>
        Task<UsuarioEquipeDto?> UsuarioDaEquipeAsync(int usuarioId, CancellationToken ct);
        Task<IReadOnlyList<UsuarioEquipeDto>> MembrosAsync(CancellationToken ct);
        Task<IReadOnlyList<LojaCarteiraRow>> CarteiraAsync(CancellationToken ct);
        Task<IReadOnlyList<AtividadeDto>> AtividadesAsync(string escopo, DateOnly hoje, int? responsavelUsuarioId, int limite, CancellationToken ct);
        /// <summary>Linhas da trilha, da mais nova para a mais antiga, depois do cursor. Paginacao por chave: nao depende da janela do quadro.</summary>
        Task<IReadOnlyList<JornadaLinha>> JornadaAsync(Guid acompanhamentoId, (DateTimeOffset Em, string Id)? depoisDe, int limite, CancellationToken ct);
        Task<IReadOnlyList<AvisoRelacionamentoDto>> AvisosAsync(int usuarioId, CancellationToken ct);
        Task<int> MarcarAvisosLidosAsync(int usuarioId, IReadOnlyCollection<long> ids, CancellationToken ct);
        Task<RelacionamentoDto?> LerAsync(Guid acompanhamentoId, int eventos, CancellationToken ct);
        /// <summary>Trava o relacionamento do acompanhamento (cria a linha se faltar) ate o Commit/Dispose.</summary>
        Task<IRelacionamentoTx> AbrirAsync(Guid acompanhamentoId, CancellationToken ct);
        /// <summary>Idempotente por (acompanhamento, direcao, referencia). Devolve false se ja estava registrado.</summary>
        Task<bool> RegistrarMarcoAsync(Guid acompanhamentoId, string direcao, DateTimeOffset em, string referencia, CancellationToken ct);
    }

    public interface IRelacionamentoTx : IAsyncDisposable
    {
        RelacionamentoEstado Estado { get; }
        Task DefinirResponsavelAsync(int novo, int? anterior, DateTimeOffset visivelAnteriorAte, CancellationToken ct);
        Task SubstituirProximoPassoAsync(string tipo, DateOnly data, string? nota, int usuarioId, CancellationToken ct);
        Task<PassoRegistro?> PassoAsync(Guid passoId, CancellationToken ct);
        /// <summary>O passo trocado por ultimo: desfazer remarca ele, mesmo com a data ja vencida.</summary>
        Task<PassoRegistro?> UltimoSubstituidoAsync(CancellationToken ct);
        Task EncerrarPassoAsync(Guid passoId, PassoEncerramento encerramento, int usuarioId, CancellationToken ct);
        Task<Guid> AbrirPassoAsync(string tipo, DateOnly data, string? nota, Guid? origemPassoId, int usuarioId, CancellationToken ct);
        Task AplicarMarcacaoAsync(string tipo, string motivo, string? categoria, string? acaoEsperada, int usuarioId, CancellationToken ct);
        Task RemoverMarcacaoAsync(int usuarioId, string motivo, CancellationToken ct);
        Task RegistrarCobrancaAsync(int cobrador, int responsavel, string? mensagem, CancellationToken ct);
        Task<DateTimeOffset?> UltimaCobrancaAsync(int cobrador, CancellationToken ct);
        Task<RetencaoDto?> RetencaoPorChaveAsync(string chave, CancellationToken ct);
        Task<RetencaoDto> RegistrarRetencaoAsync(RetencaoRegistro registro, CancellationToken ct);
        /// <summary>Quem esta Operando passa para Saindo na mesma transacao (passagem, data de saida e historico). Devolve a fase depois.</summary>
        Task<string> IniciarSaidaAsync(Guid retencaoId, RetencaoRegistro registro, int usuarioId, CancellationToken ct);
        Task<bool> CobrancaEmAbertoAsync(CancellationToken ct);
        Task AvisarAsync(int destinatario, string tipo, string mensagem, int? de, CancellationToken ct);
        Task EventoAsync(string tipo, object detalhe, int? usuarioId, CancellationToken ct);
        /// <summary>Confirma, incrementa a versao se algo mudou e devolve a versao atual.</summary>
        Task<long> ConfirmarAsync(CancellationToken ct);
    }

    public interface IRelacionamentoService
    {
        Task<EquipeDto> EquipeAsync(int usuarioId, CancellationToken ct = default);
        Task<RelacionamentoDto> ObterAsync(Guid id, CancellationToken ct = default);
        Task<PassarResultadoDto> PassarAsync(PassarRequest request, int usuarioId, CancellationToken ct = default);
        Task<RelacionamentoDto> AssumirAsync(Guid id, AssumirRequest? request, int usuarioId, CancellationToken ct = default);
        Task<RelacionamentoDto> CobrarResponsavelAsync(Guid id, CobrarResponsavelRequest? request, int usuarioId, CancellationToken ct = default);
        Task<RelacionamentoDto> DefinirProximoPassoAsync(Guid id, DefinirProximoPassoRequest request, int usuarioId, CancellationToken ct = default);
        Task<RelacionamentoDto> ConcluirPassoAsync(Guid id, Guid passoId, ConcluirPassoRequest request, int usuarioId, CancellationToken ct = default);
        Task<RelacionamentoDto> ReagendarPassoAsync(Guid id, Guid passoId, ReagendarPassoRequest request, int usuarioId, CancellationToken ct = default);
        Task<RelacionamentoDto> CancelarPassoAsync(Guid id, Guid passoId, CancelarPassoRequest request, int usuarioId, CancellationToken ct = default);
        Task<AtividadesDto> AtividadesAsync(string? escopo, int? responsavelUsuarioId, CancellationToken ct = default);
        Task<JornadaDto> JornadaAsync(Guid id, string? cursor, int? limite, CancellationToken ct = default);
        Task<RelacionamentoDto> MarcarAsync(Guid id, MarcarRequest request, int usuarioId, CancellationToken ct = default);
        Task<RelacionamentoDto> DesmarcarAsync(Guid id, string? motivo, int usuarioId, long? versaoEsperada = null, CancellationToken ct = default);
        Task<RetencaoDto> RegistrarCancelamentoAsync(Guid id, CancelamentoRequest request, int usuarioId, CancellationToken ct = default);
        Task<int> MarcarAvisosLidosAsync(IReadOnlyCollection<long> ids, int usuarioId, CancellationToken ct = default);
        /// <summary>Porta para o modulo de mensagens: so registra envio/recebimento que de fato aconteceu (referencia externa unica).</summary>
        Task<bool> RegistrarMarcoContatoAsync(Guid id, string direcao, DateTimeOffset em, string referencia, CancellationToken ct = default);
    }

    /// <summary>
    /// Responsavel, proximo passo, marcacao, cobranca ao responsavel, retencao e saida. Toda mudanca roda sob trava do relacionamento
    /// (FOR UPDATE), grava evento imutavel e incrementa a versao; o historico nunca e reescrito. Nao envia mensagem nem executa
    /// desconto/estorno: so registra o que um humano decidiu.
    /// </summary>
    public sealed class RelacionamentoService : IRelacionamentoService
    {
        private readonly IRelacionamentoStore _store;
        private readonly TimeProvider _clock;

        public RelacionamentoService(IRelacionamentoStore store, TimeProvider? clock = null)
        {
            _store = store;
            _clock = clock ?? TimeProvider.System;
        }

        private DateTimeOffset Agora => _clock.GetUtcNow();
        private DateOnly Hoje => R.Hoje(Agora);

        // ---- leitura

        public async Task<EquipeDto> EquipeAsync(int usuarioId, CancellationToken ct = default)
        {
            var membros = await _store.MembrosAsync(ct);
            var linhas = await _store.CarteiraAsync(ct);
            var hoje = Hoje;
            var agora = Agora;

            var lojas = linhas.Select(l =>
            {
                var passo = l.ProximoPassoId.HasValue && l.ProximoPassoData.HasValue
                    ? new ProximoPassoDto(l.ProximoPassoId.Value, l.ProximoPassoTipo ?? string.Empty, l.ProximoPassoData.Value, l.ProximoPassoNota, l.ProximoPassoCriadoPor,
                        l.ProximoPassoCriadoEm ?? default, l.ProximoPassoData.Value < hoje)
                    : null;
                var transitoria = l.AnteriorUsuarioId.HasValue && l.VisivelAnteriorAte > agora;
                return new LojaEquipeDto
                {
                    AcompanhamentoId = l.AcompanhamentoId, OportunidadeId = l.OportunidadeId, EstabelecimentoId = l.EstabelecimentoId,
                    Nome = l.Nome, Fase = l.Fase, ResponsavelUsuarioId = l.ResponsavelUsuarioId, ResponsavelNome = l.ResponsavelNome, Versao = l.Versao, ProximoPasso = passo,
                    AguardandoRespostaDesde = R.CalcularEspera(l.UltimaEnviada, l.UltimaRecebida).Desde, Marcacao = l.Marcacao,
                    VisaoTransitoriaUsuarioId = transitoria ? l.AnteriorUsuarioId : null, VisaoTransitoriaAte = transitoria ? l.VisivelAnteriorAte : null
                };
            }).ToList();

            return new EquipeDto
            {
                Membros = membros.Select(m =>
                {
                    var minhas = lojas.Where(l => l.ResponsavelUsuarioId == m.Id).ToList();
                    return new MembroEquipeDto(m.Id, m.Nome, minhas.Count,
                        minhas.Count(l => l.ProximoPasso is { Vencido: true }),
                        minhas.Count(l => l.ProximoPasso is { Vencido: false } p && p.Data == hoje),
                        minhas.Count(l => l.AguardandoRespostaDesde.HasValue));
                }).ToList(),
                SemResponsavel = lojas.Count(l => !l.ResponsavelUsuarioId.HasValue),
                Lojas = lojas,
                Lembretes = lojas.Where(l => l.ProximoPasso != null && l.ProximoPasso.Data <= hoje)
                    .OrderBy(l => l.ProximoPasso!.Data)
                    .Select(l => new LembreteProximoPassoDto(l.ProximoPasso!.Id, l.AcompanhamentoId, l.EstabelecimentoId, l.Nome, l.ProximoPasso.Tipo,
                        l.ProximoPasso.Data, l.ResponsavelUsuarioId, l.ProximoPasso.Vencido, l.ProximoPasso.Nota, l.ProximoPasso.CriadoEm,
                        l.ResponsavelUsuarioId is { } r
                            ? membros.FirstOrDefault(m => m.Id == r) ?? (l.ResponsavelNome == null ? null : new UsuarioEquipeDto(r, l.ResponsavelNome))
                            : null)).ToList(),
                Avisos = (await _store.AvisosAsync(usuarioId, ct)).ToList()
            };
        }

        public async Task<RelacionamentoDto> ObterAsync(Guid id, CancellationToken ct = default)
        {
            var acompanhamento = await ResolverAsync(id, ct);
            return await LerAsync(acompanhamento, ct);
        }

        // ---- responsavel

        public async Task<PassarResultadoDto> PassarAsync(PassarRequest request, int usuarioId, CancellationToken ct = default)
        {
            var ids = (request.LojaIds ?? new()).Distinct().ToList();
            if (ids.Count == 0) throw new RelacionamentoException(422, "LOTE_VAZIO", "Informe ao menos uma loja.");
            if (ids.Count > R.LoteMaximo) throw new RelacionamentoException(422, "LOTE_GRANDE", $"No maximo {R.LoteMaximo} lojas por vez.");
            var autor = await ExigirEquipeAsync(usuarioId, ct);
            var destino = await _store.UsuarioDaEquipeAsync(request.ParaUsuarioId, ct)
                ?? throw new RelacionamentoException(422, "DESTINATARIO_INVALIDO", "O responsavel precisa ser alguem da equipe Zippy.");
            var motivo = string.IsNullOrWhiteSpace(request.Motivo) ? null : request.Motivo.Trim();

            var resultado = new PassarResultadoDto();
            foreach (var lojaId in ids)
            {
                var item = new PassarItemDto { LojaId = lojaId };
                try
                {
                    long? esperada = request.VersoesEsperadas != null && request.VersoesEsperadas.TryGetValue(lojaId, out var v) ? v : null;
                    var (acompanhamento, mudou, versao) = await TransferirAsync(lojaId, destino, autor, esperada, motivo, "passou", ct);
                    item.AcompanhamentoId = acompanhamento;
                    item.Status = mudou ? "passada" : "sem_mudanca";
                    item.Versao = versao;
                    if (mudou) resultado.Passadas++;
                }
                catch (RelacionamentoException ex)
                {
                    item.Status = "erro";
                    item.Codigo = ex.Code;
                    item.Mensagem = ex.Message;
                }
                resultado.Itens.Add(item);
            }
            return resultado;
        }

        public async Task<RelacionamentoDto> AssumirAsync(Guid id, AssumirRequest? request, int usuarioId, CancellationToken ct = default)
        {
            var autor = await ExigirEquipeAsync(usuarioId, ct);
            var motivo = string.IsNullOrWhiteSpace(request?.Motivo) ? null : request!.Motivo!.Trim();
            var (acompanhamento, mudou, _) = await TransferirAsync(id, autor, autor, request?.VersaoEsperada, motivo, "assumiu", ct);
            var dto = await LerAsync(acompanhamento, ct);
            dto.Repetida = !mudou;
            return dto;
        }

        private async Task<(Guid Acompanhamento, bool Mudou, long Versao)> TransferirAsync(Guid id, UsuarioEquipeDto destino, UsuarioEquipeDto autor,
            long? versaoEsperada, string? motivo, string evento, CancellationToken ct)
        {
            var acompanhamento = await ResolverAsync(id, ct);
            await using var tx = await _store.AbrirAsync(acompanhamento, ct);
            var e = tx.Estado;
            ChecarVersao(e, versaoEsperada);
            if (e.ResponsavelUsuarioId == destino.Id) return (acompanhamento, false, e.Versao);

            var ate = Agora.AddDays(R.VisaoAnteriorDias);
            var antigo = e.ResponsavelUsuarioId;
            await tx.DefinirResponsavelAsync(destino.Id, antigo, ate, ct);
            await tx.EventoAsync(evento, new
            {
                de = antigo, para = destino.Id, motivo, visivelAte = antigo.HasValue ? ate : (DateTimeOffset?)null,
                proximoPassoId = e.ProximoPasso?.Id, proximoPasso = e.ProximoPasso == null ? null : new { e.ProximoPasso.Tipo, e.ProximoPasso.Data }
            }, autor.Id, ct);

            if (antigo.HasValue && antigo.Value != autor.Id)
                await tx.AvisarAsync(antigo.Value, "responsavel_trocou",
                    $"{autor.Nome} passou {e.Nome} para {destino.Nome}. Voce continua vendo a loja ate {R.Hoje(ate):dd/MM}.", autor.Id, ct);
            if (destino.Id != autor.Id)
                await tx.AvisarAsync(destino.Id, "recebeu_loja",
                    $"{autor.Nome} passou {e.Nome} para voce." + (e.ProximoPasso == null ? " Ainda sem proximo passo." : $" Proximo passo: {e.ProximoPasso.Tipo} em {e.ProximoPasso.Data:dd/MM}."), autor.Id, ct);

            var versao = await tx.ConfirmarAsync(ct);
            return (acompanhamento, true, versao);
        }

        public async Task<RelacionamentoDto> CobrarResponsavelAsync(Guid id, CobrarResponsavelRequest? request, int usuarioId, CancellationToken ct = default)
        {
            var autor = await ExigirEquipeAsync(usuarioId, ct);
            var mensagem = string.IsNullOrWhiteSpace(request?.Mensagem) ? null : request!.Mensagem!.Trim();
            if (mensagem is { Length: > 500 }) throw new RelacionamentoException(422, "MENSAGEM_LONGA", "A mensagem pode ter ate 500 caracteres.");

            var acompanhamento = await ResolverAsync(id, ct);
            await using (var tx = await _store.AbrirAsync(acompanhamento, ct))
            {
                var e = tx.Estado;
                if (!e.ResponsavelUsuarioId.HasValue)
                    throw new RelacionamentoException(422, "SEM_RESPONSAVEL", "Esta loja nao tem responsavel para cobrar: use assumir ou passar.");
                if (e.ResponsavelUsuarioId == autor.Id)
                    throw new RelacionamentoException(422, "PROPRIO_RESPONSAVEL", "Voce e o responsavel desta loja.");
                var ultima = await tx.UltimaCobrancaAsync(autor.Id, ct);
                if (ultima.HasValue && Agora - ultima.Value < TimeSpan.FromHours(R.CobrancaIntervaloHoras))
                    throw new RelacionamentoException(409, "COBRANCA_RECENTE", "Voce ja cobrou o responsavel desta loja nas ultimas 24 horas.",
                        new { proximaEm = ultima.Value.AddHours(R.CobrancaIntervaloHoras) });

                var responsavel = e.ResponsavelUsuarioId.Value;
                await tx.RegistrarCobrancaAsync(autor.Id, responsavel, mensagem, ct);
                // O nome fica: a tela da Equipe confirma a cobranca por ele.
                await tx.EventoAsync("cobrou_responsavel", new { responsavel, mensagem }, autor.Id, ct);
                await tx.AvisarAsync(responsavel, "cobranca",
                    $"{autor.Nome} pediu atencao em {e.Nome}" + (mensagem == null ? "." : $": {mensagem}"), autor.Id, ct);
                await tx.ConfirmarAsync(ct);
            }
            return await LerAsync(acompanhamento, ct);
        }

        // ---- proximo passo

        public async Task<RelacionamentoDto> DefinirProximoPassoAsync(Guid id, DefinirProximoPassoRequest request, int usuarioId, CancellationToken ct = default)
        {
            var hoje = Hoje;
            R.ValidarProximoPasso(request.Tipo, request.Data, DateOnly.MinValue); // a data passada e conferida na trava: desfazer pode remarcar o vencido
            var tipo = request.Tipo!.Trim().ToLowerInvariant();
            var data = request.Data!.Value;
            var nota = string.IsNullOrWhiteSpace(request.Nota) ? null : request.Nota.Trim();
            var autor = await ExigirEquipeAsync(usuarioId, ct);

            var acompanhamento = await ResolverAsync(id, ct);
            bool repetida;
            await using (var tx = await _store.AbrirAsync(acompanhamento, ct))
            {
                var e = tx.Estado;
                ChecarVersao(e, request.VersaoEsperada);
                var atual = e.ProximoPasso;
                // Mesmo tipo e data com nota nova e mudanca: a nota nao se perde. Sem nota no pedido, nada muda.
                repetida = atual != null && atual.Tipo == tipo && atual.Data == data && (nota == null || nota == atual.Nota);
                if (!repetida)
                {
                    if (data < hoje)
                    {
                        // Data passada so para desfazer: e o passo que acabou de ser trocado, e a nota dele volta junto.
                        var trocado = await tx.UltimoSubstituidoAsync(ct);
                        if (trocado == null || trocado.Tipo != tipo || trocado.Data != data)
                            throw new RelacionamentoException(422, "PROXIMO_PASSO_NO_PASSADO", "A data do proximo passo nao pode estar no passado.");
                        nota ??= trocado.Nota;
                    }
                    await tx.SubstituirProximoPassoAsync(tipo, data, nota, autor.Id, ct);
                    await tx.EventoAsync("proximo_passo", new { tipo, data, nota, anterior = atual == null ? null : new { atual.Tipo, atual.Data } }, autor.Id, ct);
                    await tx.ConfirmarAsync(ct);
                }
            }
            var dto = await LerAsync(acompanhamento, ct);
            dto.Repetida = repetida;
            return dto;
        }

        /// <summary>
        /// Encerra o passo com o resultado que a pessoa registrou e, se ela disse o que vem depois, abre o seguinte na mesma transacao.
        /// Repetir com o mesmo resultado devolve repetida, sem gravar de novo.
        /// </summary>
        public async Task<RelacionamentoDto> ConcluirPassoAsync(Guid id, Guid passoId, ConcluirPassoRequest request, int usuarioId, CancellationToken ct = default)
        {
            var resultado = request.Resultado?.Trim().ToLowerInvariant();
            if (resultado == null || !ResultadosPasso.Todos.Contains(resultado))
                throw new RelacionamentoException(422, "RESULTADO_INVALIDO", "Informe o resultado da atividade.", new { resultados = ResultadosPasso.Todos });
            var realizadoEm = request.RealizadoEm ?? Agora;
            if (realizadoEm > Agora.AddMinutes(5)) throw new RelacionamentoException(422, "REALIZADO_NO_FUTURO", "A atividade nao pode ter sido feita no futuro.");
            if (request.Proximo != null) R.ValidarProximoPasso(request.Proximo.Tipo, request.Proximo.Data, Hoje);
            var nota = string.IsNullOrWhiteSpace(request.Nota) ? null : request.Nota.Trim();
            var autor = await ExigirEquipeAsync(usuarioId, ct);

            var acompanhamento = await ResolverAsync(id, ct);
            bool repetida;
            await using (var tx = await _store.AbrirAsync(acompanhamento, ct))
            {
                var passo = await ExigirPassoAsync(tx, passoId, ct);
                repetida = passo.Estado != EstadosPasso.Aberto && passo.Resultado == resultado;
                if (!repetida)
                {
                    ExigirAberto(passo);
                    ChecarVersao(tx.Estado, request.VersaoEsperada);
                    await tx.EncerrarPassoAsync(passoId, new PassoEncerramento
                    {
                        Estado = ResultadosPasso.Estado(resultado), Resultado = resultado, Nota = nota, RealizadoEm = realizadoEm
                    }, autor.Id, ct);

                    Guid? seguinte = null;
                    if (request.Proximo != null)
                    {
                        var tipo = request.Proximo.Tipo!.Trim().ToLowerInvariant();
                        var proximaNota = string.IsNullOrWhiteSpace(request.Proximo.Nota) ? null : request.Proximo.Nota.Trim();
                        seguinte = await tx.AbrirPassoAsync(tipo, request.Proximo.Data!.Value, proximaNota, passoId, autor.Id, ct);
                        await tx.EventoAsync("proximo_passo", new { passoId = seguinte, tipo, data = request.Proximo.Data, nota = proximaNota, origemPassoId = passoId }, autor.Id, ct);
                    }
                    await tx.EventoAsync("atividade_concluida", new
                    {
                        passoId, passo.Tipo, passo.Data, resultado, nota, realizadoEm, estado = ResultadosPasso.Estado(resultado), proximoPassoId = seguinte
                    }, autor.Id, ct);
                    await tx.ConfirmarAsync(ct);
                }
            }
            var dto = await LerAsync(acompanhamento, ct);
            dto.Repetida = repetida;
            return dto;
        }

        public async Task<RelacionamentoDto> ReagendarPassoAsync(Guid id, Guid passoId, ReagendarPassoRequest request, int usuarioId, CancellationToken ct = default)
        {
            if (!request.Data.HasValue) throw new RelacionamentoException(422, "PROXIMO_PASSO_OBRIGATORIO", "Informe a nova data.");
            var data = request.Data.Value;
            if (data < Hoje) throw new RelacionamentoException(422, "PROXIMO_PASSO_NO_PASSADO", "A data do proximo passo nao pode estar no passado.");
            var motivo = string.IsNullOrWhiteSpace(request.Motivo) ? null : request.Motivo.Trim();
            var autor = await ExigirEquipeAsync(usuarioId, ct);

            var acompanhamento = await ResolverAsync(id, ct);
            bool repetida;
            await using (var tx = await _store.AbrirAsync(acompanhamento, ct))
            {
                var passo = await ExigirPassoAsync(tx, passoId, ct);
                repetida = (passo.Estado == EstadosPasso.Aberto && passo.Data == data)
                    || (passo.Estado == EstadosPasso.Reagendado && tx.Estado.ProximoPasso?.Data == data);
                if (!repetida)
                {
                    ExigirAberto(passo);
                    ChecarVersao(tx.Estado, request.VersaoEsperada);
                    await tx.EncerrarPassoAsync(passoId, new PassoEncerramento { Estado = EstadosPasso.Reagendado, Motivo = motivo }, autor.Id, ct);
                    var novo = await tx.AbrirPassoAsync(passo.Tipo, data, passo.Nota, passoId, autor.Id, ct);
                    await tx.EventoAsync("atividade_reagendada", new { passoId, novoPassoId = novo, passo.Tipo, de = passo.Data, para = data, motivo }, autor.Id, ct);
                    await tx.ConfirmarAsync(ct);
                }
            }
            var dto = await LerAsync(acompanhamento, ct);
            dto.Repetida = repetida;
            return dto;
        }

        public async Task<RelacionamentoDto> CancelarPassoAsync(Guid id, Guid passoId, CancelarPassoRequest request, int usuarioId, CancellationToken ct = default)
        {
            R.ExigirMotivo(request.Motivo);
            var motivo = request.Motivo!.Trim();
            var autor = await ExigirEquipeAsync(usuarioId, ct);

            var acompanhamento = await ResolverAsync(id, ct);
            bool repetida;
            await using (var tx = await _store.AbrirAsync(acompanhamento, ct))
            {
                var passo = await ExigirPassoAsync(tx, passoId, ct);
                repetida = passo.Estado == EstadosPasso.Cancelado;
                if (!repetida)
                {
                    ExigirAberto(passo);
                    ChecarVersao(tx.Estado, request.VersaoEsperada);
                    await tx.EncerrarPassoAsync(passoId, new PassoEncerramento { Estado = EstadosPasso.Cancelado, Motivo = motivo }, autor.Id, ct);
                    await tx.EventoAsync("atividade_cancelada", new { passoId, passo.Tipo, passo.Data, motivo }, autor.Id, ct);
                    await tx.ConfirmarAsync(ct);
                }
            }
            var dto = await LerAsync(acompanhamento, ct);
            dto.Repetida = repetida;
            return dto;
        }

        private static async Task<PassoRegistro> ExigirPassoAsync(IRelacionamentoTx tx, Guid passoId, CancellationToken ct) =>
            await tx.PassoAsync(passoId, ct) ?? throw new RelacionamentoException(404, "PASSO_NAO_ENCONTRADO", "Atividade nao encontrada nesta loja.");

        private static void ExigirAberto(PassoRegistro passo)
        {
            if (passo.Estado != EstadosPasso.Aberto)
                throw new RelacionamentoException(409, "PASSO_JA_ENCERRADO", "Esta atividade ja foi encerrada. Recarregue antes de continuar.", new { passo.Estado, passo.Resultado });
        }

        // ---- leitura: fila e jornada

        public async Task<AtividadesDto> AtividadesAsync(string? escopo, int? responsavelUsuarioId, CancellationToken ct = default)
        {
            var e = (escopo ?? EscoposAtividade.Hoje).Trim().ToLowerInvariant();
            if (!EscoposAtividade.Todos.Contains(e))
                throw new RelacionamentoException(422, "ESCOPO_INVALIDO", "Escopo invalido.", new { escopos = EscoposAtividade.Todos });
            var hoje = Hoje;
            return new AtividadesDto(e, hoje, await _store.AtividadesAsync(e, hoje, responsavelUsuarioId, JornadaRegras.LimiteMaximo, ct));
        }

        public async Task<JornadaDto> JornadaAsync(Guid id, string? cursor, int? limite, CancellationToken ct = default)
        {
            var depoisDe = JornadaRegras.LerCursor(cursor);
            var tamanho = JornadaRegras.Limite(limite);
            var acompanhamento = await ResolverAsync(id, ct);
            // Um a mais: diz se ha proxima pagina sem contar a trilha inteira.
            var linhas = await _store.JornadaAsync(acompanhamento, depoisDe, tamanho + 1, ct);
            var pagina = linhas.Take(tamanho).ToList();
            return new JornadaDto(acompanhamento, pagina.Select(JornadaRegras.Item).ToList(),
                linhas.Count > tamanho ? JornadaRegras.Cursor(pagina[^1]) : null);
        }

        // ---- marcacao

        public async Task<RelacionamentoDto> MarcarAsync(Guid id, MarcarRequest request, int usuarioId, CancellationToken ct = default)
        {
            var tipo = request.Tipo?.Trim().ToLowerInvariant();
            if (tipo == null || !TiposMarcacao.Todos.Contains(tipo))
                throw new RelacionamentoException(422, "MARCACAO_TIPO_INVALIDO", "Tipo de marcacao invalido.", new { tipos = TiposMarcacao.Todos });
            R.ExigirMotivo(request.Motivo);
            var motivo = request.Motivo!.Trim();
            var categoria = string.IsNullOrWhiteSpace(request.Categoria) ? null : request.Categoria.Trim();
            var acaoEsperada = string.IsNullOrWhiteSpace(request.AcaoEsperada) ? null : request.AcaoEsperada.Trim();
            if (categoria is { Length: > R.CategoriaMaxima }) throw new RelacionamentoException(422, "CATEGORIA_LONGA", $"A categoria pode ter ate {R.CategoriaMaxima} caracteres.");
            if (acaoEsperada is { Length: > R.AcaoEsperadaMaxima }) throw new RelacionamentoException(422, "ACAO_ESPERADA_LONGA", $"A acao esperada pode ter ate {R.AcaoEsperadaMaxima} caracteres.");
            var autor = await ExigirEquipeAsync(usuarioId, ct);

            var acompanhamento = await ResolverAsync(id, ct);
            await using (var tx = await _store.AbrirAsync(acompanhamento, ct))
            {
                var e = tx.Estado;
                ChecarVersao(e, request.VersaoEsperada);
                if (e.Marcacao?.Tipo == tipo)
                    throw new RelacionamentoException(409, "MARCACAO_JA_ATIVA", "Esta loja ja tem essa marcacao.");
                await tx.AplicarMarcacaoAsync(tipo, motivo, categoria, acaoEsperada, autor.Id, ct);
                await tx.EventoAsync("marcada", new { tipo, motivo, categoria, acaoEsperada, substituiu = e.Marcacao?.Tipo }, autor.Id, ct);
                await tx.ConfirmarAsync(ct);
            }
            return await LerAsync(acompanhamento, ct);
        }

        public async Task<RelacionamentoDto> DesmarcarAsync(Guid id, string? motivo, int usuarioId, long? versaoEsperada = null, CancellationToken ct = default)
        {
            R.ExigirMotivo(motivo);
            var autor = await ExigirEquipeAsync(usuarioId, ct);
            var acompanhamento = await ResolverAsync(id, ct);
            await using (var tx = await _store.AbrirAsync(acompanhamento, ct))
            {
                var e = tx.Estado;
                ChecarVersao(e, versaoEsperada);
                var atual = e.Marcacao ?? throw new RelacionamentoException(404, "SEM_MARCACAO", "Esta loja nao tem marcacao ativa.");
                // Quem marcou e o responsavel (ou, sem responsavel, qualquer um da equipe) podem tirar; os demais pedem para eles.
                var pode = atual.PorUsuarioId == autor.Id || e.ResponsavelUsuarioId == autor.Id || !e.ResponsavelUsuarioId.HasValue;
                if (!pode)
                    throw new RelacionamentoException(403, "SEM_PERMISSAO", "So quem marcou ou o responsavel da loja pode tirar a marcacao.");
                await tx.RemoverMarcacaoAsync(autor.Id, motivo!.Trim(), ct);
                await tx.EventoAsync("desmarcada", new { atual.Tipo, marcadaPor = atual.PorUsuarioId, resolucao = motivo.Trim(), motivo = motivo.Trim() }, autor.Id, ct);
                await tx.ConfirmarAsync(ct);
            }
            return await LerAsync(acompanhamento, ct);
        }

        // ---- retencao e saida

        public async Task<RetencaoDto> RegistrarCancelamentoAsync(Guid id, CancelamentoRequest request, int usuarioId, CancellationToken ct = default)
        {
            var hoje = Hoje;
            var registro = ValidarCancelamento(request, usuarioId, Agora, hoje);
            var autor = await ExigirEquipeAsync(usuarioId, ct);
            if (registro.OfertaAprovada) registro.OfertaAprovadaPor = autor.Id;

            var acompanhamento = await ResolverAsync(id, ct);
            await using var tx = await _store.AbrirAsync(acompanhamento, ct);

            var existente = await tx.RetencaoPorChaveAsync(registro.Chave, ct);
            if (existente != null)
            {
                existente.Repetida = true;
                existente.FaseAtual = tx.Estado.Fase;
                return existente;
            }
            ChecarVersao(tx.Estado, request.VersaoEsperada);

            registro.CobrancaEmAberto = await tx.CobrancaEmAbertoAsync(ct);
            if (registro.Resultado == ResultadosRetencao.Pausada && registro.CobrancaEmAberto)
                throw new RelacionamentoException(422, "PAUSA_COM_COBRANCA_EM_ABERTO", "Com cobranca em aberto a loja nao entra em pausa: resolva a cobranca antes.");

            var salvo = await tx.RegistrarRetencaoAsync(registro, ct);
            salvo.FaseAtual = registro.Resultado == ResultadosRetencao.Saiu
                ? await tx.IniciarSaidaAsync(salvo.Id, registro, autor.Id, ct)
                : tx.Estado.Fase;
            await tx.EventoAsync(registro.Resultado == ResultadosRetencao.Saiu ? "saida_registrada" : "retencao_registrada", new
            {
                retencaoId = salvo.Id, registro.Resultado, registro.Canal, oferta = registro.OfertaTipo, aprovada = registro.OfertaAprovada,
                registro.SaidaQuando, registro.SaidaEm, registro.PausaAte, registro.CobrancaEmAberto
            }, autor.Id, ct);

            var responsavel = tx.Estado.ResponsavelUsuarioId;
            if (responsavel.HasValue && responsavel.Value != autor.Id)
                await tx.AvisarAsync(responsavel.Value, "retencao",
                    $"{autor.Nome} registrou {(registro.Resultado == ResultadosRetencao.Saiu ? "a saida" : "uma retencao")} de {tx.Estado.Nome} ({registro.Resultado}).", autor.Id, ct);

            await tx.ConfirmarAsync(ct);
            return salvo;
        }

        /// <summary>Validacao pura (sem banco) do registro de cancelamento: oferta aprovada, pausa dentro da politica, data de saida coerente.</summary>
        public static RetencaoRegistro ValidarCancelamento(CancelamentoRequest r, int usuarioId, DateTimeOffset agora, DateOnly hoje)
        {
            var chave = r.ChaveIdempotencia?.Trim();
            if (string.IsNullOrEmpty(chave)) throw new RelacionamentoException(422, "CHAVE_OBRIGATORIA", "Envie a chave de idempotencia (Idempotency-Key).");
            if (chave.Length > 120) throw new RelacionamentoException(422, "CHAVE_LONGA", "A chave de idempotencia pode ter ate 120 caracteres.");
            if (string.IsNullOrWhiteSpace(r.Pedido)) throw new RelacionamentoException(422, "PEDIDO_OBRIGATORIO", "Registre o que a loja pediu.");
            if (string.IsNullOrWhiteSpace(r.Leitura)) throw new RelacionamentoException(422, "LEITURA_OBRIGATORIA", "Registre sua leitura do motivo.");
            var canal = r.Canal?.Trim().ToLowerInvariant();
            if (canal == null || !CanaisRetencao.Todos.Contains(canal))
                throw new RelacionamentoException(422, "CANAL_INVALIDO", "Canal invalido.", new { canais = CanaisRetencao.Todos });
            var resultado = r.Resultado?.Trim().ToLowerInvariant();
            if (resultado == null || !ResultadosRetencao.Todos.Contains(resultado))
                throw new RelacionamentoException(422, "RESULTADO_INVALIDO", "Resultado invalido.", new { resultados = ResultadosRetencao.Todos });
            var pedidoEm = r.PedidoEm ?? agora;
            if (pedidoEm > agora.AddMinutes(5)) throw new RelacionamentoException(422, "PEDIDO_NO_FUTURO", "O pedido nao pode estar no futuro.");

            var oferta = r.Oferta ?? new OfertaRetencaoRequest { Tipo = TiposOferta.Nenhuma };
            var ofertaTipo = (oferta.Tipo ?? TiposOferta.Nenhuma).Trim().ToLowerInvariant();
            if (!TiposOferta.Todos.Contains(ofertaTipo))
                throw new RelacionamentoException(422, "OFERTA_INVALIDA", "Tipo de oferta invalido.", new { tipos = TiposOferta.Todos });
            if (TiposOferta.ExigeAprovacao(ofertaTipo))
            {
                if (!oferta.Aprovada)
                    throw new RelacionamentoException(422, "OFERTA_NAO_APROVADA", "Desconto e estorno so entram com aprovacao humana. O sistema nao executa nenhum dos dois.");
                if (oferta.Valor is not > 0)
                    throw new RelacionamentoException(422, "OFERTA_SEM_VALOR", "Informe o valor aprovado da oferta.");
            }
            if (oferta.Valor is < 0) throw new RelacionamentoException(422, "OFERTA_VALOR_INVALIDO", "O valor da oferta nao pode ser negativo.");

            var pedidoDia = R.Hoje(pedidoEm);
            string? saidaQuando = null;
            DateOnly? saidaEm = null, pausaAte = null;
            if (resultado == ResultadosRetencao.Saiu)
            {
                saidaQuando = r.SaidaQuando?.Trim().ToLowerInvariant();
                saidaEm = R.CalcularSaida(saidaQuando, pedidoDia, r.SaidaEm, hoje);
            }
            else if (resultado == ResultadosRetencao.Pausada)
            {
                R.ValidarPausa(r.PausaAte, hoje);
                pausaAte = r.PausaAte;
            }

            return new RetencaoRegistro
            {
                Chave = chave, Pedido = r.Pedido.Trim(), PedidoEm = pedidoEm, Leitura = r.Leitura.Trim(), OfertaTipo = ofertaTipo,
                OfertaDescricao = string.IsNullOrWhiteSpace(oferta.Descricao) ? null : oferta.Descricao.Trim(), OfertaValor = oferta.Valor,
                OfertaAprovada = oferta.Aprovada, Canal = canal, Resultado = resultado, SaidaQuando = saidaQuando, SaidaEm = saidaEm, PausaAte = pausaAte,
                Observacao = string.IsNullOrWhiteSpace(r.Observacao) ? null : r.Observacao.Trim(), CriadoPor = usuarioId
            };
        }

        // ---- avisos e contato

        public Task<int> MarcarAvisosLidosAsync(IReadOnlyCollection<long> ids, int usuarioId, CancellationToken ct = default) =>
            ids.Count == 0 ? Task.FromResult(0) : _store.MarcarAvisosLidosAsync(usuarioId, ids, ct);

        public async Task<bool> RegistrarMarcoContatoAsync(Guid id, string direcao, DateTimeOffset em, string referencia, CancellationToken ct = default)
        {
            if (direcao is not (DirecoesContato.Enviada or DirecoesContato.Recebida))
                throw new RelacionamentoException(422, "DIRECAO_INVALIDA", "Direcao deve ser enviada ou recebida.");
            if (string.IsNullOrWhiteSpace(referencia))
                throw new RelacionamentoException(422, "REFERENCIA_OBRIGATORIA", "Sem a referencia da mensagem real nao ha como registrar envio ou recebimento.");
            if (em > Agora.AddMinutes(5)) throw new RelacionamentoException(422, "CONTATO_NO_FUTURO", "O contato nao pode estar no futuro.");
            return await _store.RegistrarMarcoAsync(await ResolverAsync(id, ct), direcao, em, referencia.Trim(), ct);
        }

        // ---- apoio

        private async Task<Guid> ResolverAsync(Guid id, CancellationToken ct) =>
            await _store.ResolverAsync(id, ct) ?? throw new RelacionamentoException(404, "NAO_ENCONTRADO", "Loja ou lead nao encontrado.");

        private async Task<RelacionamentoDto> LerAsync(Guid acompanhamento, CancellationToken ct) =>
            await _store.LerAsync(acompanhamento, 50, ct) ?? throw new RelacionamentoException(404, "NAO_ENCONTRADO", "Loja ou lead nao encontrado.");

        private async Task<UsuarioEquipeDto> ExigirEquipeAsync(int usuarioId, CancellationToken ct) =>
            await _store.UsuarioDaEquipeAsync(usuarioId, ct) ?? throw new RelacionamentoException(403, "SEM_PERMISSAO", "So a equipe Zippy faz isso.");

        private static void ChecarVersao(RelacionamentoEstado estado, long? esperada)
        {
            if (esperada.HasValue && esperada.Value != estado.Versao)
                throw new RelacionamentoException(409, "VERSAO_MUDOU", "Outra pessoa mexeu nesta loja. Recarregue antes de continuar.", new { versaoAtual = estado.Versao });
        }
    }
}
