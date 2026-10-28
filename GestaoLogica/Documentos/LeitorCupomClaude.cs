using System.Globalization;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using APIBack.Acontecimentos;
using Microsoft.Extensions.Logging;

namespace APIBack.GestaoLogica.Documentos;

/// <summary>
/// Lê a foto do cupom com Claude (visão + saída estruturada). Por que IA e não OCR puro: o cupom brasileiro vem abreviado
/// ("QJO MUSS FAT 2KG", "CX HAMB KRAFT M C/25"), torto e às vezes borrado; o OCR devolve texto cru e exigiria um parser
/// frágil. A IA devolve itens, unidades, descontos e a chave da NFC-e já separados. Nada vai para o estoque nem para o
/// financeiro sem uma pessoa conferir. Sem chave (GESTAO_ANTHROPIC_API_KEY ou ANTHROPIC_API_KEY), a leitura falha com
/// LEITURA_INDISPONIVEL e a tela segue pelo lançamento manual.
/// </summary>
public sealed class LeitorCupomClaude(IConfiguration config, ILogger<LeitorCupomClaude> logger) : ILeitorCupomFoto
{
    public const string Modelo = "claude-opus-5-5";

    private const string Instrucao = """
        Você lê fotos de cupons fiscais brasileiros (NFC-e, cupom de atacado, nota de mercado) para um sistema de compras
        de restaurante. Transcreva exatamente o que está impresso, sem corrigir abreviações nem converter unidades:
        a conversão para o estoque é feita depois, por uma pessoa. Cada item é uma linha de produto do cupom, na ordem.
        Quantidade e valores usam ponto decimal. Se um campo não aparece, devolva null. Se uma linha de item está
        ilegível, inclua-a com o que der para ler e "ilegivel": true. A chave de acesso tem 44 dígitos (normalmente perto
        do QR Code); devolva só os dígitos. Desconto de item vai no item; desconto geral vai em "desconto".
        """;

    private static readonly Dictionary<string, JsonElement> Schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""
    {
      "type": "object", "additionalProperties": false,
      "required": ["fornecedor_nome", "fornecedor_cnpj", "data_emissao", "numero", "serie", "chave_acesso", "itens", "desconto", "total"],
      "properties": {
        "fornecedor_nome": {"type": ["string", "null"]},
        "fornecedor_cnpj": {"type": ["string", "null"]},
        "data_emissao": {"type": ["string", "null"], "description": "AAAA-MM-DDTHH:MM, horário impresso"},
        "numero": {"type": ["string", "null"]},
        "serie": {"type": ["string", "null"]},
        "chave_acesso": {"type": ["string", "null"]},
        "desconto": {"type": "number"},
        "total": {"type": "number"},
        "itens": {"type": "array", "items": {
          "type": "object", "additionalProperties": false,
          "required": ["descricao", "codigo", "quantidade", "unidade", "valor_unitario", "desconto", "valor_total", "ilegivel"],
          "properties": {
            "descricao": {"type": "string"}, "codigo": {"type": ["string", "null"]},
            "quantidade": {"type": "number"}, "unidade": {"type": "string"},
            "valor_unitario": {"type": "number"}, "desconto": {"type": "number"},
            "valor_total": {"type": "number"}, "ilegivel": {"type": "boolean"}
          }
        }}
      }
    }
    """)!;

    public async Task<DocumentoLido> Ler(byte[] imagem, string mediaType, CancellationToken ct)
    {
        var chave = config["GESTAO_ANTHROPIC_API_KEY"] ?? config["ANTHROPIC_API_KEY"];
        if (string.IsNullOrWhiteSpace(chave))
            throw new GestaoException("LEITURA_INDISPONIVEL", "A leitura automática não está configurada. Lance os itens à mão.", 503);
        var client = new AnthropicClient { ApiKey = chave };
        Message resposta;
        try
        {
            resposta = await client.Messages.Create(new MessageCreateParams
            {
                Model = Modelo,
                MaxTokens = 16000,
                System = Instrucao,
                OutputConfig = new OutputConfig { Effort = Effort.Medium, Format = new JsonOutputFormat { Schema = Schema } },
                Messages =
                [
                    new UserMessageParam
                    {
                        Content = new List<ContentBlockParam>
                        {
                            new ImageBlockParam { Source = new Base64ImageSource { Data = Convert.ToBase64String(imagem), MediaType = MidiaDe(mediaType) } },
                            new TextBlockParam { Text = "Leia este cupom." },
                        },
                    },
                ],
            }, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Falha ao ler cupom com {Modelo}", Modelo);
            throw new GestaoException("LEITURA_FALHOU", "Não foi possível ler a foto agora. Tente de novo ou lance os itens à mão.", 502);
        }
        if (resposta.StopReason == StopReason.Refusal || resposta.StopReason == StopReason.MaxTokens)
            throw new GestaoException("LEITURA_FALHOU", "A foto não pôde ser lida. Lance os itens à mão.", 502);
        var texto = string.Concat(resposta.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        return Interpretar(texto);
    }

    /// <summary>JSON da IA → documento lido. Público para teste.</summary>
    public static DocumentoLido Interpretar(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var itens = new List<ItemLido>();
        foreach (var i in r.GetProperty("itens").EnumerateArray())
            itens.Add(new ItemLido(itens.Count + 1, Txt(i, "descricao") ?? "", Dec(i, "quantidade"),
                (Txt(i, "unidade") ?? "UN").ToUpperInvariant(), Dec(i, "valor_unitario"), Dec(i, "desconto"),
                Dec(i, "valor_total"), Txt(i, "codigo"), Ilegivel: i.TryGetProperty("ilegivel", out var il) && il.ValueKind == JsonValueKind.True));
        var chave = DocumentoRegras.SoDigitos(Txt(r, "chave_acesso"));
        var total = Dec(r, "total");
        var data = DateTime.TryParse(Txt(r, "data_emissao"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? new DateTimeOffset(d, TimeSpan.FromHours(-3)) : (DateTimeOffset?)null;
        return new DocumentoLido(OrigemDocumento.Foto, new FornecedorLido(DocumentoRegras.SoDigitos(Txt(r, "fornecedor_cnpj")),
                Txt(r, "fornecedor_nome")), data, Txt(r, "numero"), Txt(r, "serie"), chave?.Length == 44 ? chave : null,
            itens, Dec(r, "desconto"), total, DocumentoRegras.Conferir(itens, total));
    }

    private static string? Txt(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static decimal Dec(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

    private static MediaType MidiaDe(string tipo) => tipo.ToLowerInvariant() switch
    {
        "image/png" => MediaType.ImagePng,
        "image/webp" => MediaType.ImageWebP,
        "image/gif" => MediaType.ImageGif,
        _ => MediaType.ImageJpeg,
    };
}
