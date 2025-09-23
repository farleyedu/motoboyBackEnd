// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Dapper;
using Npgsql;

namespace APIBack.Automation.Infra
{
    public class SqlIARegraRepository : IIARegraRepository
    {
        private readonly string _connectionString;

        public SqlIARegraRepository(Microsoft.Extensions.Configuration.IConfiguration config)
        {
            _connectionString = config.GetConnectionString("DefaultConnection")
                                 ?? config["ConnectionStrings:DefaultConnection"]
                                 ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
        }

        public async Task<string?> ObterContextoAtivoAsync(Guid idEstabelecimento)
        {
            var prompts = await ObterPromptsCompostosAsync(idEstabelecimento, Array.Empty<string>());
            if (prompts.Estabelecimento.Count > 0)
            {
                return prompts.Estabelecimento[0];
            }

            return prompts.Gerais.Count > 0 ? prompts.Gerais[0] : null;
        }

        public async Task<int> CriarAsync(Guid idEstabelecimento, string contexto)
        {
            const string sql = @"INSERT INTO ia_regras (id_estabelecimento, tipo_prompt, nome_modulo, contexto, ativo, data_criacao, data_atualizacao)
                                 VALUES (@IdEstabelecimento, 'ESTABELECIMENTO', NULL, @Contexto, TRUE, NOW(), NOW())
                                 RETURNING id;";
            await using var cx = new NpgsqlConnection(_connectionString);
            var id = await cx.ExecuteScalarAsync<int>(sql, new { IdEstabelecimento = idEstabelecimento, Contexto = contexto });
            return id;
        }

        public async Task<bool> ExcluirAsync(Guid id)
        {
            const string sql = "DELETE FROM ia_regras WHERE id = @Id;";
            await using var cx = new NpgsqlConnection(_connectionString);
            var rows = await cx.ExecuteAsync(sql, new { Id = id });
            return rows > 0;
        }

        public async Task<IEnumerable<IARegra>> ListaregrasAsync(Guid idEstabelecimento)
        {
            const string sql = @"SELECT id                               AS ""Id"",
                                         id_estabelecimento               AS ""IdEstabelecimento"",
                                         contexto                         AS ""Contexto"",
                                         ativo                            AS ""Ativo"",
                                         data_criacao                     AS ""DataCriacao"",
                                         data_atualizacao                 AS ""DataAtualizacao"",
                                         modulo::text                     AS ""Modulo""
                                    FROM ia_regras
                                   WHERE id_estabelecimento = @IdEstabelecimento
                                   ORDER BY data_atualizacao DESC, data_criacao DESC;";
            await using var cx = new NpgsqlConnection(_connectionString);
            return await cx.QueryAsync<IARegra>(sql, new { IdEstabelecimento = idEstabelecimento });
        }

        public async Task<(IReadOnlyList<string> Gerais, IReadOnlyList<string> Modulos, IReadOnlyList<string> Estabelecimento)> ObterPromptsCompostosAsync(Guid idEstabelecimento, IReadOnlyCollection<string>? modulosAtivos)
        {
            var moduloArray = (modulosAtivos ?? Array.Empty<string>())
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            const string sql = @"SELECT r.id                               AS ""Id"",
                                         r.id_estabelecimento               AS ""IdEstabelecimento"",
                                         r.contexto                         AS ""Contexto"",
                                         r.ativo                            AS ""Ativo"",
                                         r.data_criacao                     AS ""DataCriacao"",
                                         r.data_atualizacao                 AS ""DataAtualizacao"",
                                         r.modulo::text                     AS ""Modulo""
                                    FROM ia_regras r
                                   WHERE r.ativo = TRUE
                                     AND (
                                          -- Prompts GERAIS (globais, sem estabelecimento específico)
                                          (r.modulo = 'GERAL' AND r.id_estabelecimento IS NULL)
                                          OR 
                                          -- Prompts MODULO (globais OU específicos do estabelecimento, para módulos ativos)
                                          (r.modulo != 'GERAL' AND r.modulo != 'ESTABELECIMENTO'
                                           AND (r.id_estabelecimento IS NULL OR r.id_estabelecimento = @IdEstabelecimento)
                                           AND @ModuloCount > 0
                                           AND r.modulo::text = ANY(@Modulos))
                                          OR 
                                          -- Prompts ESTABELECIMENTO (específicos do estabelecimento)
                                          (r.modulo = 'ESTABELECIMENTO'
                                           AND r.id_estabelecimento = @IdEstabelecimento)
                                         )
                                   ORDER BY r.data_atualizacao DESC, r.data_criacao DESC;";

            await using var cx = new NpgsqlConnection(_connectionString);
            var todos = await cx.QueryAsync<IARegra>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Modulos = moduloArray,
                ModuloCount = moduloArray.Length
            });

            var ordered = todos.ToList();
            var gerais = new List<string>();
            var modulos = new List<string>();
            var estabelecimento = new List<string>();

            foreach (var regra in ordered)
            {
                var modulo = regra.Modulo?.ToUpperInvariant() ?? "GERAL";
                switch (modulo)
                {
                    case "GERAL":
                        gerais.Add(regra.Contexto);
                        break;
                    case "ESTABELECIMENTO":
                        estabelecimento.Add(regra.Contexto);
                        break;
                    default:
                        // Qualquer outro módulo (RESERVA, DELIVERY, BARBEARIA, etc.)
                        modulos.Add(regra.Contexto);
                        break;
                }
            }

            return (gerais, modulos, estabelecimento);
        }


    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================


