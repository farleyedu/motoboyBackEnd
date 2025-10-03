using APIBack.Model;
using APIBack.Repository.Interface;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading.Tasks;

namespace APIBack.Repository
{
    public class ReservaRepository : IReservaRepository
    {
        private readonly NpgsqlDataSource _dataSource;

        // 2. Receba NpgsqlDataSource no construtor em vez de IConfiguration
        public ReservaRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource;
        }

        public async Task<long> AdicionarAsync(Reserva entity)
        {

            // A única alteração é na linha do VALUES para o @Status
            const string sql = @"INSERT INTO reservas (
                        id_cliente, id_estabelecimento, id_profissional, id_servico,
                        qtd_pessoas, data_reserva, hora_inicio, hora_fim,
                        status, observacoes)
                      VALUES (
                        @IdCliente, @IdEstabelecimento, @IdProfissional, @IdServico,
                        @QtdPessoas, @DataReserva, @HoraInicio, @HoraFim,
                        @Status, @Observacoes)
                      RETURNING id;"; 

            await using var connection = await _dataSource.OpenConnectionAsync();
            // O restante do código não muda
            return await connection.ExecuteScalarAsync<long>(sql, new
            {
                entity.IdCliente,
                entity.IdEstabelecimento,
                entity.IdProfissional,
                entity.IdServico,
                entity.QtdPessoas,
                DataReserva = entity.DataReserva.Date,
                entity.HoraInicio,
                entity.HoraFim,
                entity.Status,
                entity.Observacoes
            });
        }

        public async Task<Reserva?> BuscarPorIdAsync(long id)
        {
            const string sql = @"SELECT
                                        id AS Id,
                                        id_cliente AS IdCliente,
                                        id_estabelecimento AS IdEstabelecimento,
                                        id_profissional AS IdProfissional,
                                        id_servico AS IdServico,
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
                                        id_cliente AS IdCliente,
                                        id_estabelecimento AS IdEstabelecimento,
                                        id_profissional AS IdProfissional,
                                        id_servico AS IdServico,
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
                                   SET id_cliente = @IdCliente,
                                       id_estabelecimento = @IdEstabelecimento,
                                       id_profissional = @IdProfissional,
                                       id_servico = @IdServico,
                                       qtd_pessoas = @QtdPessoas,
                                       data_reserva = @DataReserva,
                                       hora_inicio = @HoraInicio,
                                       hora_fim = @HoraFim,
                                       status = @Status,
                                       observacoes = @Observacoes,
                                       data_atualizacao = @DataAtualizacao
                                   WHERE id = @Id;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.ExecuteAsync(sql, new
            {
                entity.Id,
                entity.IdCliente,
                entity.IdEstabelecimento,
                entity.IdProfissional,
                entity.IdServico,
                entity.QtdPessoas,
                DataReserva = entity.DataReserva.Date,
                entity.HoraInicio,
                entity.HoraFim,
                entity.Status,
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

        public async Task<int> CancelarReservaAsync(long id)
        {
            var dataAtualizacao = DateTime.UtcNow;

            const string sql = @"UPDATE reservas
                                   SET status = 'cancelado',
                                       data_atualizacao = @DataAtualizacao
                                   WHERE id = @Id AND status <> 'cancelado';";

            await using var connection = await _dataSource.OpenConnectionAsync();

            return await connection.ExecuteAsync(sql, new { Id = id, DataAtualizacao = dataAtualizacao });
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
    }
}
