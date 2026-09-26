using System;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Clientes;
using APIBack.Repository.Interface;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    /// <summary>
    /// Cadastro de clientes do estabelecimento sobre a tabela CLIENTES (a mesma que o WhatsApp preenche
    /// com nome + telefone_e164 e que as conversas referenciam por id_cliente; colunas extras na
    /// migration 20260929_02). Nao cria linha paralela: o cliente cadastrado e o do chat.
    /// </summary>
    public sealed class ClienteCadastroRepository : IClienteCadastroRepository
    {
        private const string UniqueViolation = "23505";

        private readonly NpgsqlDataSource _dataSource;

        public ClienteCadastroRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        private const string Columns = @"
       id AS Id, COALESCE(nome, '') AS Nome, telefone_e164 AS Telefone, email AS Email, observacoes AS Observacoes,
       cep AS Cep, logradouro AS Logradouro, numero AS Numero, complemento AS Complemento,
       bairro AS Bairro, cidade AS Cidade, uf AS Uf, latitude AS Latitude, longitude AS Longitude,
       ativo AS Ativo, simulado AS Simulado, avatar AS Avatar, cpf AS Cpf, data_nascimento::text AS DataNascimento,
       referencia AS Referencia, canal_preferido AS CanalPreferido, origem AS Origem, tags AS Tags,
       consentimento_whatsapp AS ConsentimentoWhatsapp, data_criacao AS CriadoEm, data_atualizacao AS AtualizadoEm";

        public async Task<ClienteListaDto> ListAsync(Guid estabelecimentoId, string? q, bool incluirInativos, int page, int pageSize)
        {
            var (safePage, safeSize) = ClienteRules.ClampPaging(page, pageSize);
            var term = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
            var digits = term == null ? string.Empty : new string(term.Where(char.IsDigit).ToArray());

            // Filtro so com condicoes fixas; o texto do usuario vai sempre por parametro.
            const string where = @"
 WHERE id_estabelecimento = @EstabelecimentoId
   AND (@IncluirInativos OR ativo = TRUE)
   AND (@Term IS NULL
        OR COALESCE(nome, '') ILIKE '%' || @Term || '%'
        OR COALESCE(email, '') ILIKE '%' || @Term || '%'
        OR COALESCE(bairro, '') ILIKE '%' || @Term || '%'
        OR (@Digits <> '' AND COALESCE(telefone_e164, '') LIKE '%' || @Digits || '%'))";
            var parameters = new
            {
                EstabelecimentoId = estabelecimentoId,
                IncluirInativos = incluirInativos,
                Term = term,
                Digits = digits,
                Offset = (safePage - 1) * safeSize,
                Limit = safeSize
            };

            await using var connection = await _dataSource.OpenConnectionAsync();
            var total = await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM clientes{where};", parameters);
            var items = (await connection.QueryAsync<ClienteDto>(
                $"SELECT{Columns} FROM clientes{where} ORDER BY lower(COALESCE(nome, telefone_e164, '')), id LIMIT @Limit OFFSET @Offset;",
                parameters)).ToList();
            return new ClienteListaDto { Itens = items, Total = total, Page = safePage, PageSize = safeSize };
        }

        public async Task<ClienteDto?> GetAsync(Guid estabelecimentoId, Guid clienteId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.QuerySingleOrDefaultAsync<ClienteDto>(
                $"SELECT{Columns} FROM clientes WHERE id = @Id AND id_estabelecimento = @EstabelecimentoId;",
                new { Id = clienteId, EstabelecimentoId = estabelecimentoId });
        }

        public async Task<ClienteDto> CreateAsync(Guid estabelecimentoId, int actorUserId, ClienteInput input)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                await LockPhoneAsync(connection, transaction, estabelecimentoId, input.TelefoneE164);

                // O WhatsApp cria a linha (so telefone) na primeira mensagem: cadastrar esse telefone
                // COMPLETA a linha em vez de duplicar, e reativa um cliente excluido antes.
                var existing = await connection.QueryFirstOrDefaultAsync<(Guid Id, bool Ativo, string? Nome)?>(@"
SELECT id, ativo, nome FROM clientes
 WHERE id_estabelecimento = @EstabelecimentoId AND telefone_e164 = @Telefone
 ORDER BY ativo DESC, data_criacao
 LIMIT 1;", new { EstabelecimentoId = estabelecimentoId, Telefone = input.TelefoneE164 }, transaction);

                if (existing is { Ativo: true } && !string.IsNullOrWhiteSpace(existing.Value.Nome))
                {
                    throw PhoneTaken();
                }

                ClienteDto saved;
                if (existing != null)
                {
                    saved = await connection.QuerySingleAsync<ClienteDto>($@"
UPDATE clientes
   SET nome = @Nome, email = @Email, observacoes = @Observacoes, cep = @Cep, logradouro = @Logradouro,
       numero = @Numero, complemento = @Complemento, bairro = @Bairro, cidade = @Cidade, uf = @Uf,
       latitude = @Latitude, longitude = @Longitude, simulado = @Simulado, avatar = @Avatar, cpf = @Cpf,
       data_nascimento = @DataNascimento::date, referencia = @Referencia, canal_preferido = @CanalPreferido,
       origem = @Origem, tags = @Tags, consentimento_whatsapp = @ConsentimentoWhatsapp, ativo = @Ativo, data_atualizacao = NOW()
 WHERE id = @Id
RETURNING{Columns};", Parameters(estabelecimentoId, existing.Value.Id, input, actorUserId), transaction);
                }
                else
                {
                    saved = await connection.QuerySingleAsync<ClienteDto>($@"
INSERT INTO clientes (id, id_estabelecimento, nome, telefone_e164, email, observacoes, cep, logradouro, numero,
                      complemento, bairro, cidade, uf, latitude, longitude, simulado, avatar, cpf, data_nascimento,
                      referencia, canal_preferido, origem, tags, consentimento_whatsapp, ativo, criado_por_usuario_id,
                      data_criacao, data_atualizacao)
VALUES (@Id, @EstabelecimentoId, @Nome, @Telefone, @Email, @Observacoes, @Cep, @Logradouro, @Numero,
        @Complemento, @Bairro, @Cidade, @Uf, @Latitude, @Longitude, @Simulado, @Avatar, @Cpf, @DataNascimento::date,
        @Referencia, @CanalPreferido, @Origem, @Tags, @ConsentimentoWhatsapp, @Ativo, @ActorUserId, NOW(), NOW())
RETURNING{Columns};", Parameters(estabelecimentoId, Guid.NewGuid(), input, actorUserId), transaction);
                }

                await transaction.CommitAsync();
                return saved;
            }
            catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
            {
                throw PhoneTaken();
            }
        }

        public async Task<ClienteDto?> UpdateAsync(Guid estabelecimentoId, Guid clienteId, ClienteInput input)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                await LockPhoneAsync(connection, transaction, estabelecimentoId, input.TelefoneE164);

                var taken = await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS (
    SELECT 1 FROM clientes
     WHERE id_estabelecimento = @EstabelecimentoId AND telefone_e164 = @Telefone AND id <> @Id
       AND ativo = TRUE AND COALESCE(btrim(nome), '') <> '');",
                    new { EstabelecimentoId = estabelecimentoId, Telefone = input.TelefoneE164, Id = clienteId }, transaction);
                if (taken) throw PhoneTaken();

                var saved = await connection.QuerySingleOrDefaultAsync<ClienteDto>($@"
UPDATE clientes
   SET nome = @Nome, telefone_e164 = @Telefone, email = @Email, observacoes = @Observacoes, cep = @Cep,
       logradouro = @Logradouro, numero = @Numero, complemento = @Complemento, bairro = @Bairro,
       cidade = @Cidade, uf = @Uf, latitude = @Latitude, longitude = @Longitude, simulado = @Simulado,
       avatar = @Avatar, cpf = @Cpf, data_nascimento = @DataNascimento::date, referencia = @Referencia,
       canal_preferido = @CanalPreferido, origem = @Origem, tags = @Tags,
       consentimento_whatsapp = @ConsentimentoWhatsapp, ativo = @Ativo, data_atualizacao = NOW()
 WHERE id = @Id AND id_estabelecimento = @EstabelecimentoId
RETURNING{Columns};", Parameters(estabelecimentoId, clienteId, input, 0), transaction);

                await transaction.CommitAsync();
                return saved;
            }
            catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
            {
                throw PhoneTaken();
            }
        }

        public async Task<bool> DeactivateAsync(Guid estabelecimentoId, Guid clienteId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var affected = await connection.ExecuteAsync(@"
UPDATE clientes SET ativo = FALSE, data_atualizacao = NOW()
 WHERE id = @Id AND id_estabelecimento = @EstabelecimentoId AND ativo = TRUE;",
                new { Id = clienteId, EstabelecimentoId = estabelecimentoId });
            return affected > 0;
        }

        // Serializa cadastros do mesmo telefone no mesmo estabelecimento (a tabela pode nao ter indice unico).
        private static Task LockPhoneAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, string phone) =>
            connection.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtext(@Key));",
                new { Key = $"cliente:{estabelecimentoId}:{phone}" }, transaction);

        private static object Parameters(Guid estabelecimentoId, Guid id, ClienteInput input, int actorUserId) => new
        {
            Id = id,
            EstabelecimentoId = estabelecimentoId,
            ActorUserId = actorUserId > 0 ? actorUserId : (int?)null,
            input.Nome,
            Telefone = input.TelefoneE164,
            input.Email,
            input.Observacoes,
            input.Cep,
            input.Logradouro,
            input.Numero,
            input.Complemento,
            input.Bairro,
            input.Cidade,
            input.Uf,
            input.Latitude,
            input.Longitude,
            input.Simulado,
            input.Avatar,
            input.Cpf,
            input.DataNascimento,
            input.Referencia,
            input.CanalPreferido,
            input.Origem,
            input.Tags,
            input.ConsentimentoWhatsapp,
            input.Ativo
        };

        private static DeliveryDomainException PhoneTaken() =>
            new(409, "CLIENT_PHONE_TAKEN", "Ja existe um cliente ativo com este telefone.");
    }
}
