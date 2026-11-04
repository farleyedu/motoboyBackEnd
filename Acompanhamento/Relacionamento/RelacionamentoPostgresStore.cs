using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Acompanhamento.Fases;
using Dapper;
using Npgsql;

namespace APIBack.Acompanhamento.Relacionamento
{
    /// <summary>
    /// Relacionamento no PostgreSQL. AbrirAsync trava a linha de acompanhamento_relacionamento (FOR UPDATE); responsavel, proximo passo,
    /// marcacao, cobranca, retencao, avisos e eventos gravam na mesma transacao e a versao sobe uma vez no Confirmar.
    /// </summary>
    public sealed class RelacionamentoPostgresStore : IRelacionamentoStore
    {
        private readonly NpgsqlDataSource _db;
        private readonly IAcompanhamentoStore _acompanhamento;

        public RelacionamentoPostgresStore(NpgsqlDataSource db, IAcompanhamentoStore acompanhamento)
        {
            _db = db;
            _acompanhamento = acompanhamento;
        }

        internal static DateTimeOffset Utc(DateTime v) => new(DateTime.SpecifyKind(v, DateTimeKind.Utc));
        internal static DateTimeOffset? Utc(DateTime? v) => v.HasValue ? Utc(v.Value) : null;
        internal static DateOnly Dia(DateTime v) => DateOnly.FromDateTime(v);

        public async Task<Guid?> ResolverAsync(Guid id, CancellationToken ct) => (await _acompanhamento.ResolverAsync(id, ct))?.Id;

        public async Task<UsuarioEquipeDto?> UsuarioDaEquipeAsync(int usuarioId, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.QueryFirstOrDefaultAsync<UsuarioEquipeDto>(new CommandDefinition(@"
SELECT id AS Id, COALESCE(nome, email, '') AS Nome FROM usuario
 WHERE id = @Id AND COALESCE(is_super_admin, FALSE) = TRUE AND deleted_at IS NULL;", new { Id = usuarioId }, cancellationToken: ct));
        }

        public async Task<IReadOnlyList<UsuarioEquipeDto>> MembrosAsync(CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return (await cx.QueryAsync<UsuarioEquipeDto>(new CommandDefinition(@"
SELECT id AS Id, COALESCE(nome, email, '') AS Nome FROM usuario
 WHERE COALESCE(is_super_admin, FALSE) = TRUE AND deleted_at IS NULL ORDER BY nome, id;", cancellationToken: ct))).ToList();
        }

        private sealed class CarteiraRaw
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
            public DateTime? VisivelAnteriorAte { get; set; }
            public Guid? ProximoPassoId { get; set; }
            public string? ProximoPassoTipo { get; set; }
            public DateTime? ProximoPassoData { get; set; }
            public string? ProximoPassoNota { get; set; }
            public int? ProximoPassoCriadoPor { get; set; }
            public DateTime? ProximoPassoCriadoEm { get; set; }
            public DateTime? UltimaEnviada { get; set; }
            public DateTime? UltimaRecebida { get; set; }
            public string? Marcacao { get; set; }
        }

        public async Task<IReadOnlyList<LojaCarteiraRow>> CarteiraAsync(CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var linhas = await cx.QueryAsync<CarteiraRaw>(new CommandDefinition(@"
SELECT a.id AS AcompanhamentoId, a.oportunidade_id AS OportunidadeId, a.estabelecimento_id AS EstabelecimentoId,
       COALESCE(e.nome_fantasia, o.nome_empresa, '') AS Nome, a.fase AS Fase,
       COALESCE(r.responsavel_usuario_id, o.responsavel_usuario_id) AS ResponsavelUsuarioId, COALESCE(u.nome, u.email) AS ResponsavelNome, COALESCE(r.versao, 1) AS Versao,
       r.anterior_usuario_id AS AnteriorUsuarioId, r.visivel_anterior_ate AS VisivelAnteriorAte,
       p.id AS ProximoPassoId, p.tipo AS ProximoPassoTipo, p.data AS ProximoPassoData, p.nota AS ProximoPassoNota,
       p.criado_por AS ProximoPassoCriadoPor, p.criado_em AS ProximoPassoCriadoEm,
       m.enviada AS UltimaEnviada, m.recebida AS UltimaRecebida, mc.tipo AS Marcacao
  FROM acompanhamento a
  LEFT JOIN estabelecimentos e ON e.id = a.estabelecimento_id
  LEFT JOIN crm_oportunidade o ON o.id = a.oportunidade_id
  LEFT JOIN acompanhamento_relacionamento r ON r.acompanhamento_id = a.id
  LEFT JOIN usuario u ON u.id = COALESCE(r.responsavel_usuario_id, o.responsavel_usuario_id)
  LEFT JOIN acompanhamento_proximo_passo p ON p.acompanhamento_id = a.id AND p.estado = 'aberto'
  LEFT JOIN LATERAL (SELECT MAX(em) FILTER (WHERE direcao = 'enviada') AS enviada, MAX(em) FILTER (WHERE direcao = 'recebida') AS recebida
                       FROM acompanhamento_contato_marco WHERE acompanhamento_id = a.id) m ON TRUE
  LEFT JOIN acompanhamento_marcacao mc ON mc.acompanhamento_id = a.id AND mc.removida_em IS NULL
 WHERE a.fase NOT IN ('encerrado', 'perdido')
 ORDER BY p.data NULLS LAST, Nome;", cancellationToken: ct));
            return linhas.Select(l => new LojaCarteiraRow
            {
                AcompanhamentoId = l.AcompanhamentoId, OportunidadeId = l.OportunidadeId, EstabelecimentoId = l.EstabelecimentoId, Nome = l.Nome, Fase = l.Fase,
                ResponsavelUsuarioId = l.ResponsavelUsuarioId, ResponsavelNome = l.ResponsavelNome, Versao = l.Versao, AnteriorUsuarioId = l.AnteriorUsuarioId,
                VisivelAnteriorAte = Utc(l.VisivelAnteriorAte), ProximoPassoId = l.ProximoPassoId, ProximoPassoTipo = l.ProximoPassoTipo,
                ProximoPassoData = l.ProximoPassoData.HasValue ? Dia(l.ProximoPassoData.Value) : null, ProximoPassoNota = l.ProximoPassoNota,
                ProximoPassoCriadoPor = l.ProximoPassoCriadoPor, ProximoPassoCriadoEm = Utc(l.ProximoPassoCriadoEm),
                UltimaEnviada = Utc(l.UltimaEnviada), UltimaRecebida = Utc(l.UltimaRecebida), Marcacao = l.Marcacao
            }).ToList();
        }

        private sealed class AtividadeRaw
        {
            public Guid Id { get; set; }
            public Guid AcompanhamentoId { get; set; }
            public Guid? OportunidadeId { get; set; }
            public Guid? EstabelecimentoId { get; set; }
            public string Nome { get; set; } = string.Empty;
            public string Fase { get; set; } = string.Empty;
            public string Tipo { get; set; } = string.Empty;
            public DateTime Data { get; set; }
            public string? Nota { get; set; }
            public string Estado { get; set; } = string.Empty;
            public string? Resultado { get; set; }
            public string? ResultadoNota { get; set; }
            public DateTime? RealizadoEm { get; set; }
            public int? ResponsavelId { get; set; }
            public string? ResponsavelNome { get; set; }
            public DateTime CriadoEm { get; set; }
            public long Versao { get; set; }
        }

        public async Task<IReadOnlyList<AtividadeDto>> AtividadesAsync(string escopo, DateOnly hoje, int? responsavelUsuarioId, int limite, CancellationToken ct)
        {
            var (filtro, ordem) = escopo switch
            {
                EscoposAtividade.Vencidas => ("p.estado = 'aberto' AND p.data < @Hoje", "p.data, Nome"),
                EscoposAtividade.Proximas => ("p.estado = 'aberto' AND p.data > @Hoje", "p.data, Nome"),
                EscoposAtividade.Feitas => ("p.estado IN ('feito', 'sem_sucesso')", "p.realizado_em DESC NULLS LAST, p.encerrado_em DESC"),
                _ => ("p.estado = 'aberto' AND p.data = @Hoje", "Nome")
            };
            await using var cx = await _db.OpenConnectionAsync(ct);
            var linhas = await cx.QueryAsync<AtividadeRaw>(new CommandDefinition(@"
SELECT p.id AS Id, a.id AS AcompanhamentoId, a.oportunidade_id AS OportunidadeId, a.estabelecimento_id AS EstabelecimentoId,
       COALESCE(e.nome_fantasia, o.nome_empresa, '') AS Nome, a.fase AS Fase, p.tipo AS Tipo, p.data AS Data, p.nota AS Nota, p.estado AS Estado,
       p.resultado AS Resultado, p.resultado_nota AS ResultadoNota, p.realizado_em AS RealizadoEm,
       COALESCE(r.responsavel_usuario_id, o.responsavel_usuario_id) AS ResponsavelId, COALESCE(u.nome, u.email) AS ResponsavelNome,
       p.criado_em AS CriadoEm, COALESCE(r.versao, 1) AS Versao
  FROM acompanhamento_proximo_passo p
  JOIN acompanhamento a ON a.id = p.acompanhamento_id
  LEFT JOIN estabelecimentos e ON e.id = a.estabelecimento_id
  LEFT JOIN crm_oportunidade o ON o.id = a.oportunidade_id
  LEFT JOIN acompanhamento_relacionamento r ON r.acompanhamento_id = a.id
  LEFT JOIN usuario u ON u.id = COALESCE(r.responsavel_usuario_id, o.responsavel_usuario_id)
 WHERE " + filtro + @"
   AND (@Responsavel::int IS NULL OR COALESCE(r.responsavel_usuario_id, o.responsavel_usuario_id) = @Responsavel)
 ORDER BY " + ordem + @"
 LIMIT @Limite;", new { Hoje = hoje.ToDateTime(TimeOnly.MinValue), Responsavel = responsavelUsuarioId, Limite = limite }, cancellationToken: ct));
            return linhas.Select(l => new AtividadeDto(l.Id, l.AcompanhamentoId, l.OportunidadeId, l.EstabelecimentoId, l.Nome, l.Fase, l.Tipo, Dia(l.Data), l.Nota, l.Estado,
                l.Resultado, l.ResultadoNota, Utc(l.RealizadoEm), l.ResponsavelId is { } r ? new UsuarioEquipeDto(r, l.ResponsavelNome ?? string.Empty) : null,
                Utc(l.CriadoEm), l.Estado == EstadosPasso.Aberto && Dia(l.Data) < hoje, l.Versao)).ToList();
        }

        public async Task<IReadOnlyList<JornadaLinha>> JornadaAsync(Guid acompanhamentoId, (DateTimeOffset Em, string Id)? depoisDe, int limite, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var linhas = await cx.QueryAsync<JornadaRaw>(new CommandDefinition(@"
SELECT x.id AS Id, x.em AS Em, x.origem AS Origem, x.tipo AS Tipo, x.usuario AS UsuarioId, COALESCE(u.nome, u.email) AS UsuarioNome,
       x.de AS De, x.para AS Para, x.motivo AS Motivo, x.nome AS Nome, x.referencia AS Referencia, x.detalhe AS Detalhe
  FROM (
    SELECT 'h' || lpad(h.id::text, 20, '0') AS id, h.em, 'historico' AS origem, h.tipo, h.autor_usuario_id AS usuario, h.de, h.para, h.motivo,
           pa.nome, h.passagem_id::text AS referencia, NULL::text AS detalhe
      FROM acompanhamento_historico h LEFT JOIN acompanhamento_passagem pa ON pa.id = h.passagem_id
     WHERE h.acompanhamento_id = @Id
    UNION ALL
    SELECT 'e' || lpad(ev.id::text, 20, '0'), ev.em, 'evento', ev.tipo, ev.usuario_id, NULL, NULL, NULL, NULL, NULL, ev.detalhe::text
      FROM acompanhamento_relacionamento_evento ev WHERE ev.acompanhamento_id = @Id
    UNION ALL
    SELECT 'c' || lpad(c.id::text, 20, '0'), c.em, 'contato', c.direcao, NULL, NULL, NULL, NULL, NULL, c.referencia, NULL
      FROM acompanhamento_contato_marco c WHERE c.acompanhamento_id = @Id
    UNION ALL
    SELECT 'p' || pa.id::text, pa.criada_em, 'passagem', pa.status, pa.autor_usuario_id, pa.de, pa.para, pa.motivo, pa.nome, pa.id::text, NULL
      FROM acompanhamento_passagem pa
     WHERE pa.acompanhamento_id = @Id
       AND NOT EXISTS (SELECT 1 FROM acompanhamento_historico h WHERE h.passagem_id = pa.id)
  ) x
  LEFT JOIN usuario u ON u.id = x.usuario
 WHERE (@CursorEm::timestamptz IS NULL OR (x.em, x.id) < (@CursorEm::timestamptz, @CursorId))
 ORDER BY x.em DESC, x.id DESC
 LIMIT @Limite;", new { Id = acompanhamentoId, CursorEm = depoisDe?.Em.UtcDateTime, CursorId = depoisDe?.Id, Limite = limite }, cancellationToken: ct));
            return linhas.Select(l => l.ToLinha()).ToList();
        }

        private sealed class JornadaRaw
        {
            public string Id { get; set; } = string.Empty;
            public DateTime Em { get; set; }
            public string Origem { get; set; } = string.Empty;
            public string Tipo { get; set; } = string.Empty;
            public int? UsuarioId { get; set; }
            public string? UsuarioNome { get; set; }
            public string? De { get; set; }
            public string? Para { get; set; }
            public string? Motivo { get; set; }
            public string? Nome { get; set; }
            public string? Referencia { get; set; }
            public string? Detalhe { get; set; }

            public JornadaLinha ToLinha() => new()
            {
                Id = Id, Em = Utc(Em), Origem = Origem, Tipo = Tipo, UsuarioId = UsuarioId, UsuarioNome = UsuarioNome, De = De, Para = Para,
                Motivo = Motivo, Nome = Nome, Referencia = Referencia, Detalhe = Detalhe
            };
        }

        public async Task<IReadOnlyList<AvisoRelacionamentoDto>> AvisosAsync(int usuarioId, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var linhas = await cx.QueryAsync<(long Id, Guid Acompanhamento, string Tipo, string Mensagem, int? De, DateTime Em)>(new CommandDefinition(@"
SELECT id, acompanhamento_id, tipo, mensagem, de_usuario_id, criado_em
  FROM acompanhamento_rel_aviso WHERE destinatario_usuario_id = @U AND lido_em IS NULL ORDER BY criado_em DESC LIMIT 100;",
                new { U = usuarioId }, cancellationToken: ct));
            return linhas.Select(a => new AvisoRelacionamentoDto(a.Id, a.Acompanhamento, a.Tipo, a.Mensagem, a.De, Utc(a.Em), false)).ToList();
        }

        public async Task<int> MarcarAvisosLidosAsync(int usuarioId, IReadOnlyCollection<long> ids, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.ExecuteAsync(new CommandDefinition(
                "UPDATE acompanhamento_rel_aviso SET lido_em = NOW() WHERE destinatario_usuario_id = @U AND lido_em IS NULL AND id = ANY(@Ids);",
                new { U = usuarioId, Ids = ids.ToArray() }, cancellationToken: ct));
        }

        public async Task<bool> RegistrarMarcoAsync(Guid acompanhamentoId, string direcao, DateTimeOffset em, string referencia, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO acompanhamento_contato_marco (acompanhamento_id, direcao, em, referencia) VALUES (@A, @D, @Em, @R)
ON CONFLICT (acompanhamento_id, direcao, referencia) DO NOTHING;", new { A = acompanhamentoId, D = direcao, Em = em.UtcDateTime, R = referencia }, cancellationToken: ct)) > 0;
        }

        // ---- leitura completa

        private sealed class EstadoRaw
        {
            public Guid AcompanhamentoId { get; set; }
            public Guid? OportunidadeId { get; set; }
            public Guid? EstabelecimentoId { get; set; }
            public string Nome { get; set; } = string.Empty;
            public string Fase { get; set; } = string.Empty;
            public long Versao { get; set; }
            public int? ResponsavelUsuarioId { get; set; }
            public int? AnteriorUsuarioId { get; set; }
            public DateTime? VisivelAnteriorAte { get; set; }
        }

        private const string EstadoSql = @"
SELECT a.id AS AcompanhamentoId, a.oportunidade_id AS OportunidadeId, a.estabelecimento_id AS EstabelecimentoId,
       COALESCE(e.nome_fantasia, o.nome_empresa, '') AS Nome, a.fase AS Fase, r.versao AS Versao,
       COALESCE(r.responsavel_usuario_id, o.responsavel_usuario_id) AS ResponsavelUsuarioId, r.anterior_usuario_id AS AnteriorUsuarioId, r.visivel_anterior_ate AS VisivelAnteriorAte
  FROM acompanhamento a
  JOIN acompanhamento_relacionamento r ON r.acompanhamento_id = a.id
  LEFT JOIN estabelecimentos e ON e.id = a.estabelecimento_id
  LEFT JOIN crm_oportunidade o ON o.id = a.oportunidade_id
 WHERE a.id = @Id";

        private static async Task GarantirLinhaAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid id, CancellationToken ct)
        {
            // Nasce com o responsavel que o CRM ja tinha (oportunidade ou contrato mais recente da loja).
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO acompanhamento_relacionamento (acompanhamento_id, responsavel_usuario_id, responsavel_desde)
SELECT a.id,
       COALESCE(o.responsavel_usuario_id,
                (SELECT c.responsavel_usuario_id FROM crm_contrato c WHERE c.estabelecimento_id = a.estabelecimento_id AND c.responsavel_usuario_id IS NOT NULL
                  ORDER BY c.created_at DESC LIMIT 1)),
       NOW()
  FROM acompanhamento a LEFT JOIN crm_oportunidade o ON o.id = a.oportunidade_id
 WHERE a.id = @Id
ON CONFLICT (acompanhamento_id) DO NOTHING;", new { Id = id }, tx, cancellationToken: ct));
        }

        private static async Task<(MarcacaoDto? Marcacao, ProximoPassoDto? Passo)> AtivosAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid id, DateOnly hoje, CancellationToken ct)
        {
            var m = await cx.QueryFirstOrDefaultAsync<(string Tipo, string Motivo, int Por, DateTime Desde, string? Categoria, string? Acao)?>(new CommandDefinition(
                "SELECT tipo, motivo, por_usuario_id, desde, categoria, acao_esperada FROM acompanhamento_marcacao WHERE acompanhamento_id = @Id AND removida_em IS NULL;",
                new { Id = id }, tx, cancellationToken: ct));
            var p = await cx.QueryFirstOrDefaultAsync<(Guid Id, string Tipo, DateTime Data, string? Nota, int? Por, DateTime Em)?>(new CommandDefinition(
                "SELECT id, tipo, data, nota, criado_por, criado_em FROM acompanhamento_proximo_passo WHERE acompanhamento_id = @Id AND estado = 'aberto';",
                new { Id = id }, tx, cancellationToken: ct));
            return (m is { } mm ? new MarcacaoDto(mm.Tipo, mm.Motivo, mm.Por, Utc(mm.Desde), mm.Categoria, mm.Acao) : null,
                    p is { } pp ? new ProximoPassoDto(pp.Id, pp.Tipo, Dia(pp.Data), pp.Nota, pp.Por, Utc(pp.Em), Dia(pp.Data) < hoje) : null);
        }

        private static DateOnly HojeBrasil() => RelacionamentoRegras.Hoje(DateTimeOffset.UtcNow);

        public async Task<RelacionamentoDto?> LerAsync(Guid acompanhamentoId, int eventos, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await GarantirLinhaAsync(cx, null, acompanhamentoId, ct);
            var raw = await cx.QueryFirstOrDefaultAsync<EstadoRaw>(new CommandDefinition(EstadoSql + ";", new { Id = acompanhamentoId }, cancellationToken: ct));
            if (raw == null) return null;

            var (marcacao, passo) = await AtivosAsync(cx, null, acompanhamentoId, HojeBrasil(), ct);
            var marcos = await cx.QueryFirstAsync<(DateTime? Enviada, DateTime? Recebida)>(new CommandDefinition(@"
SELECT MAX(em) FILTER (WHERE direcao = 'enviada'), MAX(em) FILTER (WHERE direcao = 'recebida')
  FROM acompanhamento_contato_marco WHERE acompanhamento_id = @Id;", new { Id = acompanhamentoId }, cancellationToken: ct));
            UsuarioEquipeDto? responsavel = null;
            if (raw.ResponsavelUsuarioId.HasValue)
                responsavel = await cx.QueryFirstOrDefaultAsync<UsuarioEquipeDto>(new CommandDefinition(
                    "SELECT id AS Id, COALESCE(nome, email, '') AS Nome FROM usuario WHERE id = @Id;", new { Id = raw.ResponsavelUsuarioId }, cancellationToken: ct));
            var historico = await cx.QueryAsync<(long Id, string Tipo, int? Usuario, DateTime Em, string Detalhe)>(new CommandDefinition(@"
SELECT id, tipo, usuario_id, em, detalhe::text FROM acompanhamento_relacionamento_evento
 WHERE acompanhamento_id = @Id ORDER BY em DESC, id DESC LIMIT @N;", new { Id = acompanhamentoId, N = eventos }, cancellationToken: ct));

            var agora = DateTimeOffset.UtcNow;
            return new RelacionamentoDto
            {
                AcompanhamentoId = raw.AcompanhamentoId, OportunidadeId = raw.OportunidadeId, EstabelecimentoId = raw.EstabelecimentoId, Nome = raw.Nome, Fase = raw.Fase,
                Responsavel = responsavel, Versao = raw.Versao, ProximoPasso = passo, Marcacao = marcacao,
                Espera = RelacionamentoRegras.CalcularEspera(Utc(marcos.Enviada), Utc(marcos.Recebida)),
                VisaoAnterior = raw.AnteriorUsuarioId.HasValue && raw.VisivelAnteriorAte.HasValue && Utc(raw.VisivelAnteriorAte.Value) > agora
                    ? new VisaoAnteriorDto(raw.AnteriorUsuarioId.Value, Utc(raw.VisivelAnteriorAte.Value)) : null,
                Eventos = historico.Select(h => new EventoRelacionamentoDto(h.Id, RelacionamentoRegras.TipoEvento(h.Tipo), h.Usuario, Utc(h.Em),
                    RelacionamentoRegras.DetalheEmCamelCase(h.Detalhe))).ToList()
            };
        }

        // ---- escrita

        public async Task<IRelacionamentoTx> AbrirAsync(Guid acompanhamentoId, CancellationToken ct)
        {
            var cx = await _db.OpenConnectionAsync(ct);
            NpgsqlTransaction? tx = null;
            try
            {
                tx = await cx.BeginTransactionAsync(ct);
                // Mesma ordem das passagens (oportunidade -> acompanhamento -> relacionamento): uma passagem tambem encerra passo aqui.
                var oportunidade = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                    "SELECT oportunidade_id FROM acompanhamento WHERE id = @Id;", new { Id = acompanhamentoId }, tx, cancellationToken: ct));
                if (oportunidade.HasValue)
                    await cx.ExecuteAsync(new CommandDefinition("SELECT 1 FROM crm_oportunidade WHERE id = @O FOR UPDATE;", new { O = oportunidade }, tx, cancellationToken: ct));
                await cx.ExecuteAsync(new CommandDefinition("SELECT 1 FROM acompanhamento WHERE id = @Id FOR UPDATE;", new { Id = acompanhamentoId }, tx, cancellationToken: ct));
                await GarantirLinhaAsync(cx, tx, acompanhamentoId, ct);
                var raw = await cx.QueryFirstOrDefaultAsync<EstadoRaw>(new CommandDefinition(
                    EstadoSql + " FOR UPDATE OF r;", new { Id = acompanhamentoId }, tx, cancellationToken: ct))
                    ?? throw new RelacionamentoException(404, "NAO_ENCONTRADO", "Loja ou lead nao encontrado.");
                var (marcacao, passo) = await AtivosAsync(cx, tx, acompanhamentoId, HojeBrasil(), ct);
                var estado = new RelacionamentoEstado
                {
                    AcompanhamentoId = raw.AcompanhamentoId, OportunidadeId = raw.OportunidadeId, EstabelecimentoId = raw.EstabelecimentoId, Nome = raw.Nome,
                    Fase = raw.Fase, Versao = raw.Versao, ResponsavelUsuarioId = raw.ResponsavelUsuarioId, Marcacao = marcacao, ProximoPasso = passo
                };
                return new Tx(cx, tx, estado);
            }
            catch
            {
                if (tx != null) await tx.DisposeAsync();
                await cx.DisposeAsync();
                throw;
            }
        }

        private sealed class Tx : IRelacionamentoTx
        {
            private readonly NpgsqlConnection _cx;
            private readonly NpgsqlTransaction _tx;
            private bool _alterou, _encerrada;

            public Tx(NpgsqlConnection cx, NpgsqlTransaction tx, RelacionamentoEstado estado)
            {
                _cx = cx;
                _tx = tx;
                Estado = estado;
            }

            public RelacionamentoEstado Estado { get; }
            private Guid Id => Estado.AcompanhamentoId;

            public async Task DefinirResponsavelAsync(int novo, int? anterior, DateTimeOffset visivelAnteriorAte, CancellationToken ct)
            {
                await _cx.ExecuteAsync(new CommandDefinition(@"
UPDATE acompanhamento_relacionamento
   SET responsavel_usuario_id = @Novo, responsavel_desde = NOW(), anterior_usuario_id = @Ant,
       visivel_anterior_ate = CASE WHEN @Ant IS NULL THEN NULL ELSE @Ate END, atualizado_em = NOW()
 WHERE acompanhamento_id = @Id;", new { Id, Novo = novo, Ant = anterior, Ate = visivelAnteriorAte.UtcDateTime }, _tx, cancellationToken: ct));

                // Espelha no CRM para as telas que ainda leem o responsavel de la; o historico da oportunidade registra a troca.
                if (Estado.OportunidadeId.HasValue)
                {
                    await _cx.ExecuteAsync(new CommandDefinition("UPDATE crm_oportunidade SET responsavel_usuario_id = @Novo WHERE id = @O;",
                        new { Novo = novo, O = Estado.OportunidadeId }, _tx, cancellationToken: ct));
                    await _cx.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO crm_oportunidade_historico (id_oportunidade, id_usuario, acao, detalhe) VALUES (@O, @U, 'Responsavel alterado', @D);",
                        new { O = Estado.OportunidadeId, U = novo, D = $"de {anterior?.ToString() ?? "ninguem"} para {novo}" }, _tx, cancellationToken: ct));
                }
                if (Estado.EstabelecimentoId.HasValue)
                    await _cx.ExecuteAsync(new CommandDefinition("UPDATE crm_contrato SET responsavel_usuario_id = @Novo WHERE estabelecimento_id = @E;",
                        new { Novo = novo, E = Estado.EstabelecimentoId }, _tx, cancellationToken: ct));
                _alterou = true;
            }

            public async Task SubstituirProximoPassoAsync(string tipo, DateOnly data, string? nota, int usuarioId, CancellationToken ct)
            {
                await _cx.ExecuteAsync(new CommandDefinition(@"
UPDATE acompanhamento_proximo_passo SET estado = 'substituido', encerrado_em = NOW(), encerrado_por = @U
 WHERE acompanhamento_id = @Id AND estado = 'aberto';", new { Id, U = usuarioId }, _tx, cancellationToken: ct));
                await _cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO acompanhamento_proximo_passo (acompanhamento_id, tipo, data, nota, criado_por) VALUES (@Id, @Tipo, @Data, @Nota, @U);",
                    new { Id, Tipo = tipo, Data = data.ToDateTime(TimeOnly.MinValue), Nota = nota, U = usuarioId }, _tx, cancellationToken: ct));
                _alterou = true;
            }

            private const string PassoColunas = "id, tipo, data, nota, estado, resultado";

            private sealed class PassoRaw
            {
                public Guid Id { get; set; }
                public string Tipo { get; set; } = string.Empty;
                public DateTime Data { get; set; }
                public string? Nota { get; set; }
                public string Estado { get; set; } = string.Empty;
                public string? Resultado { get; set; }

                public PassoRegistro ToRegistro() => new() { Id = Id, Tipo = Tipo, Data = Dia(Data), Nota = Nota, Estado = Estado, Resultado = Resultado };
            }

            public async Task<PassoRegistro?> PassoAsync(Guid passoId, CancellationToken ct) =>
                (await _cx.QueryFirstOrDefaultAsync<PassoRaw>(new CommandDefinition(
                    "SELECT " + PassoColunas + " FROM acompanhamento_proximo_passo WHERE id = @P AND acompanhamento_id = @Id;",
                    new { P = passoId, Id }, _tx, cancellationToken: ct)))?.ToRegistro();

            public async Task<PassoRegistro?> UltimoSubstituidoAsync(CancellationToken ct) =>
                (await _cx.QueryFirstOrDefaultAsync<PassoRaw>(new CommandDefinition(
                    "SELECT " + PassoColunas + " FROM acompanhamento_proximo_passo WHERE acompanhamento_id = @Id AND estado = 'substituido' ORDER BY encerrado_em DESC LIMIT 1;",
                    new { Id }, _tx, cancellationToken: ct)))?.ToRegistro();

            public async Task EncerrarPassoAsync(Guid passoId, PassoEncerramento e, int usuarioId, CancellationToken ct)
            {
                await _cx.ExecuteAsync(new CommandDefinition(@"
UPDATE acompanhamento_proximo_passo
   SET estado = @Estado, resultado = @Resultado, resultado_nota = @Nota, realizado_em = @RealizadoEm, encerrado_motivo = @Motivo,
       encerrado_em = NOW(), encerrado_por = @U
 WHERE id = @P AND acompanhamento_id = @Id AND estado = 'aberto';",
                    new { P = passoId, Id, e.Estado, e.Resultado, e.Nota, RealizadoEm = e.RealizadoEm?.UtcDateTime, e.Motivo, U = usuarioId }, _tx, cancellationToken: ct));
                _alterou = true;
            }

            public async Task<Guid> AbrirPassoAsync(string tipo, DateOnly data, string? nota, Guid? origemPassoId, int usuarioId, CancellationToken ct)
            {
                _alterou = true;
                return await _cx.ExecuteScalarAsync<Guid>(new CommandDefinition(@"
INSERT INTO acompanhamento_proximo_passo (acompanhamento_id, tipo, data, nota, criado_por, origem_passo_id)
VALUES (@Id, @Tipo, @Data, @Nota, @U, @Origem) RETURNING id;",
                    new { Id, Tipo = tipo, Data = data.ToDateTime(TimeOnly.MinValue), Nota = nota, U = usuarioId, Origem = origemPassoId }, _tx, cancellationToken: ct));
            }

            public async Task AplicarMarcacaoAsync(string tipo, string motivo, string? categoria, string? acaoEsperada, int usuarioId, CancellationToken ct)
            {
                await _cx.ExecuteAsync(new CommandDefinition(@"
UPDATE acompanhamento_marcacao SET removida_em = NOW(), removida_por = @U, removida_motivo = 'substituida por ' || @Tipo
 WHERE acompanhamento_id = @Id AND removida_em IS NULL;", new { Id, U = usuarioId, Tipo = tipo }, _tx, cancellationToken: ct));
                await _cx.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO acompanhamento_marcacao (acompanhamento_id, tipo, motivo, por_usuario_id, categoria, acao_esperada) VALUES (@Id, @Tipo, @Motivo, @U, @Categoria, @Acao);",
                    new { Id, Tipo = tipo, Motivo = motivo, U = usuarioId, Categoria = categoria, Acao = acaoEsperada }, _tx, cancellationToken: ct));
                _alterou = true;
            }

            public async Task RemoverMarcacaoAsync(int usuarioId, string motivo, CancellationToken ct)
            {
                await _cx.ExecuteAsync(new CommandDefinition(@"
UPDATE acompanhamento_marcacao SET removida_em = NOW(), removida_por = @U, removida_motivo = @M
 WHERE acompanhamento_id = @Id AND removida_em IS NULL;", new { Id, U = usuarioId, M = motivo }, _tx, cancellationToken: ct));
                _alterou = true;
            }

            public async Task RegistrarCobrancaAsync(int cobrador, int responsavel, string? mensagem, CancellationToken ct)
            {
                await _cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO acompanhamento_cobranca_responsavel (acompanhamento_id, cobrador_usuario_id, responsavel_usuario_id, mensagem) VALUES (@Id, @C, @R, @M);",
                    new { Id, C = cobrador, R = responsavel, M = mensagem }, _tx, cancellationToken: ct));
                _alterou = true;
            }

            public async Task<DateTimeOffset?> UltimaCobrancaAsync(int cobrador, CancellationToken ct) =>
                Utc(await _cx.ExecuteScalarAsync<DateTime?>(new CommandDefinition(@"
SELECT MAX(em) FROM acompanhamento_cobranca_responsavel WHERE acompanhamento_id = @Id AND cobrador_usuario_id = @C;",
                    new { Id, C = cobrador }, _tx, cancellationToken: ct)));

            private sealed class RetencaoRaw
            {
                public Guid Id { get; set; }
                public Guid AcompanhamentoId { get; set; }
                public string Pedido { get; set; } = string.Empty;
                public DateTime PedidoEm { get; set; }
                public string Leitura { get; set; } = string.Empty;
                public string OfertaTipo { get; set; } = string.Empty;
                public string? OfertaDescricao { get; set; }
                public decimal? OfertaValor { get; set; }
                public bool OfertaAprovada { get; set; }
                public int? OfertaAprovadaPor { get; set; }
                public string Canal { get; set; } = string.Empty;
                public string Resultado { get; set; } = string.Empty;
                public string? SaidaQuando { get; set; }
                public DateTime? SaidaEm { get; set; }
                public DateTime? PausaAte { get; set; }
                public bool CobrancaEmAberto { get; set; }
                public string? Observacao { get; set; }
                public int? CriadoPor { get; set; }
                public DateTime CriadoEm { get; set; }

                public RetencaoDto ToDto() => new()
                {
                    Id = Id, AcompanhamentoId = AcompanhamentoId, Pedido = Pedido, PedidoEm = Utc(PedidoEm), Leitura = Leitura,
                    Oferta = new OfertaRetencaoDto { Tipo = OfertaTipo, Descricao = OfertaDescricao, Valor = OfertaValor, Aprovada = OfertaAprovada, AprovadaPor = OfertaAprovadaPor },
                    Canal = Canal, Resultado = Resultado, SaidaQuando = SaidaQuando, SaidaEm = SaidaEm.HasValue ? Dia(SaidaEm.Value) : null,
                    PausaAte = PausaAte.HasValue ? Dia(PausaAte.Value) : null, CobrancaEmAberto = CobrancaEmAberto, Observacao = Observacao,
                    CriadoPor = CriadoPor, CriadoEm = Utc(CriadoEm), ExecutouFinanceiro = false
                };
            }

            private const string RetencaoColunas = @"
id, acompanhamento_id AS AcompanhamentoId, pedido, pedido_em AS PedidoEm, leitura, oferta_tipo AS OfertaTipo, oferta_descricao AS OfertaDescricao,
oferta_valor AS OfertaValor, oferta_aprovada AS OfertaAprovada, oferta_aprovada_por AS OfertaAprovadaPor, canal, resultado, saida_quando AS SaidaQuando,
saida_em AS SaidaEm, pausa_ate AS PausaAte, cobranca_em_aberto AS CobrancaEmAberto, observacao, criado_por AS CriadoPor, criado_em AS CriadoEm";

            public async Task<RetencaoDto?> RetencaoPorChaveAsync(string chave, CancellationToken ct) =>
                (await _cx.QueryFirstOrDefaultAsync<RetencaoRaw>(new CommandDefinition(
                    "SELECT " + RetencaoColunas + " FROM acompanhamento_retencao WHERE acompanhamento_id = @Id AND chave_idempotencia = @C;",
                    new { Id, C = chave }, _tx, cancellationToken: ct)))?.ToDto();

            public async Task<RetencaoDto> RegistrarRetencaoAsync(RetencaoRegistro r, CancellationToken ct)
            {
                var raw = await _cx.QueryFirstAsync<RetencaoRaw>(new CommandDefinition(@"
INSERT INTO acompanhamento_retencao (acompanhamento_id, chave_idempotencia, pedido, pedido_em, leitura, oferta_tipo, oferta_descricao, oferta_valor,
    oferta_aprovada, oferta_aprovada_por, canal, resultado, saida_quando, saida_em, pausa_ate, cobranca_em_aberto, observacao, criado_por)
VALUES (@Id, @Chave, @Pedido, @PedidoEm, @Leitura, @OfertaTipo, @OfertaDescricao, @OfertaValor, @OfertaAprovada, @OfertaAprovadaPor, @Canal, @Resultado,
    @SaidaQuando, @SaidaEm, @PausaAte, @CobrancaEmAberto, @Observacao, @CriadoPor)
RETURNING " + RetencaoColunas + ";", new
                {
                    Id, r.Chave, r.Pedido, PedidoEm = r.PedidoEm.UtcDateTime, r.Leitura, r.OfertaTipo, r.OfertaDescricao, r.OfertaValor, r.OfertaAprovada,
                    r.OfertaAprovadaPor, r.Canal, r.Resultado, r.SaidaQuando, SaidaEm = r.SaidaEm?.ToDateTime(TimeOnly.MinValue),
                    PausaAte = r.PausaAte?.ToDateTime(TimeOnly.MinValue), r.CobrancaEmAberto, r.Observacao, r.CriadoPor
                }, _tx, cancellationToken: ct));
                _alterou = true;
                return raw.ToDto();
            }

            public async Task<string> IniciarSaidaAsync(Guid retencaoId, RetencaoRegistro r, int usuarioId, CancellationToken ct)
            {
                // Em Saindo ja nao ha o que mudar; nas outras fases nao existe saida: fica o registro, a fase nao muda.
                if (Estado.Fase != FasesAcompanhamento.Operando) return Estado.Fase;
                var def = PassagensCatalogo.Obter(FasesAcompanhamento.Operando, FasesAcompanhamento.Saindo)!;
                var passagem = await _cx.ExecuteScalarAsync<Guid>(new CommandDefinition(@"
INSERT INTO acompanhamento_passagem (acompanhamento_id, chave_idempotencia, de, para, fase_resultante, nome, status, autor_usuario_id, autor_tipo, motivo, dados)
VALUES (@Id, @Chave, @De, @Para, @Para, @Nome, @Status, @U, @Autor, @Motivo, CAST(@Dados AS jsonb)) RETURNING id;",
                    new
                    {
                        Id, Chave = $"retencao:{retencaoId:N}", def.De, def.Para, def.Nome, Status = StatusPassagem.Feita, U = usuarioId, Autor = AutoresPassagem.Usuario,
                        Motivo = r.Pedido, Dados = RelacionamentoRegras.SerializarDetalhe(new { retencaoId, quando = r.SaidaQuando, saidaEm = r.SaidaEm })
                    }, _tx, cancellationToken: ct));
                await _cx.ExecuteAsync(new CommandDefinition("UPDATE acompanhamento SET saida_quando = @Quando, saida_em = @Em WHERE id = @Id;",
                    new { Id, Quando = r.SaidaQuando, Em = r.SaidaEm?.ToDateTime(TimeOnly.MinValue) }, _tx, cancellationToken: ct));
                await AcompanhamentoSql.MudarFaseAsync(_cx, _tx, Id, def.De, def.Para, usuarioId, AutoresPassagem.Usuario, r.Pedido, passagem, ct);
                Estado.Fase = def.Para;
                return def.Para;
            }

            public async Task<bool> CobrancaEmAbertoAsync(CancellationToken ct)
            {
                if (!Estado.EstabelecimentoId.HasValue || !await AcompanhamentoSql.TabelaExisteAsync(_cx, _tx, "comercial_cobranca", ct)) return false;
                return await _cx.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS (SELECT 1 FROM comercial_cobranca WHERE id_estabelecimento = @Loja AND status IN ('aberta', 'processando'));",
                    new { Loja = Estado.EstabelecimentoId }, _tx, cancellationToken: ct));
            }

            public async Task AvisarAsync(int destinatario, string tipo, string mensagem, int? de, CancellationToken ct)
            {
                await _cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO acompanhamento_rel_aviso (destinatario_usuario_id, acompanhamento_id, tipo, mensagem, de_usuario_id) VALUES (@D, @Id, @Tipo, @M, @De);",
                    new { D = destinatario, Id, Tipo = tipo, M = mensagem, De = de }, _tx, cancellationToken: ct));
            }

            public async Task EventoAsync(string tipo, object detalhe, int? usuarioId, CancellationToken ct)
            {
                await _cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO acompanhamento_relacionamento_evento (acompanhamento_id, tipo, detalhe, usuario_id) VALUES (@Id, @Tipo, @D::jsonb, @U);",
                    new { Id, Tipo = tipo, D = RelacionamentoRegras.SerializarDetalhe(detalhe), U = usuarioId }, _tx, cancellationToken: ct));
            }

            public async Task<long> ConfirmarAsync(CancellationToken ct)
            {
                var versao = Estado.Versao;
                if (_alterou)
                    versao = await _cx.ExecuteScalarAsync<long>(new CommandDefinition(
                        "UPDATE acompanhamento_relacionamento SET versao = versao + 1, atualizado_em = NOW() WHERE acompanhamento_id = @Id RETURNING versao;",
                        new { Id }, _tx, cancellationToken: ct));
                await _tx.CommitAsync(ct);
                _encerrada = true;
                return versao;
            }

            public async ValueTask DisposeAsync()
            {
                if (!_encerrada) await _tx.RollbackAsync();
                await _tx.DisposeAsync();
                await _cx.DisposeAsync();
            }
        }
    }
}
