using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Model;
using APIBack.Repository.Interface;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Repository
{
    public class OficinaAgendamentoRepository : IOficinaAgendamentoRepository
    {
        private readonly string _connectionString;
        private static volatile bool _schemaEnsured;
        private static readonly SemaphoreSlim SchemaLock = new(1, 1);
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public OficinaAgendamentoRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
        }

        public async Task<Guid> CriarAsync(OficinaAgendamento agendamento)
        {
            await EnsureSchemaAsync();

            const string sql = @"
INSERT INTO oficina_agendamentos (
    id,
    id_estabelecimento,
    id_cliente,
    id_conversa,
    id_atendimento_servico,
    id_servico,
    id_profissional,
    nome_cliente,
    telefone_e164,
    nome_servico,
    veiculo_marca,
    veiculo_modelo,
    marca_peca,
    data_agendamento,
    hora_inicio,
    hora_fim,
    status,
    codigo,
    observacao,
    dados_extras,
    data_criacao,
    data_atualizacao,
    data_cancelamento)
VALUES (
    @Id,
    @IdEstabelecimento,
    @IdCliente,
    @IdConversa,
    @IdAtendimentoServico,
    @IdServico,
    @IdProfissional,
    @NomeCliente,
    @TelefoneE164,
    @NomeServico,
    @VeiculoMarca,
    @VeiculoModelo,
    @MarcaPeca,
    @DataAgendamento,
    @HoraInicio,
    @HoraFim,
    @Status,
    @Codigo,
    @Observacao,
    CAST(@DadosExtras AS jsonb),
    @DataCriacao,
    @DataAtualizacao,
    @DataCancelamento)
RETURNING id;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteScalarAsync<Guid>(sql, ToParameters(agendamento));
        }

        public async Task<OficinaAgendamento?> ObterPorIdAsync(Guid idEstabelecimento, Guid id)
        {
            await EnsureSchemaAsync();
            const string sql = SelectSql + " WHERE id_estabelecimento = @IdEstabelecimento AND id = @Id LIMIT 1;";
            await using var connection = new NpgsqlConnection(_connectionString);
            var row = await connection.QueryFirstOrDefaultAsync<Row>(sql, new { IdEstabelecimento = idEstabelecimento, Id = id });
            return Map(row);
        }

        public async Task<OficinaAgendamento?> ObterPorCodigoAsync(Guid idEstabelecimento, string codigo)
        {
            await EnsureSchemaAsync();
            const string sql = SelectSql + " WHERE id_estabelecimento = @IdEstabelecimento AND codigo = @Codigo LIMIT 1;";
            await using var connection = new NpgsqlConnection(_connectionString);
            var row = await connection.QueryFirstOrDefaultAsync<Row>(sql, new { IdEstabelecimento = idEstabelecimento, Codigo = codigo });
            return Map(row);
        }

        public async Task<IReadOnlyCollection<OficinaAgendamento>> ListarAtivosPorClienteAsync(Guid idEstabelecimento, Guid idCliente, string? telefoneE164)
        {
            await EnsureSchemaAsync();
            const string sql = SelectSql + @"
 WHERE id_estabelecimento = @IdEstabelecimento
   AND status IN ('confirmado', 'remarcado')
   AND data_agendamento >= CURRENT_DATE
   AND (id_cliente = @IdCliente OR (@TelefoneE164 IS NOT NULL AND telefone_e164 = @TelefoneE164))
 ORDER BY data_agendamento, hora_inicio;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<Row>(sql, new { IdEstabelecimento = idEstabelecimento, IdCliente = idCliente, TelefoneE164 = string.IsNullOrWhiteSpace(telefoneE164) ? null : telefoneE164 });
            return MapAll(rows);
        }

        public async Task<IReadOnlyCollection<OficinaAgendamento>> ListarPorConversaAsync(Guid idConversa)
        {
            await EnsureSchemaAsync();
            const string sql = SelectSql + " WHERE id_conversa = @IdConversa ORDER BY data_agendamento DESC, hora_inicio DESC;";
            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<Row>(sql, new { IdConversa = idConversa });
            return MapAll(rows);
        }

        public async Task<IReadOnlyCollection<OficinaAgendamento>> ListarPorPeriodoAsync(Guid idEstabelecimento, DateTime dataInicio, DateTime dataFim, string? status, long? idProfissional, Guid? idServico)
        {
            await EnsureSchemaAsync();
            const string sql = SelectSql + @"
 WHERE id_estabelecimento = @IdEstabelecimento
   AND data_agendamento BETWEEN @DataInicio AND @DataFim
   AND (@Status IS NULL OR status = @Status)
   AND (@IdProfissional IS NULL OR id_profissional = @IdProfissional)
   AND (@IdServico IS NULL OR id_servico = @IdServico)
 ORDER BY data_agendamento, hora_inicio;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<Row>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                DataInicio = dataInicio.Date,
                DataFim = dataFim.Date,
                Status = string.IsNullOrWhiteSpace(status) ? null : status,
                IdProfissional = idProfissional,
                IdServico = idServico
            });
            return MapAll(rows);
        }

        public async Task<int> ContarConflitosAsync(Guid idEstabelecimento, DateTime data, TimeSpan horaInicio, TimeSpan horaFim, long? idProfissional, Guid? ignorarId = null)
        {
            await EnsureSchemaAsync();
            const string sql = @"
SELECT COUNT(1)
  FROM oficina_agendamentos
 WHERE id_estabelecimento = @IdEstabelecimento
   AND data_agendamento = @DataAgendamento
   AND status IN ('confirmado', 'remarcado')
   AND (@IgnorarId IS NULL OR id <> @IgnorarId)
   AND (@IdProfissional IS NULL OR id_profissional = @IdProfissional)
   AND hora_fim > @HoraInicio
   AND @HoraFim > hora_inicio;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteScalarAsync<int>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                DataAgendamento = data.Date,
                HoraInicio = horaInicio,
                HoraFim = horaFim,
                IdProfissional = idProfissional,
                IgnorarId = ignorarId
            });
        }

        public async Task AtualizarAsync(OficinaAgendamento agendamento)
        {
            await EnsureSchemaAsync();
            const string sql = @"
UPDATE oficina_agendamentos
   SET id_atendimento_servico = @IdAtendimentoServico,
       id_servico = @IdServico,
       id_profissional = @IdProfissional,
       nome_cliente = @NomeCliente,
       telefone_e164 = @TelefoneE164,
       nome_servico = @NomeServico,
       veiculo_marca = @VeiculoMarca,
       veiculo_modelo = @VeiculoModelo,
       marca_peca = @MarcaPeca,
       data_agendamento = @DataAgendamento,
       hora_inicio = @HoraInicio,
       hora_fim = @HoraFim,
       status = @Status,
       observacao = @Observacao,
       dados_extras = CAST(@DadosExtras AS jsonb),
       data_atualizacao = @DataAtualizacao,
       data_cancelamento = @DataCancelamento
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @Id;";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, ToParameters(agendamento));
        }

        public async Task AtualizarStatusAsync(Guid idEstabelecimento, Guid id, string status, DateTime? dataCancelamento = null)
        {
            await EnsureSchemaAsync();
            const string sql = @"
UPDATE oficina_agendamentos
   SET status = @Status,
       data_cancelamento = COALESCE(@DataCancelamento, data_cancelamento),
       data_atualizacao = @DataAtualizacao
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @Id;";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Id = id,
                Status = status,
                DataCancelamento = dataCancelamento,
                DataAtualizacao = DateTime.UtcNow
            });
        }

        public async Task<string> GerarCodigoUnicoAsync(Guid idEstabelecimento)
        {
            await EnsureSchemaAsync();
            await using var connection = new NpgsqlConnection(_connectionString);

            for (var tentativa = 0; tentativa < 100; tentativa++)
            {
                var codigo = $"OF{DateTime.UtcNow:yyMMdd}{Random.Shared.Next(1000, 9999)}";
                var existe = await connection.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM oficina_agendamentos WHERE id_estabelecimento = @IdEstabelecimento AND codigo = @Codigo);",
                    new { IdEstabelecimento = idEstabelecimento, Codigo = codigo });

                if (!existe)
                {
                    return codigo;
                }
            }

            throw new InvalidOperationException("Nao foi possivel gerar codigo unico de agendamento de oficina.");
        }

        private async Task EnsureSchemaAsync()
        {
            if (_schemaEnsured)
            {
                return;
            }

            await SchemaLock.WaitAsync();
            try
            {
                if (_schemaEnsured)
                {
                    return;
                }

                const string sql = @"
CREATE TABLE IF NOT EXISTS oficina_agendamentos (
    id UUID PRIMARY KEY,
    id_estabelecimento UUID NOT NULL,
    id_cliente UUID NOT NULL,
    id_conversa UUID NOT NULL,
    id_atendimento_servico UUID NULL,
    id_servico UUID NULL,
    id_profissional BIGINT NULL,
    nome_cliente VARCHAR(160) NULL,
    telefone_e164 VARCHAR(32) NOT NULL DEFAULT '',
    nome_servico VARCHAR(160) NOT NULL,
    veiculo_marca VARCHAR(80) NULL,
    veiculo_modelo VARCHAR(120) NULL,
    marca_peca VARCHAR(120) NULL,
    data_agendamento DATE NOT NULL,
    hora_inicio TIME NOT NULL,
    hora_fim TIME NOT NULL,
    status VARCHAR(40) NOT NULL DEFAULT 'confirmado',
    codigo VARCHAR(32) NOT NULL,
    observacao TEXT NULL,
    dados_extras JSONB NOT NULL DEFAULT '{}'::jsonb,
    data_criacao TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    data_atualizacao TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    data_cancelamento TIMESTAMPTZ NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_oficina_agendamentos_estab_codigo
    ON oficina_agendamentos (id_estabelecimento, codigo);

CREATE INDEX IF NOT EXISTS ix_oficina_agendamentos_estab_data
    ON oficina_agendamentos (id_estabelecimento, data_agendamento, hora_inicio);

CREATE INDEX IF NOT EXISTS ix_oficina_agendamentos_cliente_status
    ON oficina_agendamentos (id_estabelecimento, id_cliente, status, data_agendamento);

CREATE INDEX IF NOT EXISTS ix_oficina_agendamentos_conversa
    ON oficina_agendamentos (id_conversa);";

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.ExecuteAsync(sql);
                _schemaEnsured = true;
            }
            finally
            {
                SchemaLock.Release();
            }
        }

        private static object ToParameters(OficinaAgendamento agendamento)
        {
            return new
            {
                agendamento.Id,
                agendamento.IdEstabelecimento,
                agendamento.IdCliente,
                agendamento.IdConversa,
                agendamento.IdAtendimentoServico,
                agendamento.IdServico,
                agendamento.IdProfissional,
                agendamento.NomeCliente,
                agendamento.TelefoneE164,
                agendamento.NomeServico,
                agendamento.VeiculoMarca,
                agendamento.VeiculoModelo,
                agendamento.MarcaPeca,
                DataAgendamento = agendamento.DataAgendamento.Date,
                agendamento.HoraInicio,
                agendamento.HoraFim,
                agendamento.Status,
                agendamento.Codigo,
                agendamento.Observacao,
                DadosExtras = JsonSerializer.Serialize(agendamento.DadosExtras ?? new Dictionary<string, object?>(), JsonOptions),
                agendamento.DataCriacao,
                agendamento.DataAtualizacao,
                agendamento.DataCancelamento
            };
        }

        private static IReadOnlyCollection<OficinaAgendamento> MapAll(IEnumerable<Row> rows)
        {
            var list = new List<OficinaAgendamento>();
            foreach (var row in rows)
            {
                var mapped = Map(row);
                if (mapped != null)
                {
                    list.Add(mapped);
                }
            }
            return list;
        }

        private static OficinaAgendamento? Map(Row? row)
        {
            if (row == null)
            {
                return null;
            }

            return new OficinaAgendamento
            {
                Id = row.Id,
                IdEstabelecimento = row.IdEstabelecimento,
                IdCliente = row.IdCliente,
                IdConversa = row.IdConversa,
                IdAtendimentoServico = row.IdAtendimentoServico,
                IdServico = row.IdServico,
                IdProfissional = row.IdProfissional,
                NomeCliente = row.NomeCliente,
                TelefoneE164 = row.TelefoneE164 ?? string.Empty,
                NomeServico = row.NomeServico ?? string.Empty,
                VeiculoMarca = row.VeiculoMarca,
                VeiculoModelo = row.VeiculoModelo,
                MarcaPeca = row.MarcaPeca,
                DataAgendamento = row.DataAgendamento,
                HoraInicio = row.HoraInicio,
                HoraFim = row.HoraFim,
                Status = row.Status ?? "confirmado",
                Codigo = row.Codigo ?? string.Empty,
                Observacao = row.Observacao,
                DadosExtras = Deserialize(row.DadosExtrasJson),
                DataCriacao = row.DataCriacao,
                DataAtualizacao = row.DataAtualizacao,
                DataCancelamento = row.DataCancelamento
            };
        }

        private static Dictionary<string, object?> Deserialize(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, JsonOptions)
                       ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private const string SelectSql = @"
SELECT id,
       id_estabelecimento AS IdEstabelecimento,
       id_cliente AS IdCliente,
       id_conversa AS IdConversa,
       id_atendimento_servico AS IdAtendimentoServico,
       id_servico AS IdServico,
       id_profissional AS IdProfissional,
       nome_cliente AS NomeCliente,
       telefone_e164 AS TelefoneE164,
       nome_servico AS NomeServico,
       veiculo_marca AS VeiculoMarca,
       veiculo_modelo AS VeiculoModelo,
       marca_peca AS MarcaPeca,
       data_agendamento AS DataAgendamento,
       hora_inicio AS HoraInicio,
       hora_fim AS HoraFim,
       status AS Status,
       codigo AS Codigo,
       observacao AS Observacao,
       dados_extras::text AS DadosExtrasJson,
       data_criacao AS DataCriacao,
       data_atualizacao AS DataAtualizacao,
       data_cancelamento AS DataCancelamento
  FROM oficina_agendamentos";

        private sealed class Row
        {
            public Guid Id { get; set; }
            public Guid IdEstabelecimento { get; set; }
            public Guid IdCliente { get; set; }
            public Guid IdConversa { get; set; }
            public Guid? IdAtendimentoServico { get; set; }
            public Guid? IdServico { get; set; }
            public long? IdProfissional { get; set; }
            public string? NomeCliente { get; set; }
            public string? TelefoneE164 { get; set; }
            public string? NomeServico { get; set; }
            public string? VeiculoMarca { get; set; }
            public string? VeiculoModelo { get; set; }
            public string? MarcaPeca { get; set; }
            public DateTime DataAgendamento { get; set; }
            public TimeSpan HoraInicio { get; set; }
            public TimeSpan HoraFim { get; set; }
            public string? Status { get; set; }
            public string? Codigo { get; set; }
            public string? Observacao { get; set; }
            public string? DadosExtrasJson { get; set; }
            public DateTime DataCriacao { get; set; }
            public DateTime DataAtualizacao { get; set; }
            public DateTime? DataCancelamento { get; set; }
        }
    }
}
