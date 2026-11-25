using APIBack.Social.Ia;

namespace APIBack.Social.Voz;

/// <summary>Tempo de uma palavra dentro da fala, em segundos desde o começo do áudio.</summary>
public sealed record PalavraTempo(string Texto, decimal Ini, decimal Fim);

/// <param name="Voz">Id da voz no provedor (já traduzido do nome do protótipo) ou id da voz clonada.</param>
/// <param name="Velocidade">1 = normal. Cada provedor tem a sua faixa; o adaptador limita.</param>
public sealed record PedidoVoz(string Texto, string Voz, decimal Velocidade, string Idioma, string? Modelo);

/// <param name="Palavras">Nulo quando o provedor não devolve o tempo de cada palavra.</param>
public sealed record AudioVoz(byte[] Bytes, string Mime, decimal DuracaoS, IReadOnlyList<PalavraTempo>? Palavras);

public sealed record AmostraVoz(byte[] Bytes, string Mime, string NomeArquivo);

/// <summary>O que fica registrado no provedor junto com a voz clonada (rastreio da prova guardada no banco).</summary>
public sealed record ConsentimentoVoz(long ConsentimentoId, Guid Loja, int Usuario, string TermoVersao, DateTimeOffset AceitoEm);

public sealed record CapacidadesVoz(bool Sintetizar, bool Clonar, bool Alinhamento);

/// <summary>
/// Tomada de voz do Social (R10). Um adaptador por provedor: ElevenLabs (fala e clone), OpenAI TTS e Azure Speech (só fala).
/// Falhas do provedor saem como <see cref="FalhaIaException"/>.
/// </summary>
public interface IVoz
{
    string Provedor { get; }
    CapacidadesVoz Capacidades { get; }
    Task<AudioVoz> SintetizarAsync(PedidoVoz pedido, CancellationToken ct);
    /// <returns>Id da voz criada no provedor.</returns>
    Task<string> CriarVozClonadaAsync(AmostraVoz amostra, ConsentimentoVoz consentimento, CancellationToken ct);
    Task ApagarVozClonadaAsync(string vozId, CancellationToken ct);
}

public interface IFabricaVoz
{
    IVoz Criar(ProvedorEscolhido provedor);
}

public sealed class FabricaVoz(IHttpClientFactory http) : IFabricaVoz
{
    public const string ClienteHttp = "social-voz";

    public IVoz Criar(ProvedorEscolhido p) => p.ProvedorId switch
    {
        ProvedoresSocial.ElevenLabs => new VozElevenLabs(http.CreateClient(ClienteHttp), p.Chave!, p.BaseUrl),
        ProvedoresSocial.OpenAI => new VozOpenAI(http.CreateClient(ClienteHttp), p.Chave!, p.BaseUrl),
        ProvedoresSocial.Azure => new VozAzure(http.CreateClient(ClienteHttp), p.Chave!, p.BaseUrl!),
        ProvedoresSocial.Falso => new VozFalsa(),
        _ => throw new FalhaIaException(p.ProvedorId, "Este provedor não faz voz.", TipoFalhaIa.Pedido),
    };
}
