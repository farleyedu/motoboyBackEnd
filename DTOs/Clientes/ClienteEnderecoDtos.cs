using APIBack.DTOs.Cardapio;

namespace APIBack.DTOs.Clientes;

public class ClienteEnderecoRequest : CriarCardapioPedidoPublicoEnderecoRequest
{
    public string? Apelido { get; set; }
    public bool Principal { get; set; }
}

public sealed class ClienteEnderecoDto : ClienteEnderecoRequest
{
    public Guid Id { get; set; }
}

public sealed class ClienteSessaoDto
{
    public bool Autenticado { get; set; }
    public string? Token { get; set; }
    public DateTimeOffset? ExpiraEm { get; set; }
    public Guid? ClienteId { get; set; }
    public string? Nome { get; set; }
    public string? Telefone { get; set; }
    public string? WhatsappUrl { get; set; }
    public IReadOnlyList<ClienteEnderecoDto> Enderecos { get; set; } = Array.Empty<ClienteEnderecoDto>();
}

public sealed class ClienteAutenticarRequest
{
    public Guid EstabelecimentoId { get; set; }
    public string Telefone { get; set; } = string.Empty;
}

public sealed record ClienteSessao(Guid ClienteId, Guid EstabelecimentoId, string Telefone, string Nome, DateTimeOffset ExpiraEm);
