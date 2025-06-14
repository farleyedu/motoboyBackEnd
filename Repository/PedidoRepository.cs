using APIBack.DTOs;
using APIBack.Model;
using APIBack.Model.Enum;
using APIBack.Repository.Interface;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text.Json;

namespace APIBack.Repository
{
    public class PedidoRepository : IPedidoRepository
    {
        private readonly string _connectionString;

        public PedidoRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("TemplateDB");
        }

        public IEnumerable<Pedido> GetPedidos()
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                return connection.Query<Pedido>("SELECT * FROM Pedido").ToList();
            }
        }

        public IEnumerable<PedidoDTOs> GetPedidosMaps()
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                var sql = @"
                          SELECT 
                              p.Id, p.NomeCliente, p.EnderecoEntrega, p.IdIfood, p.TelefoneCliente,
                              p.DataPedido, p.StatusPedido, p.HorarioPedido, p.PrevisaoEntrega,
                              p.HorarioSaida, p.HorarioEntrega, p.Items, p.Value, p.Region,
                              p.Latitude, p.Longitude,
                              m.Id AS MotoboyId, m.Nome AS Nome, m.Avatar, m.Status
                          FROM Pedido p
                          LEFT JOIN Motoboy m ON m.Id = p.MotoboyResponsavel";

                return connection.Query<PedidoDTOs, MotoboyDTO, PedidoDTOs>(
                    sql,
                    (pedido, motoboy) =>
                    {
                        pedido.MotoboyResponsavel = motoboy;
                        return pedido;
                    },
                    splitOn: "MotoboyId"
                );
            }
        }


        public IEnumerable<Pedido> GetPedidosPorMotoboy(int motoboyId)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                var sql = "SELECT *  FROM Pedido WHERE MotoboyResponsavel = @MotoboyId";
                return connection.Query<Pedido>(sql, new { MotoboyId = motoboyId }).ToList();
            }
        }

        public void InserirPedidosIfood(PedidoCapturado pedido)
        {
            using var connection = new SqlConnection(_connectionString);
            connection.Open(); // 👈 Importante

            var sql = @"
                INSERT INTO Pedido (
                    NomeCliente, EnderecoEntrega, IdIfood, TelefoneCliente, DataPedido, 
                    StatusPedido, HorarioEntrega, Items, Value, Region,
                    Latitude, Longitude, HorarioPedido, PrevisaoEntrega, HorarioSaida, Localizador,
                    EntregaRua, EntregaNumero, EntregaBairro, EntregaCidade, EntregaEstado, EntregaCep,
                    DocumentoCliente, TipoPagamento
                )
                VALUES (
                    @NomeCliente, @EnderecoEntrega, @IdIfood, @TelefoneCliente, @DataPedido, 
                    @StatusPedido, @HorarioEntrega, @Items, @Value, @Region,
                    @Latitude, @Longitude, @HorarioPedido, @PrevisaoEntrega, @HorarioSaida, @Localizador,
                    @EntregaRua, @EntregaNumero, @EntregaBairro, @EntregaCidade, @EntregaEstado, @EntregaCep,
                    @DocumentoCliente, @TipoPagamento
                );";

            try
            {
                connection.Execute(sql, new
                {
                    PedidoIdIfood = pedido.DisplayId,
                    NomeCliente = pedido.Cliente.Nome,
                    EnderecoEntrega = $"{pedido.Endereco.Rua}, {pedido.Endereco.Numero}",
                    IdIfood = pedido.DisplayId,
                    TelefoneCliente = pedido.Cliente.Telefone,
                    DataPedido = pedido.CriadoEm,
                    StatusPedido = StatusPedido.Pendente,
                    //MotoboyResponsavel = (object?)pedido.mo ?? DBNull.Value, // 👈 isso aqui funciona
                    HorarioEntrega = pedido.HorarioEntrega.HasValue && pedido.HorarioEntrega.Value > new DateTime(1753, 1, 1)
                        ? (object)pedido.HorarioEntrega.Value
                        : DBNull.Value,

                    HorarioSaida = pedido.HorarioSaida.HasValue && pedido.HorarioSaida.Value > new DateTime(1753, 1, 1)
                        ? (object)pedido.HorarioSaida.Value
                        : DBNull.Value,

                    Items = JsonSerializer.Serialize(pedido.Itens),
                    Value = pedido.Itens.Sum(i => i.PrecoTotal),
                    Region = pedido.Endereco.Bairro,
                    Latitude = pedido.Coordenadas.Latitude,
                    Longitude = pedido.Coordenadas.Longitude,
                    HorarioPedido = pedido.CriadoEm,
                    PrevisaoEntrega = pedido.PrevisaoEntrega,
                    Localizador = pedido.Localizador,
                    EntregaRua = pedido.Endereco.Rua,
                    EntregaNumero = pedido.Endereco.Numero,
                    EntregaBairro = pedido.Endereco.Bairro,
                    EntregaCidade = pedido.Endereco.Cidade,
                    EntregaEstado = pedido.Endereco.Estado,
                    EntregaCep = pedido.Endereco.Cep,
                    DocumentoCliente = pedido.Cliente.Documento,
                    TipoPagamento = pedido.TipoPagamento // 👈 ADICIONADO
                });

            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Erro ao inserir pedido {pedido.Id}: {ex.Message}");
            }
        }



        public IEnumerable<Pedido> GetPedidosId()
        {
            throw new NotImplementedException();
        }

        public IEnumerable<Pedido> CriarPedido()
        {
            throw new NotImplementedException();
        }
        public IEnumerable<Pedido> AtribuirMotoboy()
        {
            throw new NotImplementedException();
        }
        public IEnumerable<Pedido> CancelarPedido()
        {
            throw new NotImplementedException();
        }
        public IEnumerable<Pedido> FinalizarPedido()
        {
            throw new NotImplementedException();
        }
        public IEnumerable<Pedido> AlteraPedido(int Id, Pedido pedido)
        {
            throw new NotImplementedException();
        }
    }
}
