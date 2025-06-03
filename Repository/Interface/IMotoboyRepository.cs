using APIBack.Controllers;
using APIBack.Model;
using Motoboy = APIBack.Model.Motoboy;

namespace APIBack.Repository.Interface
{
    public interface IMotoboyRepository
    {
        IEnumerable<Motoboy> GetMotoboy();
        IEnumerable<Motoboy> ConvidarMotoboy();
        Task<Motoboy> ObterPorIdAsync(int id);
        IEnumerable<Motoboy> ListarOnline();
        Task AtualizarAvatarAsync(int id, string caminhoAvatar);
    }
}
