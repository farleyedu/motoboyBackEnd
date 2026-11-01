namespace APIBack.GestaoLogica.Caixa;

/// <summary>
/// 6C6: presença do motoboy na loja para o Caixa, pela posição que o rastreio já recebe (o mesmo raio do retorno à loja). É só um
/// destaque: nunca mexe na rota, nunca confirma pagamento nem conferência. Regras puras; quem grava é o <see cref="ChegadaService"/>.
/// </summary>
public static class ChegadaRegras
{
    /// <summary>rota (longe), chegando (perto), provavel (no raio com precisão fraca), loja (retorno confirmado), sem_sinal.</summary>
    public const string Rota = "rota", Chegando = "chegando", Provavel = "provavel", Loja = "loja", SemSinal = "sem_sinal";
    public const string SinalOk = "ok", SinalAntigo = "antigo", SinalAusente = "ausente";
    public const string OrigemGps = "gps", OrigemManual = "manual";

    /// <summary>Acima disto (ou sem precisão informada) a posição não muda a presença: evita chegada por GPS de antena.</summary>
    public const double PrecisaoMaxima = 150;
    /// <summary>Para confirmar a chegada a precisão precisa caber no raio (com piso, para raio pequeno não exigir GPS perfeito).</summary>
    public const double PrecisaoParaConfirmar = 50;
    public const int RaioMinimoAproximacao = 400;
    /// <summary>Presença gravada há mais tempo que isto é de outro expediente e não vale.</summary>
    public static readonly TimeSpan Validade = TimeSpan.FromHours(12);

    public static int RaioDeAproximacao(int raio) => Math.Max(raio * 5, RaioMinimoAproximacao);

    /// <summary>
    /// Estado pela posição fresca. Nulo quando a posição não decide (precisão ruim ou ausente): mantém o que estava.
    /// Quem está na loja só sai com posição boa claramente fora (histerese), para o GPS dentro do prédio não piscar.
    /// </summary>
    public static string? Classificar(double distanciaM, double? precisaoM, int raioM, string? anterior)
    {
        if (!double.IsFinite(distanciaM) || distanciaM < 0) return null;
        if (precisaoM is not { } p || !double.IsFinite(p) || p > PrecisaoMaxima) return null;
        if (anterior == Loja && distanciaM <= Math.Max(raioM * 2d, raioM + p)) return Loja;
        if (distanciaM <= raioM && p <= Math.Max(raioM, PrecisaoParaConfirmar)) return Loja;
        if (distanciaM <= raioM + p) return Provavel;
        return distanciaM <= RaioDeAproximacao(raioM) ? Chegando : Rota;
    }

    /// <summary>Sinal pela última posição recebida desta loja.</summary>
    public static string Sinal(DateTimeOffset? posicaoEm, DateTimeOffset agora, int frescorSegundos) =>
        posicaoEm is not { } em ? SinalAusente : em < agora.AddSeconds(-frescorSegundos) ? SinalAntigo : SinalOk;

    /// <summary>
    /// O que o Caixa mostra. Sem posição recente só a chegada confirmada continua valendo (GPS some dentro da loja; a chegada manual
    /// não depende de GPS); o resto vira sem_sinal. Presença velha (outro expediente) não conta.
    /// </summary>
    public static string? Estado(string? gravado, DateTimeOffset? atualizadoEm, string sinal, DateTimeOffset agora)
    {
        var vale = gravado is not null && atualizadoEm is { } em && em >= agora - Validade;
        if (!vale) return sinal == SinalOk ? null : SemSinal;
        if (sinal == SinalOk || gravado == Loja) return gravado;
        return SemSinal;
    }
}
