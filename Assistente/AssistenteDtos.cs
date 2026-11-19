using System.Text.Json.Serialization;

namespace APIBack.Assistente;

// Formato público: zippy-admin/docs/assistente-ajuda/CONTRATO-API.md. Mudou aqui, muda lá.

public sealed class EnviarMensagemRequest
{
    public Guid? ConversaId { get; set; }
    public string? Texto { get; set; }
}

public sealed record RespostaMensagemDto(Guid ConversaId, Guid MensagemId, string Texto, IReadOnlyList<CartaoDto> Cartoes, int RestantesHoje);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "tipo")]
[JsonDerivedType(typeof(CartaoAcaoDto), "acao")]
[JsonDerivedType(typeof(CartaoTelaDto), "tela")]
[JsonDerivedType(typeof(CartaoChamadoDto), "chamado")]
public abstract record CartaoDto;

public sealed record CartaoAcaoDto(Guid AcaoId, string FuncionalidadeId, string Titulo, string Resumo, string? Antes, string? Depois, DateTimeOffset ExpiraEm) : CartaoDto;

public sealed record CartaoTelaDto(string FuncionalidadeId, string Titulo, IReadOnlyList<string> Passos, string Rota) : CartaoDto;

public sealed record CartaoChamadoDto(string Categoria, string Titulo, string Texto) : CartaoDto;

public sealed record AcaoConfirmadaDto(Guid AcaoId, string Situacao, string Mensagem, Guid? DesfazerAcaoId);

public sealed record AcaoRecusadaDto(Guid AcaoId, string Situacao);

public sealed record MensagemConversaDto(Guid Id, string De, string Texto, IReadOnlyList<CartaoDto> Cartoes, DateTimeOffset CriadaEm);

public sealed record ConversaDto(Guid ConversaId, IReadOnlyList<MensagemConversaDto> Mensagens);

/// <summary>Erro do assistente com HTTP e código do CONTRATO-API §6.</summary>
public sealed class AssistenteException(int status, string codigo, string mensagem) : Exception(mensagem)
{
    public int Status { get; } = status;
    public string Codigo { get; } = codigo;
}

// ---- Gestão (CONTRATO-API §7)

public sealed record TokensDto(long Entrada, long Saida, long CacheLidos);

public sealed record TesteIADto(bool Ok, string? Resposta, string? ItemId, string? Provedor, string? Modelo, bool UsouReserva, long Ms, TokensDto? Tokens,
    IReadOnlyList<string> Ferramentas, string? Erro, IReadOnlyList<ChecagemFerramentaDto>? Checagem);

public sealed record ChecagemFerramentaDto(string Ferramenta, string Pergunta, IReadOnlyList<string> Chamou, bool Ok, string? Erro, long Ms);

/// <summary>Estado de um item da cadeia agora (CONTRATO-API §10.3).</summary>
public sealed record EstadoItemDto(string ItemId, bool Disponivel, string? Motivo, DateTimeOffset? EsgotadoAte, bool Desligado, string? Erro,
    long UsadasHoje, bool TemChave);

/// <summary>Chaves e modelos vêm por id de provedor ({ "openai": true, "groq": false, ... }).</summary>
public sealed record ConfigIADto(ConfigIA Config, bool Padrao, string? AtualizadoPor, DateTimeOffset? AtualizadoEm, bool CifraDisponivel,
    IReadOnlyDictionary<string, ChaveInfoDto> Chaves, IReadOnlyDictionary<string, IReadOnlyList<string>> Modelos, IReadOnlyList<string> SemPreco,
    IReadOnlyList<EstadoItemDto> Estado, IReadOnlyList<AlertaDto> Alertas, IReadOnlyDictionary<string, IReadOnlySet<string>> NaoSuporta);

public sealed class TestarIARequest
{
    public ConfigIA? Config { get; set; }
    public string? Pergunta { get; set; }
    /// <summary>Testa só este item da cadeia (sem troca). Nulo = a cadeia toda.</summary>
    public string? ItemId { get; set; }
    /// <summary>Uma pergunta por ferramenta, com <c>checagem</c> na resposta.</summary>
    public bool Ferramentas { get; set; }
}

public sealed class EnviarChamadoRequest
{
    public Guid? ConversaId { get; set; }
    public string? Categoria { get; set; }
    public string? Texto { get; set; }
}
