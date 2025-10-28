using APIBack.Model;
using APIBack.Repository.Interface;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace APIBack.Repository
{
    public class ReservaRepository : IReservaRepository
    {
        private readonly NpgsqlDataSource _dataSource;
        private readonly ILogger<ReservaRepository> _logger;

        public ReservaRepository(NpgsqlDataSource dataSource, ILogger<ReservaRepository> logger)
        {
            _dataSource = dataSource;
            _logger = logger;
        }

        private static string ToPgStatus(ReservaStatus s) => s switch
        {
            ReservaStatus.Pendente => "pendente",
            ReservaStatus.Confirmado => "confirmado",
            ReservaStatus.Cancelado => "cancelado",
            _ => throw new ArgumentOutOfRangeException(nameof(s), s, "Status inválido")
        };

        public async Task<long> AdicionarAsync(Reserva entity)
        {
            entity.Codigo = await GerarCodigoUnicoAsync();
            _logger.LogInformation("[AdicionarAsync] Reserva criada com codigo: {Codigo}", entity.Codigo);

            const string sql = @"
INSERT INTO reservas (
  codigo,
  id_cliente, id_estabelecimento, id_profissional, id_servico,
  nome_cliente_reserva, qtd_pessoas, data_reserva, hora_inicio, hora_fim,
  status, observacoes)
VALUES (
  @Codigo,
  @IdCliente, @IdEstabelecimento, @IdProfissional, @IdServico,
  @NomeCliente, @QtdPessoas, @DataReserva, @HoraInicio, @HoraFim,
  @Status::status_reserva, @Observacoes)
RETURNING id;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.ExecuteScalarAsync<long>(sql, new
            {
                entity.Codigo,
                entity.IdCliente,
                entity.IdEstabelecimento,
                entity.IdProfissional,
                entity.IdServico,
                entity.NomeCliente,
                entity.QtdPessoas,
                DataReserva = entity.DataReserva.Date,
                entity.HoraInicio,
                entity.HoraFim,
                Status = ToPgStatus(entity.Status),
                entity.Observacoes
            });
        }

        public async Task<Reserva?> BuscarPorIdAsync(long id)
        {
            const string sql = @"SELECT
                                        id AS Id,
                                        codigo AS Codigo,
                                        id_cliente AS IdCliente,
                                        id_estabelecimento AS IdEstabelecimento,
                                        id_profissional AS IdProfissional,
                                        id_servico AS IdServico,
                                        nome_cliente_reserva AS NomeCliente,
                                        qtd_pessoas AS QtdPessoas,
                                        data_reserva AS DataReserva,
                                        hora_inicio AS HoraInicio,
                                        hora_fim AS HoraFim,
                                        status AS Status,
                                        observacoes AS Observacoes,
                                        data_criacao AS DataCriacao,
                                        data_atualizacao AS DataAtualizacao
                                   FROM reservas
                                   WHERE id = @Id;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.QueryFirstOrDefaultAsync<Reserva>(sql, new { Id = id });
        }

        public async Task<IEnumerable<Reserva>> BuscarTodosAsync()
        {
            const string sql = @"SELECT
                                        id AS Id,
                                        codigo AS Codigo,
                                        id_cliente AS IdCliente,
                                        id_estabelecimento AS IdEstabelecimento,
                                        id_profissional AS IdProfissional,
                                        id_servico AS IdServico,
                                        nome_cliente_reserva AS NomeCliente,
                                        qtd_pessoas AS QtdPessoas,
                                        data_reserva AS DataReserva,
                                        hora_inicio AS HoraInicio,
                                        hora_fim AS HoraFim,
                                        status AS Status,
                                        observacoes AS Observacoes,
                                        data_criacao AS DataCriacao,
                                        data_atualizacao AS DataAtualizacao
                                   FROM reservas
                                   ORDER BY id;";

            await using var connection = await _dataSource.OpenConnectionAsync();

            return await connection.QueryAsync<Reserva>(sql);
        }

        public async Task<int> AtualizarAsync(Reserva entity)
        {
            entity.DataAtualizacao = DateTime.UtcNow;

            const string sql = @"UPDATE reservas
                                   SET id_profissional = @IdProfissional,
                                       id_servico = @IdServico,
                                       nome_cliente_reserva = @NomeCliente,
                                       qtd_pessoas = @QtdPessoas,
                                       data_reserva = @DataReserva,
                                       hora_inicio = @HoraInicio,
                                       hora_fim = @HoraFim,
                                       observacoes = @Observacoes,
                                       data_atualizacao = @DataAtualizacao
                                   WHERE id = @Id;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.ExecuteAsync(sql, new
            {
                entity.Id,
                entity.IdProfissional,
                entity.IdServico,
                entity.NomeCliente,
                entity.QtdPessoas,
                DataReserva = entity.DataReserva.Date,
                entity.HoraInicio,
                entity.HoraFim,
                entity.Observacoes,
                entity.DataAtualizacao
            });
        }

        public async Task<int> ExcluirAsync(long id)
        {
            const string sql = "DELETE FROM reservas WHERE id = @Id;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.ExecuteAsync(sql, new { Id = id });
        }

        // ✨ CORRIGIDO: Método CancelarReservaAsync agora usa cast correto
        public async Task<int> CancelarReservaAsync(long id)
        {
            var dataAtualizacao = DateTime.UtcNow;

            // ✨ MUDANÇA: Adicionado @Status com cast ::status_reserva ao invés de string literal
            const string sql = @"UPDATE reservas
                                   SET status = @Status::status_reserva,
                                       data_atualizacao = @DataAtualizacao
                                   WHERE id = @Id AND status <> @StatusCancelado::status_reserva;";

            await using var connection = await _dataSource.OpenConnectionAsync();

            // ✨ MUDANÇA: Usa helper ToPgStatus() para gerar as strings corretas
            return await connection.ExecuteAsync(sql, new
            {
                Id = id,
                DataAtualizacao = dataAtualizacao,
                Status = ToPgStatus(ReservaStatus.Cancelado),
                StatusCancelado = ToPgStatus(ReservaStatus.Cancelado)
            });
        }

        public async Task<bool> BuscarDisponibilidadeAsync(Guid idEstabelecimento, DateTime dataReserva, TimeSpan horaInicio, TimeSpan? horaFim, long? idProfissional = null)
        {
            const string sql = @"SELECT COUNT(1)
                                   FROM reservas
                                  WHERE id_estabelecimento = @IdEstabelecimento
                                    AND data_reserva = @DataReserva
                                    AND status IN ('pendente', 'confirmado')
                                    AND (@IdProfissional IS NULL OR id_profissional = @IdProfissional)
                                    AND (
                                        (COALESCE(hora_fim, hora_inicio) > @HoraInicio
                                         AND COALESCE(@HoraFim, @HoraInicio) > hora_inicio)
                                        OR (hora_fim IS NULL AND @HoraFim IS NULL AND hora_inicio = @HoraInicio)
                                    );";

            await using var connection = await _dataSource.OpenConnectionAsync();
            var conflitos = await connection.ExecuteScalarAsync<int>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                DataReserva = dataReserva.Date,
                HoraInicio = horaInicio,
                HoraFim = horaFim,
                IdProfissional = idProfissional
            });

            return conflitos == 0;
        }

        public async Task<int> SomarPessoasDoDiaAsync(Guid idEstabelecimento, DateTime dataReserva)
        {
            const string sql = @"SELECT COALESCE(SUM(qtd_pessoas), 0)
                                   FROM reservas
                                  WHERE id_estabelecimento = @IdEstabelecimento
                                    AND data_reserva = @DataReserva
                                    AND status IN ('pendente','confirmado');";

            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.ExecuteScalarAsync<int>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                DataReserva = dataReserva.Date
            });
        }

        public async Task<List<Reserva>> ObterPorClienteEstabelecimentoAsync(Guid idCliente, Guid idEstabelecimento)
        {
            const string sql = @"SELECT
                            id AS Id,
                            codigo AS Codigo,
                            id_cliente AS IdCliente,
                            id_estabelecimento AS IdEstabelecimento,
                            id_profissional AS IdProfissional,
                            id_servico AS IdServico,
                            nome_cliente_reserva AS NomeCliente,
                            qtd_pessoas AS QtdPessoas,
                            data_reserva AS DataReserva,
                            hora_inicio AS HoraInicio,
                            hora_fim AS HoraFim,
                            status AS Status,
                            observacoes AS Observacoes,
                            data_criacao AS DataCriacao,
                            data_atualizacao AS DataAtualizacao
                       FROM reservas
                      WHERE id_cliente = @IdCliente
                        AND id_estabelecimento = @IdEstabelecimento
                      ORDER BY data_reserva DESC;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            var resultado = await connection.QueryAsync<Reserva>(sql, new
            {
                IdCliente = idCliente,
                IdEstabelecimento = idEstabelecimento
            });

            return resultado.AsList();
        }

        public async Task<List<Reserva>> ObterPorEstabelecimentoDataAsync(Guid idEstabelecimento, DateTime data)
        {
            const string sql = @"SELECT
                            id AS Id,
                            codigo AS Codigo,
                            id_cliente AS IdCliente,
                            id_estabelecimento AS IdEstabelecimento,
                            id_profissional AS IdProfissional,
                            id_servico AS IdServico,
                            nome_cliente_reserva AS NomeCliente,
                            qtd_pessoas AS QtdPessoas,
                            data_reserva AS DataReserva,
                            hora_inicio AS HoraInicio,
                            hora_fim AS HoraFim,
                            status AS Status,
                            observacoes AS Observacoes,
                            data_criacao AS DataCriacao,
                            data_atualizacao AS DataAtualizacao
                       FROM reservas
                      WHERE id_estabelecimento = @IdEstabelecimento
                        AND data_reserva = @Data
                        AND status = @Status
                      ORDER BY hora_inicio;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            var resultado = await connection.QueryAsync<Reserva>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Data = data.Date,
                Status = ToPgStatus(ReservaStatus.Confirmado)
            });

            return resultado.AsList();
        }

        public async Task<Reserva?> BuscarPorCodigoAsync(long codigo, Guid idEstabelecimento)
        {
            var codigoFormatado = codigo.ToString("D4");
            var reserva = await BuscarPorCodigoAsync(codigoFormatado);

            if (reserva == null)
            {
                return null;
            }

            if (reserva.IdEstabelecimento != idEstabelecimento || reserva.Status != ReservaStatus.Confirmado)
            {
                return null;
            }

            return reserva;
        }

        public async Task<string> GerarCodigoUnicoAsync()
        {
            const int codigoInicial = 1035;
            const int maxTentativas = 100;

            await using var connection = await _dataSource.OpenConnectionAsync();

            var maiorCodigoStr = await connection.ExecuteScalarAsync<string?>(@"
SELECT codigo
  FROM reservas
 WHERE codigo IS NOT NULL
 ORDER BY codigo DESC
 LIMIT 1;");

            int proximoCodigo;

            if (string.IsNullOrWhiteSpace(maiorCodigoStr))
            {
                proximoCodigo = codigoInicial;
                _logger.LogInformation("[GerarCodigoUnico] Primeira reserva - iniciando em {Codigo}", proximoCodigo);
            }
            else if (int.TryParse(maiorCodigoStr, out var maiorCodigo))
            {
                proximoCodigo = maiorCodigo + 1;
                _logger.LogDebug("[GerarCodigoUnico] Maior codigo existente: {MaiorCodigo}, proximo: {ProximoCodigo}",
                    maiorCodigo, proximoCodigo);
            }
            else
            {
                proximoCodigo = codigoInicial;
                _logger.LogWarning("[GerarCodigoUnico] Nao foi possivel interpretar maior codigo '{MaiorCodigo}', usando valor inicial {Inicial}",
                    maiorCodigoStr, codigoInicial);
            }

            if (proximoCodigo > 9999)
            {
                throw new InvalidOperationException("Codigo excedeu o limite de 9999. Expanda o range de codigos.");
            }

            for (int tentativa = 0; tentativa < maxTentativas; tentativa++)
            {
                if (proximoCodigo > 9999)
                {
                    throw new InvalidOperationException("Codigo excedeu o limite de 9999. Expanda o range de codigos.");
                }

                var codigo = proximoCodigo.ToString("D4");

                var existe = await connection.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM reservas WHERE codigo = @Codigo);",
                    new { Codigo = codigo });

                if (!existe)
                {
                    _logger.LogInformation("[GerarCodigoUnico] Codigo gerado: {Codigo}", codigo);
                    return codigo;
                }

                proximoCodigo++;
                _logger.LogDebug("[GerarCodigoUnico] Codigo {Codigo} ja existe, tentativa {Tentativa}/{Max}",
                    codigo, tentativa + 1, maxTentativas);
            }

            throw new InvalidOperationException($"Nao foi possivel gerar codigo unico apos {maxTentativas} tentativas");
        }

        public async Task<Reserva?> BuscarPorCodigoAsync(string codigo)
        {
            _logger.LogDebug("[BuscarPorCodigo] Buscando reserva com codigo: {Codigo}", codigo);

            const string sql = @"
                SELECT
                    id AS Id,
                    codigo AS Codigo,
                    id_cliente AS IdCliente,
                    id_estabelecimento AS IdEstabelecimento,
                    id_profissional AS IdProfissional,
                    id_servico AS IdServico,
                    nome_cliente_reserva AS NomeCliente,
                    qtd_pessoas AS QtdPessoas,
                    data_reserva AS DataReserva,
                    hora_inicio AS HoraInicio,
                    hora_fim AS HoraFim,
                    status AS Status,
                    observacoes AS Observacoes,
                    data_criacao AS DataCriacao,
                    data_atualizacao AS DataAtualizacao
                FROM reservas
                WHERE codigo = @Codigo
                LIMIT 1;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            var reserva = await connection.QueryFirstOrDefaultAsync<Reserva>(sql, new { Codigo = codigo });

            if (reserva == null)
            {
                _logger.LogWarning("[BuscarPorCodigo] Reserva nao encontrada: {Codigo}", codigo);
            }
            else
            {
                _logger.LogDebug("[BuscarPorCodigo] Reserva encontrada: {Codigo} | Id={Id}", codigo, reserva.Id);
            }

            return reserva;
        }

        public async Task<bool> ValidarCodigoUnicoAsync(string codigo)
        {
            const string sql = "SELECT EXISTS (SELECT 1 FROM reservas WHERE codigo = @Codigo);";

            await using var connection = await _dataSource.OpenConnectionAsync();
            var existe = await connection.ExecuteScalarAsync<bool>(sql, new { Codigo = codigo });

            _logger.LogDebug("[ValidarCodigoUnico] Codigo {Codigo}: {Status}",
                codigo, existe ? "JÁ EXISTE" : "DISPONÍVEL");

            return !existe;
        }
    }
}
