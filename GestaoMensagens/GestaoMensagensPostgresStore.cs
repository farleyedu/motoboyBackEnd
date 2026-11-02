using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Atendimento;
using APIBack.Atendimento.Templates;
using Dapper;
using Npgsql;

namespace APIBack.GestaoMensagens
{
    public sealed class GestaoMensagensPostgresStore : IGestaoMensagensStore
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly NpgsqlDataSource _db;
        private readonly ICanalRepository _canais;

        public GestaoMensagensPostgresStore(NpgsqlDataSource db, ICanalRepository canais)
        {
            _db = db;
            _canais = canais;
        }

        private const string Colunas = @"
id AS Id, direcao AS Direcao, via AS Via, canal_id AS CanalId, alvo_tipo AS AlvoTipo, estabelecimento_id AS EstabelecimentoId,
oportunidade_id AS OportunidadeId, contato_ref AS ContatoRef, telefone_e164 AS TelefoneE164, telefone_chave AS TelefoneChave,
autor_usuario_id AS AutorUsuarioId, tipo_conteudo AS TipoConteudo, texto AS Texto, modelo_nome AS ModeloNome, modelo_idioma AS ModeloIdioma,
modelo_categoria AS ModeloCategoria, variaveis::text AS VariaveisJson, finalidade AS Finalidade, estado AS Estado, erro_codigo AS ErroCodigo,
erro_detalhe AS ErroDetalhe, referencia_externa AS ReferenciaExterna, chave_idempotencia AS ChaveIdempotencia, chamado_id AS ChamadoId,
confirmado_por AS ConfirmadoPor, confirmado_em AS ConfirmadoEm, enviado_em AS EnviadoEm, criado_em AS CriadoEm";

        public async Task<AlvoMensagem?> ObterAlvoAsync(Guid? lojaId, Guid? oportunidadeId, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            if (lojaId.HasValue)
            {
                var nome = await cx.QueryFirstOrDefaultAsync<string?>(new CommandDefinition("SELECT nome_fantasia FROM estabelecimentos WHERE id = @Id", new { Id = lojaId }, cancellationToken: ct));
                return nome == null ? null : new AlvoMensagem("loja", lojaId, null, nome);
            }
            var empresa = await cx.QueryFirstOrDefaultAsync<string?>(new CommandDefinition("SELECT nome_empresa FROM crm_oportunidade WHERE id = @Id", new { Id = oportunidadeId }, cancellationToken: ct));
            return empresa == null ? null : new AlvoMensagem("oportunidade", null, oportunidadeId, empresa);
        }

        public async Task<IReadOnlyList<ContatoAlvo>> ContatosAsync(AlvoMensagem alvo, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            if (alvo.Tipo == "loja")
            {
                var linhas = await cx.QueryAsync<(int Id, string? Nome, string? Telefone)>(new CommandDefinition(@"
SELECT DISTINCT u.id, u.nome, u.telefone_e164
  FROM usuario u
  JOIN usuario_estabelecimentos ue ON ue.id_usuario = u.id
 WHERE ue.id_estabelecimento = @Loja AND u.deleted_at IS NULL
   AND COALESCE(ue.ativo, TRUE) = TRUE AND LOWER(COALESCE(ue.status, 'ativo')) = 'ativo'
   AND LOWER(COALESCE(ue.tipo_acesso, '')) <> 'motoboy'
 ORDER BY u.nome", new { Loja = alvo.LojaId }, cancellationToken: ct));
                return linhas.Select(l => new ContatoAlvo(l.Id.ToString(), l.Nome ?? $"Usuario {l.Id}", l.Telefone, GestaoMensagensRegras.TelefoneValido(l.Telefone))).ToList();
            }
            var lead = await cx.QueryFirstOrDefaultAsync<(string? Nome, string? Telefone)?>(new CommandDefinition(
                "SELECT COALESCE(NULLIF(decisor, ''), nome_empresa), whatsapp_e164 FROM crm_oportunidade WHERE id = @Id", new { Id = alvo.OportunidadeId }, cancellationToken: ct));
            return lead == null ? Array.Empty<ContatoAlvo>()
                : new[] { new ContatoAlvo("principal", lead.Value.Nome ?? alvo.Nome, lead.Value.Telefone, GestaoMensagensRegras.TelefoneValido(lead.Value.Telefone)) };
        }

        public async Task<IReadOnlyList<CanalGestao>> CanaisAsync(CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var gestao = (await cx.QueryAsync<(Guid Id, Guid CanalId, string Nome, string Finalidade, int? Dono, bool Ativo)>(new CommandDefinition(
                "SELECT id, canal_id, nome_exibicao, finalidade, dono_usuario_id, ativo FROM gestao_canal WHERE ativo ORDER BY finalidade = 'pessoal', nome_exibicao", cancellationToken: ct))).ToList();
            var remetentes = (await cx.QueryAsync<(Guid Gestao, int Usuario)>(new CommandDefinition(
                "SELECT gestao_canal_id, usuario_id FROM gestao_canal_remetente", cancellationToken: ct))).ToLookup(r => r.Gestao, r => r.Usuario);
            var resultado = new List<CanalGestao>();
            foreach (var g in gestao)
            {
                var canal = await _canais.ObterAsync(g.CanalId);
                if (canal == null) continue;
                // Numero da Zippy sem token proprio usa o token global: o que decide e o estado do canal, nao a presenca de token.
                var conectado = canal.Status == StatusCanal.Ativo;
                var estado = canal.Status + (string.IsNullOrWhiteSpace(canal.UltimoErro) ? string.Empty : $": {canal.UltimoErro}");
                resultado.Add(new CanalGestao(g.Id, g.CanalId, canal.PhoneNumberId, canal.NumeroE164, g.Nome, g.Finalidade, g.Dono, g.Ativo, conectado, estado, remetentes[g.Id].ToList()));
            }
            return resultado;
        }

        public async Task<DateTimeOffset?> UltimaEntradaAsync(Guid canalId, string telefoneChave, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var d = await cx.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT MAX(criado_em) FROM gestao_mensagem WHERE canal_id = @C AND telefone_chave = @T AND direcao = 'entrada'", new { C = canalId, T = telefoneChave }, cancellationToken: ct));
            return d.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(d.Value, DateTimeKind.Utc)) : null;
        }

        public async Task<IReadOnlyList<ModeloAprovado>> ModelosAprovadosAsync(Guid canalId, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var linhas = await cx.QueryAsync<(Guid Id, string Nome, string Idioma, string Categoria, string Conteudo)>(new CommandDefinition(@"
SELECT id, nome, idioma, COALESCE(categoria_meta, categoria), conteudo::text FROM whatsapp_template
 WHERE id_canal = @Canal AND situacao = 'enviado' AND upper(COALESCE(status_meta, '')) = 'APPROVED' ORDER BY nome", new { Canal = canalId }, cancellationToken: ct));
            return linhas.Select(l =>
            {
                var conteudo = JsonSerializer.Deserialize<TemplateConteudo>(l.Conteudo, Json);
                var botaoVariavel = conteudo?.Botoes?.Any(b => string.Equals(b.Tipo, TipoBotao.Url, StringComparison.OrdinalIgnoreCase) && b.Url?.Contains("{{") == true) == true;
                return new ModeloAprovado(l.Id, l.Nome, l.Idioma, l.Categoria, conteudo?.Corpo, GestaoMensagensRegras.ContarVariaveis(conteudo?.Corpo), botaoVariavel);
            }).ToList();
        }

        public async Task<bool> OptOutAsync(string telefoneChave, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS (SELECT 1 FROM gestao_contato_optout WHERE telefone_chave = @T)", new { T = telefoneChave }, cancellationToken: ct));
        }

        public async Task<MensagemRegistro?> PorChaveAsync(int autor, string chave, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.QueryFirstOrDefaultAsync<MensagemRegistro>(new CommandDefinition(
                $"SELECT {Colunas} FROM gestao_mensagem WHERE autor_usuario_id = @A AND chave_idempotencia = @C AND direcao = 'saida'", new { A = autor, C = chave }, cancellationToken: ct));
        }

        public async Task<bool> InserirAsync(MensagemRegistro m, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var linhas = await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO gestao_mensagem (id, direcao, via, canal_id, alvo_tipo, estabelecimento_id, oportunidade_id, contato_ref, telefone_e164, telefone_chave,
    autor_usuario_id, tipo_conteudo, texto, modelo_nome, modelo_idioma, modelo_categoria, variaveis, finalidade, estado, erro_codigo, erro_detalhe,
    referencia_externa, chave_idempotencia, chamado_id, enviado_em, criado_em)
VALUES (@Id, @Direcao, @Via, @CanalId, @AlvoTipo, @EstabelecimentoId, @OportunidadeId, @ContatoRef, @TelefoneE164, @TelefoneChave,
    @AutorUsuarioId, @TipoConteudo, @Texto, @ModeloNome, @ModeloIdioma, @ModeloCategoria, CAST(@VariaveisJson AS jsonb), @Finalidade, @Estado, @ErroCodigo, @ErroDetalhe,
    @ReferenciaExterna, @ChaveIdempotencia, @ChamadoId, @EnviadoEm, @CriadoEm)
ON CONFLICT DO NOTHING", Parametros(m), cancellationToken: ct));
            return linhas == 1;
        }

        private static object Parametros(MensagemRegistro m) => new
        {
            m.Id, m.Direcao, m.Via, m.CanalId, m.AlvoTipo, m.EstabelecimentoId, m.OportunidadeId, m.ContatoRef, m.TelefoneE164, m.TelefoneChave,
            m.AutorUsuarioId, m.TipoConteudo, m.Texto, m.ModeloNome, m.ModeloIdioma, m.ModeloCategoria, m.VariaveisJson, m.Finalidade, m.Estado,
            m.ErroCodigo, m.ErroDetalhe, m.ReferenciaExterna, m.ChaveIdempotencia, m.ChamadoId, EnviadoEm = m.EnviadoEm?.UtcDateTime, CriadoEm = m.CriadoEm.UtcDateTime
        };

        public async Task<MensagemRegistro?> ObterAsync(Guid id, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.QueryFirstOrDefaultAsync<MensagemRegistro>(new CommandDefinition($"SELECT {Colunas} FROM gestao_mensagem WHERE id = @Id", new { Id = id }, cancellationToken: ct));
        }

        public async Task<bool> AtualizarAsync(MensagemRegistro m, string? estadoEsperado, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE gestao_mensagem
   SET estado = @Estado, erro_codigo = @ErroCodigo, erro_detalhe = @ErroDetalhe, referencia_externa = @ReferenciaExterna, chamado_id = @ChamadoId,
       confirmado_por = @ConfirmadoPor, confirmado_em = @ConfirmadoEm, enviado_em = @EnviadoEm,
       entregue_em = CASE WHEN @Estado = 'entregue' THEN COALESCE(entregue_em, NOW()) ELSE entregue_em END,
       lido_em = CASE WHEN @Estado = 'lido' THEN COALESCE(lido_em, NOW()) ELSE lido_em END,
       atualizado_em = NOW()
 WHERE id = @Id AND (CAST(@Esperado AS text) IS NULL OR estado = @Esperado)",
                new { m.Id, m.Estado, m.ErroCodigo, m.ErroDetalhe, m.ReferenciaExterna, m.ChamadoId, m.ConfirmadoPor, ConfirmadoEm = m.ConfirmadoEm?.UtcDateTime,
                      EnviadoEm = m.EnviadoEm?.UtcDateTime, Esperado = estadoEsperado }, cancellationToken: ct)) == 1;
        }

        public async Task<IReadOnlyList<MensagemRegistro>> HistoricoAsync(Guid? lojaId, Guid? oportunidadeId, int limite, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return (await cx.QueryAsync<MensagemRegistro>(new CommandDefinition($@"
SELECT {Colunas} FROM gestao_mensagem
 WHERE (CAST(@Loja AS uuid) IS NOT NULL AND estabelecimento_id = @Loja) OR (CAST(@Op AS uuid) IS NOT NULL AND oportunidade_id = @Op)
 ORDER BY criado_em DESC LIMIT @Limite", new { Loja = lojaId, Op = oportunidadeId, Limite = limite }, cancellationToken: ct))).ToList();
        }

        public async Task<bool> CanalDaGestaoAsync(Guid canalId, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS (SELECT 1 FROM gestao_canal WHERE canal_id = @C AND ativo)", new { C = canalId }, cancellationToken: ct));
        }

        public async Task<MensagemRegistro?> UltimaSaidaAsync(Guid canalId, string telefoneChave, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.QueryFirstOrDefaultAsync<MensagemRegistro>(new CommandDefinition($@"
SELECT {Colunas} FROM gestao_mensagem
 WHERE canal_id = @C AND telefone_chave = @T AND direcao = 'saida' AND estado IN ('enviado', 'entregue', 'lido')
   AND (estabelecimento_id IS NOT NULL OR oportunidade_id IS NOT NULL)
 ORDER BY criado_em DESC LIMIT 1", new { C = canalId, T = telefoneChave }, cancellationToken: ct));
        }

        public async Task<MensagemRegistro?> PorReferenciaSaidaAsync(string referenciaExterna, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.QueryFirstOrDefaultAsync<MensagemRegistro>(new CommandDefinition(
                $"SELECT {Colunas} FROM gestao_mensagem WHERE referencia_externa = @R AND direcao = 'saida' LIMIT 1", new { R = referenciaExterna }, cancellationToken: ct));
        }
    }
}
