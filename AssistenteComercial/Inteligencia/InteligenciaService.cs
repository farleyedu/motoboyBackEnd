using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.AssistenteComercial.Audio;
using APIBack.Service;
using Microsoft.Extensions.Options;

namespace APIBack.AssistenteComercial.Inteligencia
{
    /// <summary>Modulos do catalogo comercial real (pacotes e avulsos nao arquivados). A inteligencia nunca afirma capacidade fora disto.</summary>
    public interface ICatalogoComercial
    {
        Task<IReadOnlyCollection<string>> ModulosAtivosAsync(CancellationToken ct);
    }

    public sealed class CatalogoComercialPacotes : ICatalogoComercial
    {
        private readonly IPacotesComerciaisService _pacotes;
        public CatalogoComercialPacotes(IPacotesComerciaisService pacotes) => _pacotes = pacotes;

        public async Task<IReadOnlyCollection<string>> ModulosAtivosAsync(CancellationToken ct)
        {
            var pacotes = await _pacotes.ListarAsync(ct);
            var avulsos = await _pacotes.ListarAvulsosAsync(ct);
            return pacotes.Where(p => !p.Arquivado).SelectMany(p => p.Modulos)
                .Concat(avulsos.Where(a => !a.Arquivado).SelectMany(a => a.Modulos))
                .Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public interface IInteligenciaService
    {
        Task<ListaObjecoesDto> ListarObjecoesAsync(int usuarioId, bool superAdmin, Guid oportunidadeId, CancellationToken ct);
        Task<ObjecaoDto> RegistrarObjecaoAsync(int usuarioId, bool superAdmin, Guid oportunidadeId, RegistrarObjecaoRequest request, CancellationToken ct);
        Task<IReadOnlyList<RecomendacaoDto>> RecomendacoesAsync(int usuarioId, bool superAdmin, Guid oportunidadeId, CancellationToken ct);
        Task<RecomendacaoDto> FeedbackAsync(int usuarioId, bool superAdmin, Guid oportunidadeId, Guid recomendacaoId, FeedbackRequest request, CancellationToken ct);
        Task<CoachingDto> CoachingAsync(int usuarioId, bool superAdmin, int? responsavelUsuarioId, CancellationToken ct);
    }

    public sealed class InteligenciaService : IInteligenciaService
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private const int MaxDescricao = 500, MaxEvidencia = 1000, MaxAbordagem = 1000, MaxObs = 500;

        private readonly IInteligenciaStore _store;
        private readonly IAudioCrm _crm;
        private readonly IVerificacaoFuncionario _equipe;
        private readonly ICatalogoComercial _catalogo;
        private readonly IRedatorRecomendacao _redator;
        private readonly IOptionsMonitor<InteligenciaOptions> _opcoes;
        private readonly TimeProvider _relogio;

        public InteligenciaService(IInteligenciaStore store, IAudioCrm crm, IVerificacaoFuncionario equipe, ICatalogoComercial catalogo,
            IRedatorRecomendacao redator, IOptionsMonitor<InteligenciaOptions> opcoes, TimeProvider relogio)
        {
            _store = store; _crm = crm; _equipe = equipe; _catalogo = catalogo; _redator = redator; _opcoes = opcoes; _relogio = relogio;
        }

        private DateTime Agora => _relogio.GetUtcNow().UtcDateTime;
        private InteligenciaOptions Opcoes => _opcoes.CurrentValue;

        // ---- acesso: equipe do CRM; responsavel da oportunidade (super admin enxerga todas) ----

        private async Task GarantirEquipeAsync(int usuarioId, bool superAdmin, CancellationToken ct)
        {
            if (!superAdmin && !await _equipe.EquipeCrmAsync(usuarioId, ct))
                throw new InteligenciaException(403, CodigoInteligencia.SemAcesso, "Somente a equipe do CRM usa a inteligencia comercial.");
        }

        private async Task<OportunidadeCrm> OportunidadeAsync(int usuarioId, bool superAdmin, Guid id, CancellationToken ct)
        {
            await GarantirEquipeAsync(usuarioId, superAdmin, ct);
            var o = await _crm.ObterAsync(id, ct) ?? throw new InteligenciaException(404, CodigoInteligencia.NaoEncontrada, "Oportunidade nao encontrada.");
            if (!superAdmin && o.ResponsavelUsuarioId != usuarioId)
                throw new InteligenciaException(403, CodigoInteligencia.SemAcesso, "Oportunidade de outro responsavel.");
            return o;
        }

        // ---- objecoes ----

        public async Task<ListaObjecoesDto> ListarObjecoesAsync(int usuarioId, bool superAdmin, Guid oportunidadeId, CancellationToken ct)
        {
            await OportunidadeAsync(usuarioId, superAdmin, oportunidadeId, ct);
            var importadas = await ImportarDoAudioAsync(usuarioId, oportunidadeId, ct);
            var todas = await _store.ListarObjecoesAsync(oportunidadeId, ct);
            return new ListaObjecoesDto(todas.Select(Dto).ToList(), importadas);
        }

        /// <summary>
        /// Traz objecoes de audio que a pessoa JA confirmou (nota_historico aplicada) e cujo autor mantem o consentimento. So o fato declarado
        /// entra, com o trecho literal como evidencia; inferencias da IA nunca viram objecao. Idempotente por (entrada, evidencia).
        /// </summary>
        private async Task<int> ImportarDoAudioAsync(int ator, Guid oportunidadeId, CancellationToken ct)
        {
            var n = 0;
            foreach (var e in await _store.EntradasAudioConfirmadasAsync(oportunidadeId, ct))
            {
                if (!await _store.ConsentimentoAudioAsync(e.UsuarioId, ct)) continue; // opt-out: nada do audio dessa pessoa e usado
                PropostaVisita? proposta; List<ResultadoAlteracao>? resultados;
                try
                {
                    proposta = JsonSerializer.Deserialize<PropostaVisita>(e.PropostaJson!, Json);
                    resultados = JsonSerializer.Deserialize<List<ResultadoAlteracao>>(e.ResultadoJson!, Json);
                }
                catch (JsonException) { continue; }
                if (proposta == null || resultados == null) continue;

                var aplicadas = resultados.Where(r => r.Situacao == "aplicada").Select(r => r.Id).ToHashSet();
                var notas = proposta.Alteracoes.Where(a => a.Tipo == TipoAlteracao.NotaHistorico && aplicadas.Contains(a.Id)).ToList();

                foreach (var f in proposta.Fatos.Objecoes)
                {
                    if (string.IsNullOrWhiteSpace(f.Trecho) || string.IsNullOrWhiteSpace(f.Texto)) continue;
                    var confirmada = notas.Any(a => string.Equals(a.Trecho, f.Trecho, StringComparison.OrdinalIgnoreCase)
                                                    || a.Valor.Contains(f.Texto, StringComparison.OrdinalIgnoreCase));
                    if (!confirmada) continue;

                    var o = new Objecao
                    {
                        Id = Guid.NewGuid(), OportunidadeId = oportunidadeId, Tipo = TipoDeclarado(f.Texto), Descricao = Cortar(f.Texto, MaxDescricao),
                        Evidencia = Cortar(f.Trecho, MaxEvidencia), Origem = OrigemObjecao.Audio, EntradaAudioId = e.Id, Certeza = CertezaObjecao.Declarada,
                        Estado = EstadoObjecao.Levantada, Impacto = ImpactoObjecao.Desconhecido, OcorridaEm = e.AtualizadaEm, CriadoPor = e.UsuarioId,
                    };
                    if (!await _store.InserirObjecaoAsync(o, ct)) continue;
                    await _store.AuditarObjecaoAsync(o.Id, oportunidadeId, "importada_audio", ator, null, Snapshot(o), $"entrada {e.Id}", ct);
                    n++;
                }
            }
            return n;
        }

        /// <summary>Tipo so quando a palavra e inequivoca; do contrario "outro" (a pessoa classifica depois).</summary>
        private static string TipoDeclarado(string texto)
        {
            var t = RecomendacaoMotor.Normalizar(texto);
            if (t.Contains("caro") || t.Contains("preco") || t.Contains("mensalidade")) return TipoObjecao.Preco;
            if (t.Contains("concorrente")) return TipoObjecao.Concorrente;
            if (t.Contains("integra")) return TipoObjecao.Integracao;
            return TipoObjecao.Outro;
        }

        public async Task<ObjecaoDto> RegistrarObjecaoAsync(int usuarioId, bool superAdmin, Guid oportunidadeId, RegistrarObjecaoRequest r, CancellationToken ct)
        {
            await OportunidadeAsync(usuarioId, superAdmin, oportunidadeId, ct);
            if (r == null) throw Invalido("Corpo da requisicao ausente.");
            return r.Id.HasValue ? await CorrigirAsync(usuarioId, oportunidadeId, r, ct) : await CriarAsync(usuarioId, oportunidadeId, r, ct);
        }

        private async Task<ObjecaoDto> CriarAsync(int usuarioId, Guid oportunidadeId, RegistrarObjecaoRequest r, CancellationToken ct)
        {
            var o = new Objecao
            {
                Id = Guid.NewGuid(), OportunidadeId = oportunidadeId, Origem = OrigemObjecao.Manual, CriadoPor = usuarioId,
                Tipo = Escolha(r.Tipo, TipoObjecao.Todos, "tipo", obrigatorio: true)!,
                Descricao = Texto(r.Descricao, MaxDescricao, "descricao", obrigatorio: true)!,
                Evidencia = EvidenciaObrigatoria(r.Evidencia),
                Certeza = Escolha(r.Certeza, CertezaObjecao.Todos, "certeza") ?? CertezaObjecao.Declarada,
                Estado = Escolha(r.Estado, EstadoObjecao.Todos, "estado") ?? EstadoObjecao.Levantada,
                Impacto = Escolha(r.Impacto, ImpactoObjecao.Todos, "impacto") ?? ImpactoObjecao.Desconhecido,
                Abordagem = Texto(r.Abordagem, MaxAbordagem, "abordagem"),
                ModuloCitado = Texto(r.ModuloCitado, 120, "moduloCitado"),
                RespostaPrometidaAte = Utc(r.RespostaPrometidaAte), OcorridaEm = Utc(r.OcorridaEm) ?? Agora,
            };
            ValidarCerteza(o);
            await _store.InserirObjecaoAsync(o, ct);
            await _store.AuditarObjecaoAsync(o.Id, oportunidadeId, "criada", usuarioId, null, Snapshot(o), null, ct);
            return Dto((await _store.ObterObjecaoAsync(o.Id, ct)) ?? o);
        }

        private async Task<ObjecaoDto> CorrigirAsync(int usuarioId, Guid oportunidadeId, RegistrarObjecaoRequest r, CancellationToken ct)
        {
            var atual = await _store.ObterObjecaoAsync(r.Id!.Value, ct);
            if (atual == null || atual.OportunidadeId != oportunidadeId)
                throw new InteligenciaException(404, CodigoInteligencia.ObjecaoNaoEncontrada, "Objecao nao encontrada nesta oportunidade.");
            if (string.IsNullOrWhiteSpace(r.MotivoCorrecao))
                throw new InteligenciaException(422, CodigoInteligencia.MotivoAusente, "Informe o motivo da correcao (fica na auditoria).");
            if (r.VersaoEsperada is null)
                throw Invalido("Informe versaoEsperada para corrigir.");
            if (r.VersaoEsperada != atual.Versao)
                throw new InteligenciaException(409, CodigoInteligencia.Conflito, "A objecao mudou desde que voce a leu.", new { versaoAtual = atual.Versao });

            var novo = atual.Copia();
            if (r.Tipo != null) novo.Tipo = Escolha(r.Tipo, TipoObjecao.Todos, "tipo")!;
            if (r.Descricao != null) novo.Descricao = Texto(r.Descricao, MaxDescricao, "descricao", obrigatorio: true)!;
            if (r.Evidencia != null) novo.Evidencia = EvidenciaObrigatoria(r.Evidencia);
            if (r.Certeza != null) novo.Certeza = Escolha(r.Certeza, CertezaObjecao.Todos, "certeza")!;
            if (r.Estado != null) novo.Estado = Escolha(r.Estado, EstadoObjecao.Todos, "estado")!;
            if (r.Impacto != null) novo.Impacto = Escolha(r.Impacto, ImpactoObjecao.Todos, "impacto")!;
            if (r.Abordagem != null) novo.Abordagem = Texto(r.Abordagem, MaxAbordagem, "abordagem");
            if (r.ModuloCitado != null) novo.ModuloCitado = Texto(r.ModuloCitado, 120, "moduloCitado");
            if (r.RespostaPrometidaAte.HasValue) novo.RespostaPrometidaAte = Utc(r.RespostaPrometidaAte);
            if (r.OcorridaEm.HasValue) novo.OcorridaEm = Utc(r.OcorridaEm)!.Value;
            ValidarCerteza(novo);

            if (!await _store.AtualizarObjecaoAsync(novo, atual.Versao, ct))
                throw new InteligenciaException(409, CodigoInteligencia.Conflito, "A objecao mudou desde que voce a leu.");
            await _store.AuditarObjecaoAsync(atual.Id, oportunidadeId, "corrigida", usuarioId, Snapshot(atual), Snapshot(novo), r.MotivoCorrecao!.Trim(), ct);
            return Dto((await _store.ObterObjecaoAsync(atual.Id, ct)) ?? novo);
        }

        private static void ValidarCerteza(Objecao o)
        {
            if (o.Certeza == CertezaObjecao.Hipotese && o.Estado == EstadoObjecao.Impeditiva)
                throw new InteligenciaException(422, CodigoInteligencia.HipoteseComoFato, "Hipotese nao pode ser marcada como impeditiva: confirme com o cliente e mude a certeza para declarada.");
        }

        private static string EvidenciaObrigatoria(string? v)
        {
            if (string.IsNullOrWhiteSpace(v))
                throw new InteligenciaException(422, CodigoInteligencia.EvidenciaAusente, "Toda objecao precisa de evidencia: o que foi dito ou visto que a sustenta.");
            return Texto(v, MaxEvidencia, "evidencia", obrigatorio: true)!;
        }

        // ---- recomendacoes ----

        public async Task<IReadOnlyList<RecomendacaoDto>> RecomendacoesAsync(int usuarioId, bool superAdmin, Guid oportunidadeId, CancellationToken ct)
        {
            var opp = await OportunidadeAsync(usuarioId, superAdmin, oportunidadeId, ct);
            await ImportarDoAudioAsync(usuarioId, oportunidadeId, ct);
            var objecoes = await _store.ListarObjecoesAsync(oportunidadeId, ct);

            IReadOnlyCollection<string>? catalogo = null;
            try { catalogo = await _catalogo.ModulosAtivosAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { catalogo = null; }
            if (catalogo == null && objecoes.Any(o => !string.IsNullOrWhiteSpace(o.ModuloCitado) && o.Estado != EstadoObjecao.Resolvida))
                throw new InteligenciaException(503, CodigoInteligencia.Indisponivel, "Catalogo comercial indisponivel: nao e possivel afirmar o que o produto cobre agora.");

            var abertas = objecoes.Where(o => o.Estado is EstadoObjecao.Levantada or EstadoObjecao.Investigando).Select(o => o.Tipo).Distinct().ToArray();
            var historico = abertas.Length == 0 ? Array.Empty<ObjecaoHistorica>() : await _store.ObjecoesResolvidasComAbordagemAsync(abertas, oportunidadeId, 500, ct);

            var candidatas = RecomendacaoMotor.Gerar(objecoes, catalogo, historico, Opcoes, Agora);
            var existentes = await _store.ListarRecomendacoesAsync(oportunidadeId, ct);
            var conhecidas = existentes.Select(x => x.ImpressaoDigital).ToHashSet();
            var redigir = opp.ResponsavelUsuarioId is int resp && await _store.ConsentimentoAudioAsync(resp, ct); // opt-out: sem consentimento, sem IA

            foreach (var c in candidatas.Where(c => !conhecidas.Contains(c.ImpressaoDigital)))
            {
                var rec = new Recomendacao
                {
                    Id = Guid.NewGuid(), OportunidadeId = oportunidadeId, ResponsavelUsuarioId = opp.ResponsavelUsuarioId, Regra = c.Regra,
                    Prioridade = RegraRecomendacao.Prioridade(c.Regra), ImpressaoDigital = c.ImpressaoDigital, Sugestao = c.Sugestao, Hipotese = c.Hipotese,
                    Acao = c.Acao, EvidenciasJson = JsonSerializer.Serialize(c.Evidencias, Json), RedacaoIa = redigir ? await RedigirAsync(c, catalogo, ct) : null,
                };
                if (await _store.InserirRecomendacaoAsync(rec, ct))
                    await _store.RegistrarEventoAsync(rec.Id, "gerada", null, new { c.Regra, comRedacaoIa = rec.RedacaoIa != null }, ct);
            }

            var vigentes = candidatas.Select(c => c.ImpressaoDigital).ToHashSet();
            return (await _store.ListarRecomendacoesAsync(oportunidadeId, ct))
                .Where(x => x.Estado != EstadoRecomendacao.Sugerida || vigentes.Contains(x.ImpressaoDigital)) // sugestao que perdeu a base some; o que a pessoa decidiu fica
                .OrderBy(x => x.Prioridade).ThenByDescending(x => x.CriadoEm).Select(DtoRec).ToList();
        }

        private async Task<string?> RedigirAsync(RecomendacaoCandidata c, IReadOnlyCollection<string>? catalogo, CancellationToken ct)
        {
            var fatos = c.Evidencias.Select(e => new FatoParaRedacao($"{e.Tipo}:{e.Referencia}", e.Texto)).ToList();
            try
            {
                var r = await _redator.RedigirAsync(c, fatos, catalogo ?? Array.Empty<string>(), ct);
                return GuardaRedacao.Valida(r, fatos, catalogo ?? Array.Empty<string>(), out _) ? r!.Texto.Trim() : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { return null; } // IA fora do ar nao derruba a recomendacao deterministica
        }

        public async Task<RecomendacaoDto> FeedbackAsync(int usuarioId, bool superAdmin, Guid oportunidadeId, Guid recomendacaoId, FeedbackRequest f, CancellationToken ct)
        {
            await OportunidadeAsync(usuarioId, superAdmin, oportunidadeId, ct);
            if (f == null || string.IsNullOrWhiteSpace(f.Evento)) throw Invalido("Informe o evento do feedback.");
            var atual = await _store.ObterRecomendacaoAsync(recomendacaoId, ct);
            if (atual == null || atual.OportunidadeId != oportunidadeId)
                throw new InteligenciaException(404, CodigoInteligencia.RecomendacaoNaoEncontrada, "Recomendacao nao encontrada nesta oportunidade.");
            if (f.VersaoEsperada.HasValue && f.VersaoEsperada != atual.Versao)
                throw new InteligenciaException(409, CodigoInteligencia.Conflito, "A recomendacao mudou desde que voce a leu.", new { versaoAtual = atual.Versao });

            var n = atual.Copia();
            var obs = Texto(f.Observacao, MaxObs, "observacao");
            var evento = f.Evento.Trim().ToLowerInvariant();
            switch (evento)
            {
                case "aceita":
                    Exigir(atual.Estado == EstadoRecomendacao.Sugerida, atual, evento); n.Estado = EstadoRecomendacao.Aceita; break;
                case "rejeitada":
                    Exigir(atual.Estado == EstadoRecomendacao.Sugerida, atual, evento); n.Estado = EstadoRecomendacao.Rejeitada; n.MotivoRejeicao = Texto(f.Motivo, MaxObs, "motivo"); break;
                case "executada":
                    Exigir(atual.Estado == EstadoRecomendacao.Aceita, atual, evento); n.Estado = EstadoRecomendacao.Executada; break;
                case "reacao":
                    Exigir(atual.Estado == EstadoRecomendacao.Executada && atual.Reacao == null, atual, evento);
                    n.Reacao = Escolha(f.Reacao, ReacaoRecomendacao.Todas, "reacao", obrigatorio: true); break;
                case "resultado":
                    Exigir(atual.Estado == EstadoRecomendacao.Executada && atual.Resultado == null, atual, evento);
                    n.Resultado = Escolha(f.Resultado, ResultadoRecomendacao.Todos, "resultado", obrigatorio: true); break;
                default:
                    throw Invalido("Evento invalido. Use aceita, rejeitada, executada, reacao ou resultado.");
            }
            if (obs != null) n.Observacao = obs;

            if (!await _store.AtualizarRecomendacaoAsync(n, atual.Versao, ct))
                throw new InteligenciaException(409, CodigoInteligencia.Conflito, "A recomendacao mudou desde que voce a leu.");
            await _store.RegistrarEventoAsync(atual.Id, evento, usuarioId, new { de = atual.Estado, para = n.Estado, n.Reacao, n.Resultado, n.MotivoRejeicao }, ct);
            return DtoRec((await _store.ObterRecomendacaoAsync(atual.Id, ct)) ?? n);
        }

        private static void Exigir(bool ok, Recomendacao r, string evento)
        {
            if (!ok) throw new InteligenciaException(409, CodigoInteligencia.TransicaoInvalida, $"Evento '{evento}' nao se aplica a uma recomendacao '{r.Estado}'.", new { estado = r.Estado });
        }

        // ---- coaching ----

        public async Task<CoachingDto> CoachingAsync(int usuarioId, bool superAdmin, int? responsavelUsuarioId, CancellationToken ct)
        {
            await GarantirEquipeAsync(usuarioId, superAdmin, ct);
            var alvo = responsavelUsuarioId ?? usuarioId;
            if (alvo != usuarioId)
            {
                if (!superAdmin) throw new InteligenciaException(403, CodigoInteligencia.SemAcesso, "O coaching de outra pessoa e restrito a administracao.");
                if (!await _equipe.EquipeCrmAsync(alvo, ct)) throw new InteligenciaException(404, CodigoInteligencia.NaoEncontrada, "Responsavel nao pertence a equipe do CRM.");
            }
            var desde = Agora.AddDays(-Opcoes.JanelaCoachingDias);
            var objecoes = await _store.ObjecoesDoResponsavelAsync(alvo, desde, ct);
            var recs = await _store.RecomendacoesDoResponsavelAsync(alvo, desde, ct);
            return CoachingMotor.Montar(alvo, objecoes, recs, Opcoes, Agora);
        }

        // ---- util ----

        private static InteligenciaException Invalido(string msg) => new(422, CodigoInteligencia.Invalido, msg);

        private static string? Escolha(string? v, string[] validos, string campo, bool obrigatorio = false)
        {
            if (string.IsNullOrWhiteSpace(v)) return obrigatorio ? throw Invalido($"Campo '{campo}' obrigatorio.") : null;
            var x = v.Trim().ToLowerInvariant();
            return validos.Contains(x) ? x : throw new InteligenciaException(422, CodigoInteligencia.Invalido, $"Valor invalido para '{campo}'.", new { validos });
        }

        private static string? Texto(string? v, int max, string campo, bool obrigatorio = false)
        {
            if (string.IsNullOrWhiteSpace(v)) return obrigatorio ? throw Invalido($"Campo '{campo}' obrigatorio.") : null;
            var t = v.Trim();
            return t.Length <= max ? t : throw Invalido($"Campo '{campo}' passa de {max} caracteres.");
        }

        private static string Cortar(string s, int max) => s.Length <= max ? s : s[..max];
        private static DateTime? Utc(DateTime? d) => d.HasValue ? (d.Value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d.Value, DateTimeKind.Utc) : d.Value.ToUniversalTime()) : null;

        private static object Snapshot(Objecao o) => new
        {
            o.Tipo, o.Descricao, o.Evidencia, o.Origem, o.Certeza, o.Estado, o.Impacto, o.Abordagem, o.ModuloCitado, o.RespostaPrometidaAte, o.OcorridaEm,
        };

        private static ObjecaoDto Dto(Objecao o) => new(o.Id, o.OportunidadeId, o.Tipo, o.Descricao, o.Evidencia, o.Origem, o.EntradaAudioId, o.Certeza, o.Estado,
            o.Impacto, o.Abordagem, o.ModuloCitado, o.RespostaPrometidaAte, o.OcorridaEm, o.AtualizadoEm, o.Versao);

        private static RecomendacaoDto DtoRec(Recomendacao r)
        {
            var ev = JsonSerializer.Deserialize<List<EvidenciaDto>>(r.EvidenciasJson, Json) ?? new();
            var natureza = r.Resultado != null ? "resultado_registrado_sem_atribuicao_causal"
                : r.RedacaoIa != null ? "regra_com_redacao_ia_validada" : "regra_baseada_em_fatos_registrados";
            return new RecomendacaoDto(r.Id, r.OportunidadeId, r.Regra, r.Prioridade, r.Sugestao, r.Hipotese, r.Acao, ev, r.RedacaoIa, r.Estado,
                r.MotivoRejeicao, r.Reacao, r.Resultado, r.Observacao, r.CriadoEm, r.AtualizadoEm, r.Versao, natureza);
        }
    }
}
