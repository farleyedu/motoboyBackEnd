using APIBack.DTOs;
using APIBack.Model;

namespace APIBack.Service.Interface
{
    public interface IMotoboyService
    {
        IEnumerable<Motoboy> GetMotoboy();
        IEnumerable<Motoboy> ConvidarMotoboy();
        IEnumerable<MotoboyComPedidosDTO> GetMotoboysOnline(Guid estabelecimentoId);
        Task<ResultadoUploadAvatar> UploadAvatarAsync(int id, IFormFile avatar);
    }
}
