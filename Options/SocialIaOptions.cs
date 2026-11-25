namespace APIBack.Options;

/// <summary>
/// IA e voz do Zippy Social (seção <c>Social:Ia</c>). Os números de crédito são provisórios (protótipo do editor, P6) e ficam aqui,
/// não no código: o dono ajusta quando souber o preço real por minuto.
/// Chaves da Zippy por variável de ambiente: <c>Social__Ia__Chaves__elevenlabs</c>, <c>__openai</c>, <c>__azure_speech</c>,
/// <c>__anthropic</c> (sem esta, usa a do Assistente). Azure precisa de <c>Social__Ia__AzureEndpoint</c>.
/// </summary>
public sealed class SocialIaOptions
{
    public const string Secao = "Social:Ia";

    /// <summary>Créditos que cada loja recebe no mês enquanto o plano (<c>pacotes.social_creditos_mes</c>) não existir.</summary>
    public int CreditosMesPadrao { get; set; } = 100;

    public int VozSegundosPorCredito { get; set; } = 30;
    public int CreditosVozClone { get; set; } = 10;
    public int CreditosRoteiro { get; set; } = 1;
    public int CreditosEncurtar { get; set; } = 1;
    public int CreditosGanchos { get; set; } = 1;

    /// <summary>Proteção da chave da Zippy: chamadas por minuto por loja.</summary>
    public int LimitePorMinuto { get; set; } = 20;

    /// <summary>Proteção da chave da Zippy: créditos por dia por loja.</summary>
    public int TetoDiarioCreditos { get; set; } = 60;

    /// <summary>Provedor padrão quando a loja não escolheu (origem Zippy).</summary>
    public string ProvedorVoz { get; set; } = "openai";
    public string ProvedorVozClone { get; set; } = "elevenlabs";
    public string ProvedorTexto { get; set; } = "anthropic";
    public string ModeloTexto { get; set; } = "claude-haiku-5-5";

    /// <summary>
    /// Sem chave da Zippy para o provedor, usa o provedor falso (áudio em silêncio, texto de exemplo). Ligado só em desenvolvimento
    /// (Program.cs); em produção o pedido falha com aviso.
    /// </summary>
    public bool FalsoSemChave { get; set; }

    public Dictionary<string, string> Chaves { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ex.: <c>https://brazilsouth.tts.speech.microsoft.com</c>.</summary>
    public string? AzureEndpoint { get; set; }
}
