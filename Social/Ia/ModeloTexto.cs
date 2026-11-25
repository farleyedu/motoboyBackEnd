using System.Text.Json;
using APIBack.Assistente;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace APIBack.Social.Ia;

public sealed record PedidoTexto(string Sistema, string Contexto, string Texto, int MaxTokens, string Modelo);

public sealed record RespostaTexto(string Texto, long TokensEntrada, long TokensSaida);

/// <summary>Tomada de texto do Social (README §9, <c>IModeloTexto</c>). Uma por provedor; a fase 2 acrescenta imagem.</summary>
public interface IModeloTexto
{
    Task<RespostaTexto> GerarAsync(PedidoTexto pedido, CancellationToken ct);
}

public interface IFabricaTexto
{
    IModeloTexto Criar(ProvedorEscolhido provedor);
}

public sealed class FabricaTexto : IFabricaTexto
{
    public IModeloTexto Criar(ProvedorEscolhido p) => p.ProvedorId switch
    {
        ProvedoresSocial.Anthropic => new TextoAnthropic(p.Chave),
        ProvedoresSocial.Falso => new TextoFalso(),
        _ => throw new FalhaIaException(p.ProvedorId, "Este provedor ainda não escreve textos no Social.", TipoFalhaIa.Pedido),
    };
}

/// <summary>Claude pela API da Anthropic (sem ferramentas, uma rodada). Erros classificados como no Assistente.</summary>
public sealed class TextoAnthropic(string? chave) : IModeloTexto
{
    public async Task<RespostaTexto> GerarAsync(PedidoTexto pedido, CancellationToken ct)
    {
        if (chave is null) throw new FalhaIaException(ProvedoresSocial.Anthropic, "sem chave", TipoFalhaIa.SemChave);
        var client = new AnthropicClient { ApiKey = chave, MaxRetries = 1, Timeout = TimeSpan.FromSeconds(45) };
        var parametros = new MessageCreateParams
        {
            Model = pedido.Modelo,
            MaxTokens = pedido.MaxTokens,
            System = new List<BetaTextBlockParam> { new() { Text = pedido.Sistema } },
            Messages = new List<BetaMessageParam>
            {
                new BetaUserMessageParam
                {
                    Content = new List<BetaContentBlockParam>
                    {
                        new BetaTextBlockParam { Text = string.IsNullOrWhiteSpace(pedido.Contexto) ? "(sem contexto)" : pedido.Contexto },
                        new BetaTextBlockParam { Text = pedido.Texto },
                    },
                },
            },
        };
        BetaMessage r;
        try
        {
            r = await client.Beta.Messages.Create(parametros, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var f = ProvedorAnthropic.Classificar(ex, ProvedoresSocial.Anthropic);
            var tipo = f.Tipo switch
            {
                TipoFalha.Limite => f.CotaDiaria ? TipoFalhaIa.SemSaldo : TipoFalhaIa.Limite,
                TipoFalha.Servidor => TipoFalhaIa.ForaDoAr,
                TipoFalha.SemChave => TipoFalhaIa.SemChave,
                TipoFalha.Configuracao when f.Status is 401 or 403 => TipoFalhaIa.ChaveRecusada,
                _ => TipoFalhaIa.Pedido,
            };
            throw new FalhaIaException(ProvedoresSocial.Anthropic, FalhaIaException.MensagemDe(tipo), tipo, ex);
        }
        if (r.StopReason == BetaStopReason.Refusal)
            throw new FalhaIaException(ProvedoresSocial.Anthropic, "recusou o conteúdo", TipoFalhaIa.Pedido);
        var texto = string.Join("\n", r.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text)).Trim();
        if (texto.Length == 0) throw new FalhaIaException(ProvedoresSocial.Anthropic, "resposta vazia", TipoFalhaIa.ForaDoAr);
        var entrada = r.Usage.InputTokens + (r.Usage.CacheCreationInputTokens ?? 0) + (r.Usage.CacheReadInputTokens ?? 0);
        return new RespostaTexto(texto, entrada, r.Usage.OutputTokens);
    }
}

/// <summary>Sem chave (desenvolvimento e testes): devolve JSON de exemplo no formato que cada pedido espera.</summary>
public sealed class TextoFalso : IModeloTexto
{
    public Task<RespostaTexto> GerarAsync(PedidoTexto pedido, CancellationToken ct)
    {
        object resposta = pedido.Texto.Contains("\"ganchos\"", StringComparison.Ordinal)
            ? new { ganchos = new[] { "Você não vai acreditar no final", "Ninguém esperava por isso", "Olha o que aconteceu aqui" } }
            : pedido.Texto.Contains("\"texto\"", StringComparison.Ordinal) && !pedido.Texto.Contains("\"falas\"", StringComparison.Ordinal)
                ? new { texto = "Olha só o que aconteceu" }
                : new
                {
                    falas = new object[]
                    {
                        new { texto = "Se liga no que vai acontecer agora.", ancora = "inicio" },
                        new { texto = "E foi assim que tudo terminou.", ancora = "fim" },
                    },
                };
        return Task.FromResult(new RespostaTexto(JsonSerializer.Serialize(resposta), 100, 40));
    }
}
