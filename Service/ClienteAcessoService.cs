using System.Security.Cryptography;
using System.Text;
using APIBack.Automation.Interfaces;
using APIBack.DTOs.Clientes;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;

namespace APIBack.Service;

public sealed class ClienteAcessoService(IClienteEnderecoRepository enderecos, IClienteCadastroRepository clientes,
    ICardapioPedidoWebRepository pedidos, ICardapioRepository cardapio, IWabaPhoneRepository waba) : IClienteAcessoService
{
    public static readonly TimeSpan JanelaWhatsapp = TimeSpan.FromHours(1);
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<ClienteSessaoDto> AuthenticateAsync(Guid estabelecimentoId, string telefone)
    {
        var loja = await cardapio.ObterEstabelecimentoPublicoAsync(estabelecimentoId, null);
        if (loja == null || !loja.Publicado) throw new DeliveryDomainException(404, "STORE_NOT_FOUND", "Cardápio não encontrado.");
        var variantes = CardapioConfirmacaoRules.VariantesTelefone(telefone);
        if (variantes.Count == 0) throw new DeliveryDomainException(422, "INVALID_PHONE", "Informe seu WhatsApp com DDD.");
        var conversa = await pedidos.ObterConversaPorTelefoneAsync(estabelecimentoId, variantes);
        if (conversa == null || !conversa.FalouRecentemente(JanelaWhatsapp))
        {
            var numeroLoja = await waba.ObterDisplayPhoneParaServicoAsync(estabelecimentoId, "cardapio_web");
            var digits = new string((numeroLoja ?? "").Where(char.IsDigit).ToArray());
            if (digits.Length is 10 or 11) digits = "55" + digits;
            return new ClienteSessaoDto { WhatsappUrl = digits.Length >= 12 ? $"https://wa.me/{digits}?text={Uri.EscapeDataString("Olá! Quero acessar meus endereços e fazer um pedido pelo cardápio.")}" : null };
        }
        // Usa o telefone da conversa comprovada, incluindo clientes antigos sem o nono dígito.
        var cliente = await clientes.GetByTelefoneAsync(estabelecimentoId, telefone);
        if (cliente == null)
        {
            foreach (var variante in variantes)
            {
                cliente = await clientes.GetByTelefoneAsync(estabelecimentoId, variante);
                if (cliente != null) break;
            }
        }
        if (cliente == null) throw new DeliveryDomainException(409, "CLIENT_NOT_READY", "Sua mensagem está sendo processada. Tente novamente.");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expires = DateTimeOffset.UtcNow.AddDays(30);
        await enderecos.CreateSessionAsync(estabelecimentoId, cliente.Id, Hash(token), expires);
        var profile = await ProfileAsync(estabelecimentoId, token);
        profile.Token = token;
        return profile;
    }

    public async Task<ClienteSessao> RequireAsync(Guid estabelecimentoId, string? token, string? telefone = null)
    {
        var session = string.IsNullOrEmpty(token) || token.Length != 64 ? null : await enderecos.GetSessionAsync(estabelecimentoId, Hash(token));
        if (session == null) throw new DeliveryDomainException(403, "CLIENT_UNAUTHENTICATED", "Confirme seu WhatsApp para continuar.");
        if (telefone != null && !CardapioConfirmacaoRules.VariantesTelefone(telefone).Intersect(CardapioConfirmacaoRules.VariantesTelefone(session.Telefone)).Any())
            throw new DeliveryDomainException(403, "CLIENT_PHONE_MISMATCH", "O número do pedido deve ser o da sua sessão. Confirme o outro WhatsApp para trocar.");
        return session;
    }

    public async Task<ClienteSessaoDto> ProfileAsync(Guid estabelecimentoId, string? token)
    {
        var session = await RequireAsync(estabelecimentoId, token);
        return new ClienteSessaoDto { Autenticado = true, ClienteId = session.ClienteId, Nome = session.Nome, Telefone = session.Telefone,
            ExpiraEm = session.ExpiraEm, Enderecos = await enderecos.ListAsync(estabelecimentoId, session.ClienteId) };
    }

    public Task LogoutAsync(Guid estabelecimentoId, string? token) => string.IsNullOrEmpty(token) ? Task.CompletedTask : enderecos.RevokeSessionAsync(estabelecimentoId, Hash(token));
}
