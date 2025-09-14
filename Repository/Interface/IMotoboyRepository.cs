using APIBack.Model;

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
