using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Atendimento.Templates
{
    public interface ITemplateRepository
    {
        /// <summary>
        /// Modelos da loja na WABA do numero. Sem WABA conhecida (numero da Zippy ainda nao confirmado nesta execucao), os modelos
        /// criados por este numero e os da mesma WABA deles.
        /// </summary>
        Task<IReadOnlyList<TemplateWhatsapp>> ListarAsync(Guid estabelecimentoId, Guid canalId, string? wabaId);
        Task<TemplateWhatsapp?> ObterAsync(Guid id);
        /// <summary>Todos os modelos do Zippy nesta WABA, de qualquer loja e situacao (menos excluidos): base da regra de isolamento.</summary>
        Task<IReadOnlyList<TemplateWhatsapp>> ListarDaWabaAsync(string wabaId);
        /// <exception cref="TemplateConflitoException">Nome + idioma (ou ID da Meta) ja usado nesta WABA.</exception>
        Task InserirAsync(TemplateWhatsapp template);
        /// <summary>Grava o estado vindo da Meta e o estado local. Nunca muda loja, WABA, nome, idioma ou conteudo enviado.</summary>
        /// <exception cref="TemplateConflitoException">O ID da Meta ja pertence a outro modelo do Zippy.</exception>
        Task AtualizarAsync(TemplateWhatsapp template);
        Task RegistrarEventoAsync(Guid templateId, string acao, int? usuarioId, string? detalheJson);
        Task<IReadOnlyList<TemplateEvento>> ListarEventosAsync(Guid templateId);
    }

    public sealed class SqlTemplateRepository : ITemplateRepository
    {
        private const string Colunas = @"
       id AS Id, id_estabelecimento AS IdEstabelecimento, id_canal AS IdCanal, waba_id AS WabaId, meta_template_id AS MetaTemplateId,
       nome AS Nome, idioma AS Idioma, categoria AS Categoria, categoria_meta AS CategoriaMeta, conteudo::text AS ConteudoJson,
       componentes_meta::text AS ComponentesMetaJson, situacao AS Situacao, status_meta AS StatusMeta, motivo_rejeicao AS MotivoRejeicao,
       qualidade AS Qualidade, ultimo_erro AS UltimoErro, ultimo_erro_codigo AS UltimoErroCodigo, criado_por AS CriadoPor,
       enviado_em AS EnviadoEm, status_alterado_em AS StatusAlteradoEm, sincronizado_em AS SincronizadoEm, excluido_em AS ExcluidoEm,
       created_at_utc AS CriadoEm";

        private readonly string _connectionString;

        public SqlTemplateRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
        }

        public async Task<IReadOnlyList<TemplateWhatsapp>> ListarAsync(Guid estabelecimentoId, Guid canalId, string? wabaId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            return (await connection.QueryAsync<TemplateWhatsapp>($@"
SELECT {Colunas}
  FROM whatsapp_template
 WHERE id_estabelecimento = @Loja
   AND (id_canal = @Canal
        OR waba_id = CAST(@Waba AS text)
        OR (CAST(@Waba AS text) IS NULL AND waba_id IN (SELECT waba_id FROM whatsapp_template WHERE id_canal = @Canal AND id_estabelecimento = @Loja)))
 ORDER BY created_at_utc DESC
 LIMIT 500;", new { Loja = estabelecimentoId, Canal = canalId, Waba = wabaId })).ToList();
        }

        public async Task<TemplateWhatsapp?> ObterAsync(Guid id)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QuerySingleOrDefaultAsync<TemplateWhatsapp>($"SELECT {Colunas} FROM whatsapp_template WHERE id = @Id;", new { Id = id });
        }

        public async Task<IReadOnlyList<TemplateWhatsapp>> ListarDaWabaAsync(string wabaId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            return (await connection.QueryAsync<TemplateWhatsapp>(
                $"SELECT {Colunas} FROM whatsapp_template WHERE waba_id = @Waba AND situacao <> 'excluido' ORDER BY created_at_utc;",
                new { Waba = wabaId })).ToList();
        }

        public async Task InserirAsync(TemplateWhatsapp t)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            try
            {
                await connection.ExecuteAsync(@"
INSERT INTO whatsapp_template (
    id, id_estabelecimento, id_canal, waba_id, meta_template_id, nome, idioma, categoria, categoria_meta, conteudo, componentes_meta,
    situacao, status_meta, motivo_rejeicao, qualidade, ultimo_erro, ultimo_erro_codigo, criado_por, enviado_em, status_alterado_em,
    sincronizado_em, excluido_em)
VALUES (
    @Id, @IdEstabelecimento, @IdCanal, @WabaId, @MetaTemplateId, @Nome, @Idioma, @Categoria, @CategoriaMeta, CAST(@ConteudoJson AS jsonb),
    CAST(@ComponentesMetaJson AS jsonb), @Situacao, @StatusMeta, @MotivoRejeicao, @Qualidade, @UltimoErro, @UltimoErroCodigo, @CriadoPor,
    @EnviadoEm, @StatusAlteradoEm, @SincronizadoEm, @ExcluidoEm);", t);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new TemplateConflitoException("Ja existe um modelo com este nome e idioma nesta conta.");
            }
        }

        public async Task AtualizarAsync(TemplateWhatsapp t)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            try
            {
                await connection.ExecuteAsync(@"
UPDATE whatsapp_template
   SET meta_template_id = @MetaTemplateId, categoria_meta = @CategoriaMeta, componentes_meta = CAST(@ComponentesMetaJson AS jsonb),
       situacao = @Situacao, status_meta = @StatusMeta, motivo_rejeicao = @MotivoRejeicao, qualidade = @Qualidade,
       ultimo_erro = @UltimoErro, ultimo_erro_codigo = @UltimoErroCodigo, enviado_em = @EnviadoEm,
       status_alterado_em = @StatusAlteradoEm, sincronizado_em = @SincronizadoEm, excluido_em = @ExcluidoEm, updated_at_utc = NOW()
 WHERE id = @Id;", t);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new TemplateConflitoException("O modelo da Meta ja esta vinculado a outro cadastro.");
            }
        }

        public async Task RegistrarEventoAsync(Guid templateId, string acao, int? usuarioId, string? detalheJson)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(@"
INSERT INTO whatsapp_template_evento (id_template, acao, usuario_id, detalhe)
VALUES (@Template, @Acao, @Usuario, CAST(@Detalhe AS jsonb));",
                new { Template = templateId, Acao = acao, Usuario = usuarioId, Detalhe = detalheJson });
        }

        public async Task<IReadOnlyList<TemplateEvento>> ListarEventosAsync(Guid templateId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            return (await connection.QueryAsync<TemplateEvento>(@"
SELECT acao AS Acao, usuario_id AS UsuarioId, detalhe::text AS DetalheJson, created_at_utc AS CriadoEm
  FROM whatsapp_template_evento
 WHERE id_template = @Template
 ORDER BY id DESC
 LIMIT 200;", new { Template = templateId })).ToList();
        }
    }
}
