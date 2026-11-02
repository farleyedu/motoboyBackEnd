using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace APIBack.Acompanhamento.Incidentes
{
    /// <summary>Porta para o servico de mensagens (prompt 08). Sem implementacao, a mensagem fica preparada e pendente: nunca ha falso sucesso.</summary>
    public interface IIncidenteMensageria
    {
        bool Disponivel { get; }
        Task<bool> EnviarAsync(Guid lojaId, string contato, string mensagem, CancellationToken ct);
    }

    public interface IIncidenteService
    {
        Task<IncidentesDto> ListarAsync(Guid? lojaId, CancellationToken ct = default);
        Task<FalhaDto> RegistrarAsync(RegistrarFalhaRequest request, int usuarioId, CancellationToken ct = default);
        Task<IReadOnlyList<AvisoDto>> AvisosAsync(int usuarioId, bool somenteNaoLidos, CancellationToken ct = default);
        Task<int> MarcarLidosAsync(IReadOnlyCollection<long> ids, int usuarioId, CancellationToken ct = default);
        Task<AvisarResultadoDto> AvisarAsync(Guid incidenteId, AvisarRequest request, int usuarioId, CancellationToken ct = default);
        Task<IncidenteDto> NormalizouAsync(Guid incidenteId, NormalizouRequest request, int usuarioId, CancellationToken ct = default);
    }

    /// <summary>
    /// Falhas por loja, incidentes de 3+ lojas e avisos. Detecta sozinho so o que tem evidencia (iFood: falhas de polling; canal Meta: canal em erro).
    /// As demais categorias aparecem como sem_monitoramento com a fonte que falta; so entram por registro manual com prova.
    /// </summary>
    public sealed class IncidenteService : IIncidenteService
    {
        private readonly NpgsqlDataSource _db;
        private readonly TimeProvider _clock;
        private readonly IIncidenteMensageria? _mensageria;

        public IncidenteService(NpgsqlDataSource db, TimeProvider? clock = null, IIncidenteMensageria? mensageria = null)
        {
            _db = db;
            _clock = clock ?? TimeProvider.System;
            _mensageria = mensageria;
        }

        // ---- classificacao (pura)

        private static readonly Regex Numeros = new(@"[0-9a-f]{8}-[0-9a-f-]{27}|\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Assinatura estavel: mesma falha em lojas diferentes tem o mesmo texto, sem ids nem numeros.</summary>
        public static string Assinatura(string categoria, string? codigo)
        {
            var texto = Numeros.Replace((codigo ?? "").Trim().ToLowerInvariant(), "#");
            texto = Regex.Replace(texto, @"\s+", " ");
            if (texto.Length > 80) texto = texto[..80];
            return $"{categoria}:{texto}";
        }

        /// <summary>Quem provavelmente causou. Sem pista clara, indeterminado: nao se culpa ninguem por palpite.</summary>
        public static string ClassificarExecutor(string? erro)
        {
            var t = (erro ?? "").ToLowerInvariant();
            if (Regex.IsMatch(t, "token|expired|expirad|revoked|revogad|unauthor|permission|permiss|invalid_grant")) return ExecutoresIncidente.Loja;
            if (Regex.IsMatch(t, "unavailable|bad gateway|\\b50[234]\\b|service_unavailable")) return ExecutoresIncidente.Terceiro;
            return ExecutoresIncidente.Indeterminado;
        }

        // ---- deteccao (so com evidencia)

        private sealed record Deteccao(string Categoria, bool Ok, IReadOnlyList<SinalFalha> Sinais, string? Indisponivel);

        private async Task<Deteccao> DetectarIfoodAsync(NpgsqlConnection cx, CancellationToken ct)
        {
            try
            {
                var linhas = await cx.QueryAsync<(Guid LojaId, string? Codigo, int Falhas, DateTime Inicio)>(new CommandDefinition(@"
SELECT estabelecimento_id, poll_error_code, poll_failures, COALESCE(last_poll_ok_at_utc, authorized_at_utc, created_at_utc)
  FROM ifood_connections
 WHERE status = 'authorized' AND poll_failures > 0 AND poll_error_code IS NOT NULL;", cancellationToken: ct));
                var sinais = linhas.Select(l => new SinalFalha(CategoriasIncidente.Ifood, "ifood_connections.poll_error_code",
                    Assinatura(CategoriasIncidente.Ifood, l.Codigo), l.LojaId, new DateTimeOffset(DateTime.SpecifyKind(l.Inicio, DateTimeKind.Utc)),
                    $"Consulta de eventos do iFood falhando ({l.Codigo})", ClassificarExecutor(l.Codigo),
                    JsonSerializer.Serialize(new { origem = "ifood_connections", codigo = l.Codigo, falhas_seguidas = l.Falhas }))).ToList();
                return new Deteccao(CategoriasIncidente.Ifood, true, sinais, null);
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
            {
                return new Deteccao(CategoriasIncidente.Ifood, false, Array.Empty<SinalFalha>(), "tabela/coluna do iFood ausente neste banco");
            }
        }

        private async Task<Deteccao> DetectarCanalMetaAsync(NpgsqlConnection cx, CancellationToken ct)
        {
            try
            {
                var linhas = await cx.QueryAsync<(Guid LojaId, string? Erro, DateTime Em)>(new CommandDefinition(@"
SELECT id_estabelecimento, ultimo_erro, ultimo_erro_em
  FROM canal_whatsapp
 WHERE status = 'erro' AND ultimo_erro_em IS NOT NULL;", cancellationToken: ct));
                var sinais = linhas.Select(l => new SinalFalha(CategoriasIncidente.CanalMeta, "canal_whatsapp.ultimo_erro",
                    Assinatura(CategoriasIncidente.CanalMeta, l.Erro), l.LojaId, new DateTimeOffset(DateTime.SpecifyKind(l.Em, DateTimeKind.Utc)),
                    $"Canal WhatsApp em erro ({Resumo(l.Erro)})", ClassificarExecutor(l.Erro),
                    JsonSerializer.Serialize(new { origem = "canal_whatsapp", erro = Resumo(l.Erro) }))).ToList();
                return new Deteccao(CategoriasIncidente.CanalMeta, true, sinais, null);
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
            {
                return new Deteccao(CategoriasIncidente.CanalMeta, false, Array.Empty<SinalFalha>(), "tabela canal_whatsapp ausente neste banco");
            }
        }

        private static string Resumo(string? s) => string.IsNullOrWhiteSpace(s) ? "sem detalhe" : (s.Length > 120 ? s[..120] : s);

        private static string Chave(string origem, SinalFalha s) => $"{origem}:{s.Assinatura}:{s.LojaId}:{s.Inicio.ToUniversalTime():yyyyMMddTHHmmssZ}";

        // ---- sincronizacao + agregacao (idempotente)

        private async Task SincronizarAsync(CancellationToken ct)
        {
            await using var leitura = await _db.OpenConnectionAsync(ct);
            var deteccoes = new[] { await DetectarIfoodAsync(leitura, ct), await DetectarCanalMetaAsync(leitura, ct) };
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            await TravaAsync(cx, tx, ct);
            var agora = _clock.GetUtcNow();
            foreach (var d in deteccoes.Where(d => d.Ok))
            {
                var chaves = new List<string>();
                foreach (var s in d.Sinais)
                {
                    var chave = Chave("auto", s);
                    chaves.Add(chave);
                    var id = Guid.NewGuid();
                    var criada = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(@"
INSERT INTO acompanhamento_falha (id, categoria, fonte, assinatura, loja_id, inicio, causa_provavel, executor, prova, origem, chave)
VALUES (@Id, @Cat, @Fonte, @Ass, @Loja, @Inicio, @Causa, @Exec, @Prova::jsonb, 'automatica', @Chave)
ON CONFLICT (chave) DO NOTHING RETURNING id;",
                        new { Id = id, Cat = s.Categoria, Fonte = s.Fonte, Ass = s.Assinatura, Loja = s.LojaId, Inicio = s.Inicio, Causa = s.CausaProvavel, Exec = s.Executor, Prova = s.ProvaJson, Chave = chave },
                        tx, cancellationToken: ct));
                    if (criada != null) await EventoAsync(cx, tx, null, id, s.LojaId, "falha_detectada", new { s.Categoria, s.Assinatura }, null, ct);
                }
                // A evidencia sumiu: a falha automatica termina sozinha (o incidente agregado so termina por confirmacao humana).
                var fechadas = await cx.QueryAsync<(Guid Id, Guid LojaId)>(new CommandDefinition(@"
UPDATE acompanhamento_falha SET fim = @Agora, estado = 'normalizada'
 WHERE categoria = @Cat AND origem = 'automatica' AND estado = 'aberta' AND NOT (chave = ANY(@Chaves))
RETURNING id, loja_id;", new { Agora = agora, Cat = d.Categoria, Chaves = chaves.ToArray() }, tx, cancellationToken: ct));
                foreach (var f in fechadas) await EventoAsync(cx, tx, null, f.Id, f.LojaId, "falha_encerrada_sem_sinal", new { d.Categoria }, null, ct);
            }
            await AgregarAsync(cx, tx, ct);
            await tx.CommitAsync(ct);
        }

        private static Task TravaAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CancellationToken ct) =>
            cx.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext('acompanhamento_incidentes'));", transaction: tx, cancellationToken: ct));

        private static Task EventoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid? incidente, Guid? falha, Guid? loja, string tipo, object detalhe, int? usuario, CancellationToken ct) =>
            cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO acompanhamento_incidente_evento (incidente_id, falha_id, loja_id, tipo, detalhe, usuario_id)
VALUES (@Inc, @Falha, @Loja, @Tipo, @Det::jsonb, @Usr);",
                new { Inc = incidente, Falha = falha, Loja = loja, Tipo = tipo, Det = JsonSerializer.Serialize(detalhe), Usr = usuario }, tx, cancellationToken: ct));

        private async Task AgregarAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CancellationToken ct)
        {
            var abertas = (await cx.QueryAsync<FalhaAberta>(new CommandDefinition(@"
SELECT id AS Id, categoria AS Categoria, assinatura AS Assinatura, loja_id AS LojaId, inicio AS Inicio, causa_provavel AS CausaProvavel, executor AS Executor
  FROM acompanhamento_falha WHERE estado = 'aberta';", transaction: tx, cancellationToken: ct))).ToList();
            foreach (var g in IncidenteAgregador.Agrupar(abertas))
            {
                var existente = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                    "SELECT id FROM acompanhamento_incidente WHERE assinatura = @A AND estado = 'aberto';", new { A = g.Assinatura }, tx, cancellationToken: ct));
                var incidenteId = existente ?? Guid.NewGuid();
                if (existente == null)
                {
                    await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO acompanhamento_incidente (id, categoria, assinatura, inicio, causa_provavel, executor, lojas_afetadas)
VALUES (@Id, @Cat, @Ass, @Inicio, @Causa, @Exec, @N);",
                        new { Id = incidenteId, Cat = g.Categoria, Ass = g.Assinatura, Inicio = g.Inicio, Causa = g.CausaProvavel, Exec = g.Executor, N = g.LojaIds.Count }, tx, cancellationToken: ct));
                    await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO acompanhamento_aviso (incidente_id, tipo, texto) VALUES (@Id, 'incidente_aberto', @Texto) ON CONFLICT DO NOTHING;",
                        new { Id = incidenteId, Texto = $"{g.LojaIds.Count} lojas com a mesma falha ({g.Categoria}): {g.CausaProvavel}. Revise e avise as lojas." }, tx, cancellationToken: ct));
                    await EventoAsync(cx, tx, incidenteId, null, null, "incidente_aberto", new { g.Categoria, g.Assinatura, Lojas = g.LojaIds.Count }, null, ct);
                }
                await cx.ExecuteAsync(new CommandDefinition(
                    "UPDATE acompanhamento_falha SET incidente_id = @Inc WHERE id = ANY(@Ids) AND incidente_id IS NULL;",
                    new { Inc = incidenteId, Ids = g.FalhaIds.ToArray() }, tx, cancellationToken: ct));
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE acompanhamento_incidente SET lojas_afetadas = (SELECT COUNT(DISTINCT loja_id) FROM acompanhamento_falha WHERE incidente_id = @Inc) WHERE id = @Inc;",
                    new { Inc = incidenteId }, tx, cancellationToken: ct));
            }
        }

        // ---- consulta

        private sealed class FalhaRow
        {
            public Guid Id { get; set; }
            public string Categoria { get; set; } = "";
            public string Fonte { get; set; } = "";
            public Guid LojaId { get; set; }
            public string? LojaNome { get; set; }
            public DateTime Inicio { get; set; }
            public DateTime? Fim { get; set; }
            public string CausaProvavel { get; set; } = "";
            public string Executor { get; set; } = "";
            public string Prova { get; set; } = "{}";
            public string Estado { get; set; } = "";
            public string Origem { get; set; } = "";
            public Guid? IncidenteId { get; set; }
            public FalhaDto ToDto() => new(Id, Categoria, Fonte, LojaId, LojaNome, Utc(Inicio), Fim == null ? null : Utc(Fim.Value), CausaProvavel, Executor, Prova, Estado, Origem, IncidenteId);
        }

        private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));

        private sealed class IncidenteRow
        {
            public Guid Id { get; set; }
            public string Categoria { get; set; } = "";
            public string Estado { get; set; } = "";
            public DateTime Inicio { get; set; }
            public DateTime? Fim { get; set; }
            public string CausaProvavel { get; set; } = "";
            public string Executor { get; set; } = "";
            public int LojasAfetadas { get; set; }
            public int EnviosPendentes { get; set; }
        }

        private const string SelectFalha = @"
SELECT f.id AS Id, f.categoria AS Categoria, f.fonte AS Fonte, f.loja_id AS LojaId, e.nome_fantasia AS LojaNome, f.inicio AS Inicio, f.fim AS Fim,
       f.causa_provavel AS CausaProvavel, f.executor AS Executor, f.prova::text AS Prova, f.estado AS Estado, f.origem AS Origem, f.incidente_id AS IncidenteId
  FROM acompanhamento_falha f LEFT JOIN estabelecimentos e ON e.id = f.loja_id";

        public async Task<IncidentesDto> ListarAsync(Guid? lojaId, CancellationToken ct = default)
        {
            await SincronizarAsync(ct);
            await using var cx = await _db.OpenConnectionAsync(ct);
            var falhas = (await cx.QueryAsync<FalhaRow>(new CommandDefinition(
                SelectFalha + @" WHERE (@Loja::uuid IS NULL OR f.loja_id = @Loja) ORDER BY f.inicio DESC LIMIT 1000;", new { Loja = lojaId }, cancellationToken: ct))).Select(r => r.ToDto()).ToList();
            var incidentes = (await cx.QueryAsync<IncidenteRow>(new CommandDefinition(@"
SELECT i.id AS Id, i.categoria AS Categoria, i.estado AS Estado, i.inicio AS Inicio, i.fim AS Fim, i.causa_provavel AS CausaProvavel, i.executor AS Executor,
       i.lojas_afetadas AS LojasAfetadas,
       (SELECT COUNT(*)::int FROM acompanhamento_incidente_envio v WHERE v.incidente_id = i.id AND v.estado = 'pendente_servico') AS EnviosPendentes
  FROM acompanhamento_incidente i
 WHERE (@Loja::uuid IS NULL OR EXISTS (SELECT 1 FROM acompanhamento_falha f WHERE f.incidente_id = i.id AND f.loja_id = @Loja))
 ORDER BY (i.estado = 'aberto') DESC, i.inicio DESC LIMIT 200;", new { Loja = lojaId }, cancellationToken: ct))).ToList();

            // Falhas de um incidente aparecem so dentro dele (uma vez); quando filtra por loja, o incidente traz todas as lojas para nao esconder o agregado.
            var porIncidente = new Dictionary<Guid, List<FalhaDto>>();
            foreach (var i in incidentes)
            {
                var lojas = (await cx.QueryAsync<FalhaRow>(new CommandDefinition(SelectFalha + " WHERE f.incidente_id = @I ORDER BY e.nome_fantasia, f.inicio;", new { I = i.Id }, cancellationToken: ct)))
                    .Select(r => r.ToDto()).ToList();
                porIncidente[i.Id] = lojas;
            }
            var dtos = incidentes.Select(i => new IncidenteDto(i.Id, i.Categoria, i.Estado, Utc(i.Inicio), i.Fim == null ? null : Utc(i.Fim.Value), i.CausaProvavel, i.Executor,
                i.LojasAfetadas, porIncidente[i.Id], i.EnviosPendentes)).ToList();
            var isoladas = falhas.Where(f => f.IncidenteId == null).ToList();
            return new IncidentesDto(_clock.GetUtcNow(), dtos, isoladas, await CoberturaAsync(cx, ct));
        }

        private async Task<IReadOnlyList<CoberturaDto>> CoberturaAsync(NpgsqlConnection cx, CancellationToken ct)
        {
            var ifood = await DetectarIfoodAsync(cx, ct);
            var meta = await DetectarCanalMetaAsync(cx, ct);
            return CategoriasIncidente.FonteFaltante.Select(kv =>
            {
                var d = kv.Key == CategoriasIncidente.Ifood ? ifood : kv.Key == CategoriasIncidente.CanalMeta ? meta : null;
                if (d != null) return d.Ok ? new CoberturaDto(kv.Key, "monitorado", null) : new CoberturaDto(kv.Key, "sem_monitoramento", d.Indisponivel);
                return new CoberturaDto(kv.Key, "sem_monitoramento", kv.Value);
            }).ToList();
        }

        // ---- registro manual

        public async Task<FalhaDto> RegistrarAsync(RegistrarFalhaRequest request, int usuarioId, CancellationToken ct = default)
        {
            var erros = new List<string>();
            if (!CategoriasIncidente.Existe(request.Categoria)) erros.Add("categoria invalida");
            if (request.LojaId == null || request.LojaId == Guid.Empty) erros.Add("lojaId obrigatorio");
            if (string.IsNullOrWhiteSpace(request.Fonte)) erros.Add("fonte obrigatoria");
            if (string.IsNullOrWhiteSpace(request.CausaProvavel)) erros.Add("causaProvavel obrigatoria");
            if (string.IsNullOrWhiteSpace(request.Prova)) erros.Add("prova obrigatoria: registro manual sem evidencia nao vira falha");
            var executor = string.IsNullOrWhiteSpace(request.Executor) ? ExecutoresIncidente.Indeterminado : request.Executor.Trim().ToLowerInvariant();
            if (!ExecutoresIncidente.Todos.Contains(executor)) erros.Add("executor deve ser zippy, loja, terceiro ou indeterminado");
            var agora = _clock.GetUtcNow();
            var inicio = request.Inicio ?? agora;
            if (inicio > agora.AddMinutes(5)) erros.Add("inicio no futuro");
            if (erros.Count > 0) throw new IncidenteException(422, "FALHA_INVALIDA", "Falha invalida.", erros);

            var s = new SinalFalha(request.Categoria!, request.Fonte!.Trim(), Assinatura(request.Categoria!, string.IsNullOrWhiteSpace(request.Assinatura) ? request.CausaProvavel : request.Assinatura),
                request.LojaId!.Value, inicio, request.CausaProvavel!.Trim(), executor, JsonSerializer.Serialize(new { origem = "manual", texto = request.Prova!.Trim() }));
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            await TravaAsync(cx, tx, ct);
            var existeLoja = await cx.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS (SELECT 1 FROM estabelecimentos WHERE id = @L);", new { L = s.LojaId }, tx, cancellationToken: ct));
            if (!existeLoja) throw new IncidenteException(404, "LOJA_INEXISTENTE", "Loja nao encontrada.");
            var id = Guid.NewGuid();
            var criada = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(@"
INSERT INTO acompanhamento_falha (id, categoria, fonte, assinatura, loja_id, inicio, causa_provavel, executor, prova, origem, chave, criado_por)
VALUES (@Id, @Cat, @Fonte, @Ass, @Loja, @Inicio, @Causa, @Exec, @Prova::jsonb, 'manual', @Chave, @Usr)
ON CONFLICT (chave) DO NOTHING RETURNING id;",
                new { Id = id, Cat = s.Categoria, Fonte = s.Fonte, Ass = s.Assinatura, Loja = s.LojaId, Inicio = s.Inicio, Causa = s.CausaProvavel, Exec = s.Executor, Prova = s.ProvaJson, Chave = Chave("manual", s), Usr = usuarioId },
                tx, cancellationToken: ct));
            if (criada == null) throw new IncidenteException(409, "FALHA_REPETIDA", "Esta falha ja foi registrada para a loja.");
            await EventoAsync(cx, tx, null, id, s.LojaId, "falha_registrada", new { s.Categoria, s.Assinatura, s.Executor }, usuarioId, ct);
            await AgregarAsync(cx, tx, ct);
            var row = await cx.QuerySingleAsync<FalhaRow>(new CommandDefinition(SelectFalha + " WHERE f.id = @Id;", new { Id = id }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return row.ToDto();
        }

        // ---- avisos

        public async Task<IReadOnlyList<AvisoDto>> AvisosAsync(int usuarioId, bool somenteNaoLidos, CancellationToken ct = default)
        {
            await SincronizarAsync(ct);
            await using var cx = await _db.OpenConnectionAsync(ct);
            var linhas = await cx.QueryAsync<(long Id, Guid IncidenteId, string Tipo, string Texto, DateTime CriadoEm, bool Lido)>(new CommandDefinition(@"
SELECT a.id, a.incidente_id, a.tipo, a.texto, a.criado_em, (l.aviso_id IS NOT NULL)
  FROM acompanhamento_aviso a LEFT JOIN acompanhamento_aviso_lido l ON l.aviso_id = a.id AND l.usuario_id = @U
 WHERE (@So = FALSE OR l.aviso_id IS NULL)
 ORDER BY a.criado_em DESC, a.id DESC LIMIT 200;", new { U = usuarioId, So = somenteNaoLidos }, cancellationToken: ct));
            return linhas.Select(l => new AvisoDto(l.Id, l.IncidenteId, l.Tipo, l.Texto, Utc(l.CriadoEm), l.Lido)).ToList();
        }

        public async Task<int> MarcarLidosAsync(IReadOnlyCollection<long> ids, int usuarioId, CancellationToken ct = default)
        {
            if (ids == null || ids.Count == 0) throw new IncidenteException(422, "IDS_OBRIGATORIOS", "Informe os avisos lidos.");
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO acompanhamento_aviso_lido (aviso_id, usuario_id)
SELECT id, @U FROM acompanhamento_aviso WHERE id = ANY(@Ids) ON CONFLICT DO NOTHING;", new { U = usuarioId, Ids = ids.Distinct().ToArray() }, cancellationToken: ct));
        }

        public async Task<AvisarResultadoDto> AvisarAsync(Guid incidenteId, AvisarRequest request, int usuarioId, CancellationToken ct = default)
        {
            var destinatarios = (request.Destinatarios ?? new()).Where(d => d != null).ToList();
            if (destinatarios.Count == 0 || destinatarios.Any(d => d.LojaId == Guid.Empty || string.IsNullOrWhiteSpace(d.Contato)))
                throw new IncidenteException(422, "SEM_DESTINATARIO", "Informe ao menos um destinatario (loja e contato).");
            if (string.IsNullOrWhiteSpace(request.Mensagem) || !request.MensagemValidada)
                throw new IncidenteException(422, "MENSAGEM_NAO_VALIDADA", "A mensagem precisa de texto e da validacao de uma pessoa antes de ser preparada.");
            var mensagem = request.Mensagem.Trim();
            var contatos = destinatarios.Select(d => (d.LojaId, Contato: d.Contato!.Trim())).ToList();
            if (contatos.Select(c => c.Contato).Distinct().Count() != contatos.Count)
                throw new IncidenteException(422, "DESTINATARIO_DUPLICADO", "Ha contatos repetidos na lista.");

            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            await TravaAsync(cx, tx, ct);
            var estado = await cx.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT estado FROM acompanhamento_incidente WHERE id = @I;", new { I = incidenteId }, tx, cancellationToken: ct));
            if (estado == null) throw new IncidenteException(404, "INCIDENTE_INEXISTENTE", "Incidente nao encontrado.");
            if (estado != "aberto") throw new IncidenteException(409, "INCIDENTE_NORMALIZADO", "O incidente ja foi normalizado.");
            var vinculadas = (await cx.QueryAsync<Guid>(new CommandDefinition(
                "SELECT DISTINCT loja_id FROM acompanhamento_falha WHERE incidente_id = @I;", new { I = incidenteId }, tx, cancellationToken: ct))).ToHashSet();
            var fora = contatos.Where(c => !vinculadas.Contains(c.LojaId)).Select(c => c.LojaId).ToList();
            if (fora.Count > 0) throw new IncidenteException(422, "LOJA_FORA_DO_INCIDENTE", "Ha destinatarios de lojas que nao fazem parte do incidente.", fora);
            var repetidos = (await cx.QueryAsync<string>(new CommandDefinition(
                "SELECT contato FROM acompanhamento_incidente_envio WHERE incidente_id = @I AND contato = ANY(@C);", new { I = incidenteId, C = contatos.Select(c => c.Contato).ToArray() }, tx, cancellationToken: ct))).ToList();
            if (repetidos.Count > 0) throw new IncidenteException(409, "ENVIO_REPETIDO", "Ja existe aviso preparado para estes contatos neste incidente.", repetidos);

            var envios = new List<EnvioDto>();
            foreach (var (loja, contato) in contatos)
            {
                var enviado = false;
                string? aviso = "Servico de mensagens indisponivel: mensagem preparada, nao enviada.";
                if (_mensageria is { Disponivel: true })
                {
                    enviado = await _mensageria.EnviarAsync(loja, contato, mensagem, ct);
                    aviso = enviado ? null : "O servico de mensagens recusou o envio; ficou pendente.";
                }
                var estadoEnvio = enviado ? "enviado" : "pendente_servico";
                await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO acompanhamento_incidente_envio (incidente_id, loja_id, contato, mensagem, estado, preparado_por)
VALUES (@I, @L, @C, @M, @E, @U);", new { I = incidenteId, L = loja, C = contato, M = mensagem, E = estadoEnvio, U = usuarioId }, tx, cancellationToken: ct));
                await EventoAsync(cx, tx, incidenteId, null, loja, enviado ? "aviso_enviado" : "aviso_preparado", new { Contato = contato }, usuarioId, ct);
                envios.Add(new EnvioDto(loja, contato, estadoEnvio, enviado, aviso));
            }
            await tx.CommitAsync(ct);
            return new AvisarResultadoDto(envios);
        }

        // ---- normalizacao

        public async Task<IncidenteDto> NormalizouAsync(Guid incidenteId, NormalizouRequest request, int usuarioId, CancellationToken ct = default)
        {
            if (!request.Confirmado) throw new IncidenteException(422, "CONFIRMACAO_OBRIGATORIA", "Confirme que a falha normalizou.");
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            await TravaAsync(cx, tx, ct);
            var agora = _clock.GetUtcNow();
            var atualizado = await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE acompanhamento_incidente SET estado = 'normalizado', fim = @Agora, normalizou_por = @U, normalizou_nota = @Nota
 WHERE id = @I AND estado = 'aberto';", new { I = incidenteId, Agora = agora, U = usuarioId, Nota = request.Nota?.Trim() }, tx, cancellationToken: ct));
            if (atualizado == 0)
            {
                var existe = await cx.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS (SELECT 1 FROM acompanhamento_incidente WHERE id = @I);", new { I = incidenteId }, tx, cancellationToken: ct));
                throw existe ? new IncidenteException(409, "INCIDENTE_NORMALIZADO", "O incidente ja foi normalizado.") : new IncidenteException(404, "INCIDENTE_INEXISTENTE", "Incidente nao encontrado.");
            }
            // So fim/estado mudam; causa, executor e prova originais ficam.
            await cx.ExecuteAsync(new CommandDefinition(
                "UPDATE acompanhamento_falha SET fim = COALESCE(fim, @Agora), estado = 'normalizada' WHERE incidente_id = @I;", new { I = incidenteId, Agora = agora }, tx, cancellationToken: ct));
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO acompanhamento_aviso (incidente_id, tipo, texto) VALUES (@I, 'incidente_normalizado', 'Incidente normalizado e confirmado pela equipe.') ON CONFLICT DO NOTHING;",
                new { I = incidenteId }, tx, cancellationToken: ct));
            await EventoAsync(cx, tx, incidenteId, null, null, "incidente_normalizado", new { request.Nota }, usuarioId, ct);
            await tx.CommitAsync(ct);
            return (await ListarAsync(null, ct)).Incidentes.First(i => i.Id == incidenteId);
        }
    }
}
