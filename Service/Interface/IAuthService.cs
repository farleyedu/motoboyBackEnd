using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Auth;
using APIBack.Model.Auth;

namespace APIBack.Service.Interface
{
    public interface IAuthService
    {
        Task<TokenResponse> LoginAsync(LoginRequest request);
        Task<TokenResponse> SelecionarEstabelecimentoAsync(Guid userId, Guid estabelecimentoId);
        Task<List<EstabelecimentoDisponivelDTO>> ListarEstabelecimentosDisponiveisAsync(Guid userId);
    }
}

