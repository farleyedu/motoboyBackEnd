namespace APIBack.Equipe.Ponto;

/// <summary>Regras puras do ponto (P02): dia e hora no fuso da loja, minutos com pausas e o que fazer com o turno ao sair.</summary>
public static class PontoRegras
{
    public const string Garcom = "garcom", Motoboy = "motoboy", Cozinha = "cozinha", Caixa = "caixa";
    public static readonly string[] Modos = { Garcom, Motoboy, Cozinha, Caixa };

    public const string Ativo = "ativo", Pausa = "pausa", Encerrado = "encerrado";
    public const string Preenchido = "preenchido", Mantido = "mantido", NaoAplicado = "nao_aplicado";
    public const int MotivoMax = 280, MotivoPausaMax = 140;

    /// <summary>Modo do ponto quando o app não manda: vem da função da pessoa (ou do vínculo de motoboy).</summary>
    public static string? ModoPadrao(string? funcaoCodigo, string? vinculo) => funcaoCodigo switch
    {
        "garcom" or "gerente_salao" => Garcom,
        "cozinha" or "bar" => Cozinha,
        "caixa" => Caixa,
        _ => vinculo == "motoboy" ? Motoboy : null,
    };

    /// <summary>Dia do ponto = data local da loja na hora da entrada (o relógio do aparelho não decide).</summary>
    public static DateOnly DiaLocal(DateTimeOffset quando, TimeZoneInfo fuso) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(quando, fuso).DateTime);

    public static string HoraLocal(DateTimeOffset quando, TimeZoneInfo fuso) =>
        TimeZoneInfo.ConvertTime(quando, fuso).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Instantes [início, fim) de um dia local, em UTC, para buscar os pontos daquele dia mesmo em virada de horário.</summary>
    public static (DateTimeOffset Inicio, DateTimeOffset Fim) LimitesDoDia(DateOnly dia, TimeZoneInfo fuso)
    {
        static DateTimeOffset Local(DateTime d, TimeZoneInfo f) => new(d, f.GetUtcOffset(d));
        var ini = dia.ToDateTime(TimeOnly.MinValue);
        return (Local(ini, fuso), Local(ini.AddDays(1), fuso));
    }

    public static int Minutos(DateTimeOffset de, DateTimeOffset ate) => ate <= de ? 0 : (int)Math.Floor((ate - de).TotalMinutes);

    /// <summary>Pausa aberta conta até <paramref name="agora"/>.</summary>
    public static int MinutosPausa(IEnumerable<(DateTimeOffset Inicio, DateTimeOffset? Fim)> pausas, DateTimeOffset agora) =>
        pausas.Sum(p => Minutos(p.Inicio, p.Fim ?? agora));

    /// <summary>Tempo de trabalho = da entrada à saída (ou agora) menos as pausas.</summary>
    public static int MinutosTrabalhados(DateTimeOffset entrada, DateTimeOffset? saida, IEnumerable<(DateTimeOffset Inicio, DateTimeOffset? Fim)> pausas,
        DateTimeOffset agora)
    {
        var fim = saida ?? agora;
        return Math.Max(0, Minutos(entrada, fim) - MinutosPausa(pausas, fim));
    }

    public static string Estado(bool aberto, bool pausado) => !aberto ? Encerrado : pausado ? Pausa : Ativo;

    /// <summary>
    /// O que fazer com o turno ao sair. Nunca apaga o que o gerente já decidiu: só preenche turno inexistente ou previsto fora do
    /// livro, ou estende a saída de um turno que o próprio ponto preencheu antes no mesmo dia e que ninguém mexeu depois
    /// (o horário gravado continua igual ao que o ponto escreveu).
    /// </summary>
    public static DecisaoTurno DecidirTurno(TurnoAtual? turno, string entradaPonto, string saidaPonto, PontoAnteriorNoDia? anterior)
    {
        if (turno is { Status: "realizado" } && anterior is { TurnoSituacao: Preenchido, TurnoEntrada: { } e, TurnoSaida: { } s }
            && turno.Entrada == e && turno.Saida == s)
        {
            return e == saidaPonto
                ? new(AcaoTurno.NaoAplicar, Aviso: "Jornada do dia fechou no mesmo minuto da entrada: o turno não mudou.")
                : new(AcaoTurno.Preencher, e, saidaPonto, "Ponto: nova saída no mesmo dia.");
        }
        if (turno is not null && (turno.Status != "previsto" || turno.NoLivro))
            return new(AcaoTurno.Manter, Aviso: "O gerente já decidiu este turno: ficou como estava.");
        if (entradaPonto == saidaPonto)
            return new(AcaoTurno.NaoAplicar, Aviso: "Ponto com menos de um minuto: o turno não foi preenchido.");
        return new(AcaoTurno.Preencher, entradaPonto, saidaPonto);
    }

    public static string? Limpar(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();
}
