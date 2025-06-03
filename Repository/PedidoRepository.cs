using APIBack.DTOs;
using APIBack.Model;
using APIBack.Repository.Interface;
using Dapper;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;

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
