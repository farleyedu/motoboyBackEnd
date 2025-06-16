using APIBack.DTOs;
using APIBack.Model;
using APIBack.Repository;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;

namespace APIBack.Service
{
    public class MotoboyService : IMotoboyService
    {
        private readonly IMotoboyRepository _motoboyRepository;
        private readonly IWebHostEnvironment _env;
        private readonly IPedidoRepository _pedidoRepository;

        public MotoboyService(IMotoboyRepository motoboyRepository, IWebHostEnvironment env, IPedidoRepository pedidoRepository)
        {
            _motoboyRepository = motoboyRepository;
            _env = env;
            _pedidoRepository = pedidoRepository;
        }

        public IEnumerable<Motoboy> GetMotoboy()
        {
            return _motoboyRepository.GetMotoboy();
        }

        public IEnumerable<Motoboy> ConvidarMotoboy()
        {
            return _motoboyRepository.ConvidarMotoboy();
        }

        public IEnumerable<MotoboyComPedidosDTO> GetMotoboysOnline()
        {
            var motoboys = _motoboyRepository.ListarOnline();
            var result = new List<MotoboyComPedidosDTO>();

            try
            {
                foreach (var motoboy in motoboys)
                {
                    var pedidos = _pedidoRepository.GetPedidosPorMotoboy(motoboy.Id ?? 0);

                    result.Add(new MotoboyComPedidosDTO
                    {
                        Id = motoboy.Id ?? 0,
                        Nome = motoboy.Nome ?? "",
                        Avatar = motoboy.Avatar ?? "",
                        Status = "online",
                        Latitude = motoboy?.Latitude ?? 0,
                        Longitude = motoboy?.Longitude ?? 0,
                        pedidos = pedidos.Select(p => new PedidoDTO
                        {
                            Id = p.Id ?? 0,
                            Status = p.StatusPedido?.ToString() ?? "",
                            Address = p.EnderecoEntrega ?? "",
                            DepartureTime = p.DataPedido.ToString("HH:mm"),
                            Eta = "",
                            EtaMinutes = 0,
                            Coordinates = new[] { 0.0, 0.0 } // Substituir quando tiver coords
                        }).ToList()
                    });
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("erro no pedido", e);
            }

           return result;
        }

        public async Task<ResultadoUploadAvatar> UploadAvatarAsync(int id, IFormFile avatar)
        {
            var motoboy = await _motoboyRepository.ObterPorIdAsync(id);
            if (motoboy == null)
                return new ResultadoUploadAvatar { Sucesso = false, Mensagem = "Motoboy não encontrado" };

            var fileName = $"{Guid.NewGuid()}{Path.GetExtension(avatar.FileName)}";
            var relativePath = Path.Combine("avatars", fileName);
            var fullPath = Path.Combine(_env.WebRootPath, relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            using (var stream = new FileStream(fullPath, FileMode.Create))
            {
                await avatar.CopyToAsync(stream);
            }

            motoboy.Avatar = relativePath.Replace("\\", "/");
            await _motoboyRepository.AtualizarAvatarAsync(id, motoboy.Avatar);

            return new ResultadoUploadAvatar
            {
                Sucesso = true,
                CaminhoAvatar = motoboy.Avatar
            };
        }
    }
}
