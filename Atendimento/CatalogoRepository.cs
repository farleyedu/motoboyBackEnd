using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Atendimento
{
    public interface ICatalogoRepository
    {
        Task<IReadOnlyList<ServicoCatalogoItem>> ListarCatalogoAsync();
        Task<IReadOnlyList<ServicoDoTipo>> ListarServicosDoTipoAsync(Guid tipoEstabelecimentoId);
        /// <summary>Tipo da loja; null quando a loja nao existe ou nao tem tipo.</summary>
        Task<Guid?> ObterTipoDaLojaAsync(Guid estabelecimentoId);
        Task<bool> LojaExisteAsync(Guid estabelecimentoId);
        Task<IReadOnlyList<ServicoDaLoja>> ListarServicosDaLojaAsync(Guid estabelecimentoId);

        /// <summary>
        /// Grava de uma vez (transacao): liga os servicos pedidos, desliga os demais, tira dos numeros da loja os
        /// servicos desligados e acrescenta os modulos exigidos (nunca remove modulo).
        /// </summary>
        Task DefinirServicosDaLojaAsync(
            Guid estabelecimentoId, IReadOnlyCollection<string> ativos, IReadOnlyCollection<string> modulosExigidos, int? usuarioId);

        /// <summary>Garante um modulo ligado na loja (ignora valores que o enum modulo_enum ainda nao conhece).</summary>
        Task GarantirModulosAsync(Guid estabelecimentoId, IReadOnlyCollection<string> modulos);
    }

    public sealed class SqlCatalogoRepository : ICatalogoRepository
    {
        private readonly string _connectionString;

        public SqlCatalogoRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
        }

        private sealed class CatalogoRow
        {
            public string Codigo { get; set; } = string.Empty;
            public string Nome { get; set; } = string.Empty;
            public string? Descricao { get; set; }
            public string[] ModulosExigidos { get; set; } = Array.Empty<string>();
            public int Ordem { get; set; }
            public bool Ativo { get; set; }
        }

        public async Task<IReadOnlyList<ServicoCatalogoItem>> ListarCatalogoAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<CatalogoRow>(@"
SELECT codigo AS Codigo, nome AS Nome, descricao AS Descricao, modulos_exigidos AS ModulosExigidos, ordem AS Ordem, ativo AS Ativo
  FROM servico_catalogo
 ORDER BY ordem, codigo;");
            return rows.Select(r => new ServicoCatalogoItem(r.Codigo, r.Nome, r.Descricao, r.ModulosExigidos, r.Ordem, r.Ativo)).ToList();
        }

        public async Task<IReadOnlyList<ServicoDoTipo>> ListarServicosDoTipoAsync(Guid tipoEstabelecimentoId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<(string Codigo, bool Padrao)>(@"
SELECT servico_codigo AS Codigo, padrao AS Padrao
  FROM tipo_servico
 WHERE id_tipo_estabelecimento = @Tipo
 ORDER BY servico_codigo;", new { Tipo = tipoEstabelecimentoId });
            return rows.Select(r => new ServicoDoTipo(r.Codigo, r.Padrao)).ToList();
        }

        public async Task<Guid?> ObterTipoDaLojaAsync(Guid estabelecimentoId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteScalarAsync<Guid?>(
                "SELECT id_tipo_estabelecimento FROM estabelecimentos WHERE id = @Id;", new { Id = estabelecimentoId });
        }

        public async Task<bool> LojaExisteAsync(Guid estabelecimentoId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM estabelecimentos WHERE id = @Id);", new { Id = estabelecimentoId });
        }

        public async Task<IReadOnlyList<ServicoDaLoja>> ListarServicosDaLojaAsync(Guid estabelecimentoId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<ServicoDaLoja>(@"
SELECT servico_codigo AS Codigo, ativo AS Ativo, config::text AS ConfigJson
  FROM estabelecimento_servico
 WHERE id_estabelecimento = @Id
 ORDER BY servico_codigo;", new { Id = estabelecimentoId });
            return rows.ToList();
        }

        public async Task DefinirServicosDaLojaAsync(
            Guid estabelecimentoId, IReadOnlyCollection<string> ativos, IReadOnlyCollection<string> modulosExigidos, int? usuarioId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();

            var codigos = ativos.ToArray();
            var parametros = new { Loja = estabelecimentoId, Ativos = codigos, Usuario = usuarioId };

            await connection.ExecuteAsync(@"
INSERT INTO estabelecimento_servico (id_estabelecimento, servico_codigo, ativo, alterado_por_usuario_id)
SELECT @Loja, s, TRUE, @Usuario FROM unnest(@Ativos::text[]) AS s
ON CONFLICT (id_estabelecimento, servico_codigo)
DO UPDATE SET ativo = TRUE, alterado_por_usuario_id = EXCLUDED.alterado_por_usuario_id, updated_at_utc = NOW();", parametros, tx);

            await connection.ExecuteAsync(@"
UPDATE estabelecimento_servico
   SET ativo = FALSE, alterado_por_usuario_id = @Usuario, updated_at_utc = NOW()
 WHERE id_estabelecimento = @Loja AND ativo AND servico_codigo <> ALL(@Ativos::text[]);", parametros, tx);

            await connection.ExecuteAsync(@"
DELETE FROM canal_servico cs
 USING canal_whatsapp c
 WHERE cs.id_canal = c.id AND c.id_estabelecimento = @Loja AND cs.servico_codigo <> ALL(@Ativos::text[]);", parametros, tx);

            if (modulosExigidos.Count > 0)
            {
                await AdicionarModulosAsync(connection, tx, estabelecimentoId, modulosExigidos);
            }

            await tx.CommitAsync();
        }

        public async Task GarantirModulosAsync(Guid estabelecimentoId, IReadOnlyCollection<string> modulos)
        {
            if (modulos.Count == 0) return;

            await using var connection = new NpgsqlConnection(_connectionString);
            await AdicionarModulosAsync(connection, null, estabelecimentoId, modulos);
        }

        // modulos_ativos e modulo_enum[]: um valor que o enum ainda nao conhece derrubaria o UPDATE, entao so entram os conhecidos.
        private static Task AdicionarModulosAsync(
            NpgsqlConnection connection, NpgsqlTransaction? tx, Guid estabelecimentoId, IReadOnlyCollection<string> modulos) =>
            connection.ExecuteAsync(@"
UPDATE estabelecimentos e
   SET modulos_ativos = (
           SELECT COALESCE(array_agg(DISTINCT m::modulo_enum), '{}'::modulo_enum[])
             FROM unnest(COALESCE(e.modulos_ativos::text[], '{}'::text[]) || @Modulos::text[]) AS t(m)
            WHERE upper(m) IN (SELECT upper(v) FROM unnest(enum_range(NULL::modulo_enum)::text[]) AS v)),
       data_atualizacao = NOW()
 WHERE e.id = @Id;", new { Id = estabelecimentoId, Modulos = modulos.Select(m => m.ToUpperInvariant()).ToArray() }, tx);
    }
}
