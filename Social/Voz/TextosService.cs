using System.Globalization;
using System.Text;
using System.Text.Json;
using APIBack.Social.Ia;
using APIBack.Social.Nucleo;
using Dapper;
using Npgsql;

namespace APIBack.Social.Voz;

public sealed record RoteiroRequest(long CorteId, string? Estilo);
public sealed record EncurtarRequest(string? Texto, int? MaxPalavras);
public sealed record GanchosRequest(long CorteId);

/// <summary>Fala do roteiro com âncora: <c>inicio</c>, <c>fim</c>, <c>antes:&lt;marca&gt;</c> ou <c>depois:&lt;marca&gt;</c>.</summary>
public sealed record FalaRoteiroDto(string Id, string Texto, string Ancora);
public sealed record RoteiroDto(IReadOnlyList<FalaRoteiroDto> Falas, int Creditos, int Restam, string Origem, string? Aviso);
public sealed record EncurtarDto(string Texto, int Creditos, int Restam, string Origem, string? Aviso);
public sealed record GanchosDto(IReadOnlyList<string> Ganchos, int Creditos, int Restam, string Origem, string? Aviso);

/// <summary>Regras puras dos textos: contexto do corte, leitura da resposta da IA e âncoras.</summary>
public static class TextosRegras
{
    public static readonly string[] Estilos = { "contexto", "historia", "comentario", "gancho" };
    public const int MaxFalasRoteiro = 4, MaxPalavrasFala = 25;

    public sealed record Corte(string Titulo, decimal Duracao, IReadOnlyList<string> Falas, IReadOnlyList<string> Marcas);

    /// <summary>
    /// Lê os dados do corte (R02, <c>social_cortes.dados</c>): <c>falas[{quem,t,txt}]</c>, <c>momentos[{id?,k,t?}]</c> e, no corte de
    /// exemplo, <c>eventos[]</c> (texto). As marcas valem como âncora <c>antes:</c>/<c>depois:</c>.
    /// </summary>
    public static Corte LerCorte(string titulo, decimal duracao, string? dadosJson)
    {
        var falas = new List<string>();
        var marcas = new List<string>();
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(dadosJson) ? "{}" : dadosJson);
        var raiz = doc.RootElement;
        if (raiz.ValueKind == JsonValueKind.Object)
        {
            if (raiz.TryGetProperty("falas", out var fs) && fs.ValueKind == JsonValueKind.Array)
                foreach (var f in fs.EnumerateArray().Take(200))
                {
                    var txt = Str(f, "txt") ?? Str(f, "texto");
                    if (string.IsNullOrWhiteSpace(txt)) continue;
                    var t = f.TryGetProperty("t", out var tv) && tv.TryGetDecimal(out var td) ? td.ToString("0.0", CultureInfo.InvariantCulture) + "s " : "";
                    var quem = Str(f, "quem");
                    falas.Add($"{t}{(quem is null ? "" : quem + ": ")}{txt}");
                }
            if (raiz.TryGetProperty("momentos", out var ms) && ms.ValueKind == JsonValueKind.Array)
                foreach (var m in ms.EnumerateArray().Take(50))
                    if ((Str(m, "id") ?? Str(m, "k")) is { Length: > 0 } marca) marcas.Add(marca);
            if (raiz.TryGetProperty("eventos", out var es) && es.ValueKind == JsonValueKind.Array)
                foreach (var e in es.EnumerateArray().Take(50))
                    if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } ev) marcas.Add(ev);
        }
        return new Corte(titulo, duracao, falas, marcas.Distinct(StringComparer.Ordinal).ToList());
    }

    public static string Contexto(Corte c)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Título do corte: {c.Titulo}");
        sb.AppendLine($"Duração: {c.Duracao.ToString("0", CultureInfo.InvariantCulture)} segundos");
        sb.AppendLine(c.Marcas.Count == 0 ? "Momentos marcados: nenhum" : $"Momentos marcados (marcas): {string.Join(", ", c.Marcas)}");
        sb.AppendLine(c.Falas.Count == 0 ? "Transcrição: (sem fala)" : "Transcrição:\n" + string.Join("\n", c.Falas));
        var texto = sb.ToString();
        return texto.Length <= 12000 ? texto : texto[..12000];
    }

    public const string Sistema =
        "Você escreve narração curta para vídeos verticais (Shorts, Reels, TikTok) em português do Brasil. Frases curtas, faladas, " +
        "sem hashtag, sem emoji e sem inventar fatos que não estão no corte. Responda só com JSON válido, sem texto antes ou depois.";

    public static string PedidoRoteiro(string estilo, IReadOnlyList<string> marcas) =>
        $$"""
        Escreva a narração no estilo "{{estilo}}" ({{DescricaoEstilo(estilo)}}).
        De 1 a {{MaxFalasRoteiro}} falas, cada uma com no máximo {{MaxPalavrasFala}} palavras.
        Cada fala tem uma âncora: "inicio", "fim", "antes:<marca>" ou "depois:<marca>", usando só estas marcas: {{(marcas.Count == 0 ? "(nenhuma, use inicio ou fim)" : string.Join(", ", marcas))}}.
        Formato: {"falas":[{"texto":"...","ancora":"inicio"}]}
        """;

    public static string PedidoEncurtar(int max) =>
        $$"""Reescreva a fala abaixo com no máximo {{max}} palavras, mantendo o sentido. Formato: {"texto":"..."}""";

    public const string PedidoGanchos =
        """Escreva 3 ganchos diferentes para os 2 primeiros segundos do vídeo (até 8 palavras cada). Formato: {"ganchos":["...","...","..."]}""";

    private static string DescricaoEstilo(string estilo) => estilo switch
    {
        "contexto" => "explica o que está acontecendo para quem chegou agora",
        "historia" => "conta como uma pequena história, com começo e final",
        "comentario" => "comenta o lance com opinião e humor leve",
        _ => "abre com uma frase que prende nos primeiros segundos",
    };

    /// <summary>Pega o primeiro objeto JSON da resposta (o modelo às vezes cerca com ```json). Sem JSON: resposta inválida.</summary>
    public static JsonElement Json(string resposta, string provedor)
    {
        var ini = resposta.IndexOf('{');
        var fim = resposta.LastIndexOf('}');
        if (ini < 0 || fim <= ini) throw new FalhaIaException(provedor, "resposta sem JSON", TipoFalhaIa.ForaDoAr);
        try
        {
            using var doc = JsonDocument.Parse(resposta[ini..(fim + 1)]);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new FalhaIaException(provedor, "JSON inválido", TipoFalhaIa.ForaDoAr, ex);
        }
    }

    public static string Ancora(string? ancora, IReadOnlyList<string> marcas, bool ultima)
    {
        var a = (ancora ?? "").Trim();
        if (a is "inicio" or "fim") return a;
        foreach (var lado in new[] { "antes:", "depois:" })
            if (a.StartsWith(lado, StringComparison.Ordinal) && marcas.Contains(a[lado.Length..])) return a;
        return ultima ? "fim" : "inicio";
    }

    public static IReadOnlyList<FalaRoteiroDto> Falas(JsonElement json, IReadOnlyList<string> marcas, string provedor)
    {
        if (!json.TryGetProperty("falas", out var fs) || fs.ValueKind != JsonValueKind.Array)
            throw new FalhaIaException(provedor, "resposta sem falas", TipoFalhaIa.ForaDoAr);
        var brutas = fs.EnumerateArray().Select(f => (Texto: Limitar(Str(f, "texto"), MaxPalavrasFala), Ancora: Str(f, "ancora")))
            .Where(f => f.Texto.Length > 0).Take(MaxFalasRoteiro).ToList();
        if (brutas.Count == 0) throw new FalhaIaException(provedor, "resposta sem falas", TipoFalhaIa.ForaDoAr);
        return brutas.Select((f, i) => new FalaRoteiroDto(NovoId(), f.Texto, Ancora(f.Ancora, marcas, i == brutas.Count - 1 && i > 0)))
            .ToList();
    }

    /// <summary>Corta em <paramref name="max"/> palavras (a IA às vezes passa do pedido).</summary>
    public static string Limitar(string? texto, int max)
    {
        var palavras = (texto ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', palavras.Take(max));
    }

    public static string NovoId() => Guid.NewGuid().ToString("N")[..8];

    private static string? Str(JsonElement e, string nome) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public interface ISocialTextosService
{
    Task<RoteiroDto> RoteiroAsync(Guid loja, int usuario, RoteiroRequest req, CancellationToken ct);
    Task<EncurtarDto> EncurtarAsync(Guid loja, int usuario, EncurtarRequest req, CancellationToken ct);
    Task<GanchosDto> GanchosAsync(Guid loja, int usuario, GanchosRequest req, CancellationToken ct);
}

/// <summary>Roteiro, encurtar fala e ganchos (Claude Haiku 5.5 por padrão). 1 crédito cada, na cota da Zippy.</summary>
public sealed class SocialTextosService(NpgsqlDataSource fonte, SocialIa ia, IFabricaTexto fabrica) : ISocialTextosService
{
    private sealed class CorteLinha
    {
        public string Titulo { get; set; } = "";
        public decimal Ini { get; set; }
        public decimal Fim { get; set; }
        public string Dados { get; set; } = "{}";
    }

    public async Task<RoteiroDto> RoteiroAsync(Guid loja, int usuario, RoteiroRequest req, CancellationToken ct)
    {
        var estilo = (req.Estilo ?? "").Trim().ToLowerInvariant();
        if (!TextosRegras.Estilos.Contains(estilo)) throw SocialException.Validacao("Estilo do roteiro inválido.");
        var corte = await CorteAsync(loja, req.CorteId, ct);
        var (json, p, aviso, saldo, creditos) = await GerarAsync(loja, usuario, UsosIa.Roteiro, ia.Op.CreditosRoteiro, req.CorteId,
            TextosRegras.Contexto(corte), TextosRegras.PedidoRoteiro(estilo, corte.Marcas), 800,
            (j, prov) => TextosRegras.Falas(j, corte.Marcas, prov), ct);
        return new RoteiroDto(json, creditos, saldo, p.Origem, aviso);
    }

    public async Task<EncurtarDto> EncurtarAsync(Guid loja, int usuario, EncurtarRequest req, CancellationToken ct)
    {
        var texto = VozRegras.Texto(req.Texto);
        var max = req.MaxPalavras ?? 12;
        if (max is < 3 or > 60) throw SocialException.Validacao("O máximo de palavras vai de 3 a 60.");
        var (curto, p, aviso, saldo, creditos) = await GerarAsync(loja, usuario, UsosIa.Textos, ia.Op.CreditosEncurtar, null,
            $"Fala: {texto}", TextosRegras.PedidoEncurtar(max), 300,
            (j, prov) => j.TryGetProperty("texto", out var t) && t.ValueKind == JsonValueKind.String && TextosRegras.Limitar(t.GetString(), max) is { Length: > 0 } s
                ? s : throw new FalhaIaException(prov, "resposta sem texto", TipoFalhaIa.ForaDoAr), ct);
        return new EncurtarDto(curto, creditos, saldo, p.Origem, aviso);
    }

    public async Task<GanchosDto> GanchosAsync(Guid loja, int usuario, GanchosRequest req, CancellationToken ct)
    {
        var corte = await CorteAsync(loja, req.CorteId, ct);
        var (ganchos, p, aviso, saldo, creditos) = await GerarAsync(loja, usuario, UsosIa.Textos, ia.Op.CreditosGanchos, req.CorteId,
            TextosRegras.Contexto(corte), TextosRegras.PedidoGanchos, 300,
            (j, prov) =>
            {
                var lista = j.TryGetProperty("ganchos", out var g) && g.ValueKind == JsonValueKind.Array
                    ? g.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => TextosRegras.Limitar(x.GetString(), 12))
                        .Where(s => s.Length > 0).Take(3).ToList()
                    : new List<string>();
                return lista.Count == 3 ? (IReadOnlyList<string>)lista : throw new FalhaIaException(prov, "resposta sem 3 ganchos", TipoFalhaIa.ForaDoAr);
            }, ct);
        return new GanchosDto(ganchos, creditos, saldo, p.Origem, aviso);
    }

    private async Task<(T Valor, ProvedorEscolhido P, string? Aviso, int Restam, int Creditos)> GerarAsync<T>(Guid loja, int usuario,
        string uso, int creditos, long? corteId, string contexto, string pedido, int maxTokens, Func<JsonElement, string, T> ler,
        CancellationToken ct)
    {
        var r = await ia.ExecutarAsync(loja, uso, creditos, async (p, c) =>
        {
            var modelo = p.ModeloDe("texto") ?? ia.Op.ModeloTexto;
            var resposta = await fabrica.Criar(p).GerarAsync(new PedidoTexto(TextosRegras.Sistema, contexto, pedido, maxTokens, modelo), c);
            return (Valor: ler(TextosRegras.Json(resposta.Texto, p.ProvedorId), p.ProvedorId), Resposta: resposta, Modelo: modelo);
        }, ct);
        var p = r.Provedor;
        var (valor, resp, modelo) = r.Valor;
        var custo = Math.Round(resp.TokensEntrada / 1_000_000m * p.Preco("texto_entrada_por_milhao")
                             + resp.TokensSaida / 1_000_000m * p.Preco("texto_saida_por_milhao"), 6);
        var saldo = await SocialBanco.NaLoja(fonte, loja, ct, (c, tx) => ia.CobrarAsync(c, tx, loja, usuario, p,
            new UsoIa(uso, p.ProvedorId, modelo, p.Origem, resp.TokensEntrada + resp.TokensSaida, "tokens", creditos, custo, corteId,
                new { entrada = resp.TokensEntrada, saida = resp.TokensSaida }), ct));
        return (valor, p, r.Aviso, saldo.Restam, p.Origem == OrigensIa.Zippy ? creditos : 0);
    }

    private async Task<TextosRegras.Corte> CorteAsync(Guid loja, long corteId, CancellationToken ct)
    {
        if (corteId <= 0) throw SocialException.Validacao("Informe o corte.");
        var l = await SocialBanco.NaLoja(fonte, loja, ct, (c, tx) => c.QueryFirstOrDefaultAsync<CorteLinha>(new CommandDefinition("""
            SELECT titulo AS "Titulo", ini AS "Ini", fim AS "Fim", dados::text AS "Dados"
              FROM social_cortes WHERE id = @corteId AND estabelecimento_id = @loja
            """, new { corteId, loja }, tx, cancellationToken: ct))) ?? throw SocialException.NaoEncontrado("Corte não encontrado.");
        return TextosRegras.LerCorte(l.Titulo, l.Fim - l.Ini, l.Dados);
    }
}
