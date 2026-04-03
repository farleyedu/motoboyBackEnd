using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Automation.Infra
{
    public class SqlServicoAtendimentoRepository : IServicoAtendimentoRepository
    {
        private readonly string _connectionString;
        private static volatile bool _schemaEnsured;
        private static readonly SemaphoreSlim SchemaLock = new(1, 1);
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public SqlServicoAtendimentoRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
        }

        public async Task<ServicoAtendimento?> ObterAbertoAsync(Guid idEstabelecimento, string telefoneE164)
        {
            if (idEstabelecimento == Guid.Empty || string.IsNullOrWhiteSpace(telefoneE164))
            {
                return null;
            }

            await EnsureSchemaAsync();

            const string sql = @"
SELECT id,
       id_estabelecimento AS IdEstabelecimento,
       id_conversa        AS IdConversa,
       id_cliente         AS IdCliente,
       telefone_e164      AS TelefoneE164,
       nome_cliente       AS NomeCliente,
       intencao_principal AS IntencaoPrincipal,
       intencao_detalhe   AS IntencaoDetalhe,
       id_servico         AS IdServico,
       nome_servico       AS NomeServico,
       categoria_servico  AS CategoriaServico,
       resumo_atendimento AS ResumoAtendimento,
       status             AS Status,
       etapa_atual        AS EtapaAtual,
       ultima_pergunta    AS UltimaPergunta,
       dados_extras::text AS DadosExtrasJson,
       via_numero_central AS ViaNumeroCentral,
       data_handover      AS DataHandover,
       data_conclusao     AS DataConclusao,
       data_criacao       AS DataCriacao,
       data_atualizacao   AS DataAtualizacao
  FROM cliente_servicos
 WHERE id_estabelecimento = @IdEstabelecimento
   AND telefone_e164 = @TelefoneE164
   AND status IN ('em_triagem', 'aguardando_cliente', 'aguardando_interno', 'em_andamento')
 ORDER BY data_atualizacao DESC
 LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var row = await connection.QueryFirstOrDefaultAsync<ServicoAtendimentoRow>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                TelefoneE164 = telefoneE164
            });

            return Map(row);
        }

        public async Task<ServicoAtendimento?> ObterPorConversaAsync(Guid idConversa)
        {
            if (idConversa == Guid.Empty)
            {
                return null;
            }

            await EnsureSchemaAsync();

            const string sql = @"
SELECT id,
       id_estabelecimento AS IdEstabelecimento,
       id_conversa        AS IdConversa,
       id_cliente         AS IdCliente,
       telefone_e164      AS TelefoneE164,
       nome_cliente       AS NomeCliente,
       intencao_principal AS IntencaoPrincipal,
       intencao_detalhe   AS IntencaoDetalhe,
       id_servico         AS IdServico,
       nome_servico       AS NomeServico,
       categoria_servico  AS CategoriaServico,
       resumo_atendimento AS ResumoAtendimento,
       status             AS Status,
       etapa_atual        AS EtapaAtual,
       ultima_pergunta    AS UltimaPergunta,
       dados_extras::text AS DadosExtrasJson,
       via_numero_central AS ViaNumeroCentral,
       data_handover      AS DataHandover,
       data_conclusao     AS DataConclusao,
       data_criacao       AS DataCriacao,
       data_atualizacao   AS DataAtualizacao
  FROM cliente_servicos
 WHERE id_conversa = @IdConversa
 ORDER BY data_atualizacao DESC
 LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var row = await connection.QueryFirstOrDefaultAsync<ServicoAtendimentoRow>(sql, new { IdConversa = idConversa });
            return Map(row);
        }

        public async Task<Guid> CriarAsync(ServicoAtendimento atendimento)
        {
            ArgumentNullException.ThrowIfNull(atendimento);

            await EnsureSchemaAsync();

            const string sql = @"
INSERT INTO cliente_servicos (
    id,
    id_estabelecimento,
    id_conversa,
    id_cliente,
    telefone_e164,
    nome_cliente,
    intencao_principal,
    intencao_detalhe,
    id_servico,
    nome_servico,
    categoria_servico,
    resumo_atendimento,
    status,
    etapa_atual,
    ultima_pergunta,
    dados_extras,
    via_numero_central,
    data_handover,
    data_conclusao,
    data_criacao,
    data_atualizacao
) VALUES (
    @Id,
    @IdEstabelecimento,
    @IdConversa,
    @IdCliente,
    @TelefoneE164,
    @NomeCliente,
    @IntencaoPrincipal,
    @IntencaoDetalhe,
    @IdServico,
    @NomeServico,
    @CategoriaServico,
    @ResumoAtendimento,
    @Status,
    @EtapaAtual,
    @UltimaPergunta,
    CAST(@DadosExtras AS jsonb),
    @ViaNumeroCentral,
    @DataHandover,
    @DataConclusao,
    @DataCriacao,
    @DataAtualizacao
);";

            if (atendimento.Id == Guid.Empty)
            {
                atendimento.Id = Guid.NewGuid();
            }

            var agora = DateTime.UtcNow;
            if (atendimento.DataCriacao == default)
            {
                atendimento.DataCriacao = agora;
            }

            atendimento.DataAtualizacao = agora;

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, ToParameters(atendimento));
            return atendimento.Id;
        }

        public async Task AtualizarAsync(ServicoAtendimento atendimento)
        {
            ArgumentNullException.ThrowIfNull(atendimento);

            await EnsureSchemaAsync();

            const string sql = @"
UPDATE cliente_servicos
   SET id_conversa = @IdConversa,
       id_cliente = @IdCliente,
       telefone_e164 = @TelefoneE164,
       nome_cliente = @NomeCliente,
       intencao_principal = @IntencaoPrincipal,
       intencao_detalhe = @IntencaoDetalhe,
       id_servico = @IdServico,
       nome_servico = @NomeServico,
       categoria_servico = @CategoriaServico,
       resumo_atendimento = @ResumoAtendimento,
       status = @Status,
       etapa_atual = @EtapaAtual,
       ultima_pergunta = @UltimaPergunta,
       dados_extras = CAST(@DadosExtras AS jsonb),
       via_numero_central = @ViaNumeroCentral,
       data_handover = @DataHandover,
       data_conclusao = @DataConclusao,
       data_atualizacao = @DataAtualizacao
 WHERE id = @Id;";

            atendimento.DataAtualizacao = DateTime.UtcNow;

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, ToParameters(atendimento));
        }

        public async Task AtualizarExtrasAsync(Guid id, IReadOnlyDictionary<string, object?> dadosExtras)
        {
            if (id == Guid.Empty || dadosExtras == null || dadosExtras.Count == 0)
            {
                return;
            }

            await EnsureSchemaAsync();

            const string sql = @"
UPDATE cliente_servicos
   SET dados_extras = COALESCE(dados_extras, '{}'::jsonb) || CAST(@DadosExtras AS jsonb),
       data_atualizacao = NOW()
 WHERE id = @Id;";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, new
            {
                Id = id,
                DadosExtras = JsonSerializer.Serialize(dadosExtras, JsonOptions)
            });
        }

        public async Task AtualizarStatusAsync(Guid id, string status)
        {
            if (id == Guid.Empty || string.IsNullOrWhiteSpace(status))
            {
                return;
            }

            await EnsureSchemaAsync();

            const string sql = @"
UPDATE cliente_servicos
   SET status = @Status,
       data_handover = CASE
           WHEN @Status = 'aguardando_interno' THEN COALESCE(data_handover, NOW())
           ELSE data_handover
       END,
       data_conclusao = CASE
           WHEN @Status IN ('concluido', 'cancelado') THEN COALESCE(data_conclusao, NOW())
           ELSE data_conclusao
       END,
       data_atualizacao = NOW()
 WHERE id = @Id;";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, new { Id = id, Status = status.Trim().ToLowerInvariant() });
        }

        public Task ConcluirAsync(Guid id, string status)
        {
            return AtualizarStatusAsync(id, status);
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
CREATE TABLE IF NOT EXISTS cliente_servicos (
    id UUID PRIMARY KEY,
    id_estabelecimento UUID NOT NULL,
    id_conversa UUID NOT NULL,
    id_cliente UUID NOT NULL,
    telefone_e164 VARCHAR(20) NOT NULL,
    nome_cliente VARCHAR(160) NULL,
    intencao_principal VARCHAR(60) NOT NULL DEFAULT 'duvida_servicos',
    intencao_detalhe VARCHAR(60) NULL,
    id_servico UUID NULL,
    nome_servico VARCHAR(160) NULL,
    categoria_servico VARCHAR(120) NULL,
    resumo_atendimento TEXT NULL,
    status VARCHAR(60) NOT NULL DEFAULT 'em_triagem',
    etapa_atual VARCHAR(120) NULL,
    ultima_pergunta TEXT NULL,
    dados_extras JSONB NOT NULL DEFAULT '{}'::jsonb,
    via_numero_central BOOLEAN NOT NULL DEFAULT FALSE,
    data_handover TIMESTAMPTZ NULL,
    data_conclusao TIMESTAMPTZ NULL,
    data_criacao TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    data_atualizacao TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

ALTER TABLE cliente_servicos ADD COLUMN IF NOT EXISTS nome_cliente VARCHAR(160);
ALTER TABLE cliente_servicos ADD COLUMN IF NOT EXISTS intencao_principal VARCHAR(60) NOT NULL DEFAULT 'duvida_servicos';
ALTER TABLE cliente_servicos ADD COLUMN IF NOT EXISTS intencao_detalhe VARCHAR(60);
ALTER TABLE cliente_servicos ADD COLUMN IF NOT EXISTS id_servico UUID NULL;
ALTER TABLE cliente_servicos ADD COLUMN IF NOT EXISTS nome_servico VARCHAR(160);
ALTER TABLE cliente_servicos ADD COLUMN IF NOT EXISTS categoria_servico VARCHAR(120);
ALTER TABLE cliente_servicos ADD COLUMN IF NOT EXISTS resumo_atendimento TEXT;
ALTER TABLE cliente_servicos ADD COLUMN IF NOT EXISTS status VARCHAR(60) NOT NULL DEFAULT 'em_triagem';
ALTER TABLE cliente_servicos ADD COLUMN IF NOT EXISTS etapa_atual VARCHAR(120);
ALTER TABLE cliente_servicos ADD COLUMN IF NOT EXISTS ultima_pergunta TEXT;
ALTER TABLE cliente_servicos ADD COLUMN IF NOT EXISTS dados_extras JSONB NOT NULL DEFAULT '{}'::jsonb;
ALTER TABLE cliente_servicos ADD COLUMN IF NOT EXISTS via_numero_central BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE cliente_servicos ADD COLUMN IF NOT EXISTS data_handover TIMESTAMPTZ NULL;
ALTER TABLE cliente_servicos ADD COLUMN IF NOT EXISTS data_conclusao TIMESTAMPTZ NULL;
ALTER TABLE cliente_servicos ADD COLUMN IF NOT EXISTS data_criacao TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE cliente_servicos ADD COLUMN IF NOT EXISTS data_atualizacao TIMESTAMPTZ NOT NULL DEFAULT NOW();

UPDATE cliente_servicos
   SET dados_extras = '{}'::jsonb
 WHERE dados_extras IS NULL;

CREATE INDEX IF NOT EXISTS ix_cliente_servicos_conversa
    ON cliente_servicos (id_conversa);

CREATE INDEX IF NOT EXISTS ix_cliente_servicos_cliente
    ON cliente_servicos (id_cliente);

CREATE INDEX IF NOT EXISTS ix_cliente_servicos_estab_status_updated
    ON cliente_servicos (id_estabelecimento, status, data_atualizacao DESC);

CREATE INDEX IF NOT EXISTS ix_cliente_servicos_estab_tel_aberto
    ON cliente_servicos (id_estabelecimento, telefone_e164, data_atualizacao DESC);";

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.ExecuteAsync(sql);
                _schemaEnsured = true;
            }
            finally
            {
                SchemaLock.Release();
            }
        }

        private static object ToParameters(ServicoAtendimento atendimento)
        {
            return new
            {
                atendimento.Id,
                atendimento.IdEstabelecimento,
                atendimento.IdConversa,
                atendimento.IdCliente,
                atendimento.TelefoneE164,
                atendimento.NomeCliente,
                atendimento.IntencaoPrincipal,
                atendimento.IntencaoDetalhe,
                atendimento.IdServico,
                atendimento.NomeServico,
                atendimento.CategoriaServico,
                atendimento.ResumoAtendimento,
                atendimento.Status,
                atendimento.EtapaAtual,
                atendimento.UltimaPergunta,
                DadosExtras = SerializeExtras(atendimento.DadosExtras),
                atendimento.ViaNumeroCentral,
                atendimento.DataHandover,
                atendimento.DataConclusao,
                atendimento.DataCriacao,
                atendimento.DataAtualizacao
            };
        }

        private static ServicoAtendimento? Map(ServicoAtendimentoRow? row)
        {
            if (row == null)
            {
                return null;
            }

            return new ServicoAtendimento
            {
                Id = row.Id,
                IdEstabelecimento = row.IdEstabelecimento,
                IdConversa = row.IdConversa,
                IdCliente = row.IdCliente,
                TelefoneE164 = row.TelefoneE164 ?? string.Empty,
                NomeCliente = row.NomeCliente,
                IntencaoPrincipal = string.IsNullOrWhiteSpace(row.IntencaoPrincipal) ? "duvida_servicos" : row.IntencaoPrincipal!,
                IntencaoDetalhe = row.IntencaoDetalhe,
                IdServico = row.IdServico,
                NomeServico = row.NomeServico,
                CategoriaServico = row.CategoriaServico,
                ResumoAtendimento = row.ResumoAtendimento,
                Status = string.IsNullOrWhiteSpace(row.Status) ? "em_triagem" : row.Status!,
                EtapaAtual = row.EtapaAtual,
                UltimaPergunta = row.UltimaPergunta,
                DadosExtras = DeserializeExtras(row.DadosExtrasJson),
                ViaNumeroCentral = row.ViaNumeroCentral,
                DataHandover = row.DataHandover,
                DataConclusao = row.DataConclusao,
                DataCriacao = row.DataCriacao,
                DataAtualizacao = row.DataAtualizacao
            };
        }

        private static string SerializeExtras(IReadOnlyDictionary<string, object?> dadosExtras)
        {
            return JsonSerializer.Serialize(dadosExtras ?? new Dictionary<string, object?>(), JsonOptions);
        }

        private static Dictionary<string, object?> DeserializeExtras(string? json)
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

        private sealed class ServicoAtendimentoRow
        {
            public Guid Id { get; set; }
            public Guid IdEstabelecimento { get; set; }
            public Guid IdConversa { get; set; }
            public Guid IdCliente { get; set; }
            public string? TelefoneE164 { get; set; }
            public string? NomeCliente { get; set; }
            public string? IntencaoPrincipal { get; set; }
            public string? IntencaoDetalhe { get; set; }
            public Guid? IdServico { get; set; }
            public string? NomeServico { get; set; }
            public string? CategoriaServico { get; set; }
            public string? ResumoAtendimento { get; set; }
            public string? Status { get; set; }
            public string? EtapaAtual { get; set; }
            public string? UltimaPergunta { get; set; }
            public string? DadosExtrasJson { get; set; }
            public bool ViaNumeroCentral { get; set; }
            public DateTime? DataHandover { get; set; }
            public DateTime? DataConclusao { get; set; }
            public DateTime DataCriacao { get; set; }
            public DateTime DataAtualizacao { get; set; }
        }
    }
}
