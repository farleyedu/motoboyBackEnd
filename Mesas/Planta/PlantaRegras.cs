using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace APIBack.Mesas.Planta;

/// <summary>Um problema da planta, ligado ao item que o editor deve destacar (<c>planta</c>, <c>piso</c> ou <c>mesa</c>).</summary>
public sealed record PlantaErro(string Item, string? Id, string Codigo, string Mensagem);

public sealed record PlantaAndarDoc(string Chave, string Nome, short Nivel);
public sealed record PlantaAreaDoc(string Chave, string PisoId, string Nome, string? Cor);
public sealed record PlantaMesaDoc(string Chave, string Numero, string PisoId, string? SetorId, short Capacidade, string Forma, bool Reservavel);
public sealed record PlantaLida(IReadOnlyList<PlantaAndarDoc> Andares, IReadOnlyList<PlantaAreaDoc> Areas, IReadOnlyList<PlantaMesaDoc> Mesas);

/// <summary>Mesa que já está no cadastro, com o que importa para comparar com a planta nova.</summary>
public sealed record PlantaMesaAtual(Guid Id, string Chave, string Numero, bool Ativa, bool EmUso);

public sealed record PlantaMesaMudanca(Guid Id, PlantaMesaDoc Nova, string? NumeroAntes, bool Reativar);

/// <summary>O que publicar faz no cadastro de mesas. <see cref="Presas"/> não vazio = publicar é recusado.</summary>
public sealed record PlantaDiferenca(
    IReadOnlyList<PlantaMesaDoc> Criar,
    IReadOnlyList<PlantaMesaMudanca> Atualizar,
    IReadOnlyList<PlantaMesaAtual> Desativar,
    IReadOnlyList<PlantaErro> Presas)
{
    public IEnumerable<PlantaMesaMudanca> Renumeradas => Atualizar.Where(a => a.NumeroAntes is not null);
}

/// <summary>
/// Regras da planta (CONTRATO-API §1.3 e §3.3), sem banco. O documento segue o protótipo (<c>ZM.dados</c>):
/// <c>{ pisos:[{id, nome, nivel, contorno:[[x,y]...], setores:[{id, nome, cor}], elementos:[{tipo, principal}]}], mesas:[{id, numero,
/// pisoId, setorId, forma, capacidade | cadeiras, x, y}] }</c>, medidas em cm. A chave estável da mesa é o <c>id</c> dela no documento.
/// </summary>
public static class PlantaRegras
{
    public const int TamanhoMaximo = 5 * 1024 * 1024;
    public static readonly string[] Formas = { "redonda", "quadrada", "retangular", "oval" };

    /// <summary>Para salvar o rascunho basta o formato: um objeto com <c>pisos</c> e <c>mesas</c> em lista. O resto o editor completa.</summary>
    public static string? ValidarFormato(JsonNode? documento, int tamanho)
    {
        if (documento is not JsonObject doc) return "Envie a planta.";
        if (tamanho > TamanhoMaximo) return "A planta ficou grande demais. Tire a foto de fundo ou diminua a imagem.";
        if (doc["pisos"] is not JsonArray) return "A planta precisa da lista de andares.";
        if (doc["mesas"] is not JsonArray) return "A planta precisa da lista de mesas.";
        return null;
    }

    /// <summary>Tudo o que impede publicar, um erro por item. Sem erro, devolve a planta lida para gravar o cadastro.</summary>
    public static (PlantaLida? Lida, IReadOnlyList<PlantaErro> Erros) ValidarParaPublicar(JsonObject documento)
    {
        var erros = new List<PlantaErro>();
        var pisos = (documento["pisos"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        var mesas = (documento["mesas"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();

        var andares = new List<PlantaAndarDoc>();
        var areas = new List<PlantaAreaDoc>();
        var contornos = new Dictionary<string, double[][]>(StringComparer.Ordinal);
        var setoresDoPiso = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var entrada = false;

        if (pisos.Count == 0) erros.Add(new("planta", null, "SEM_ANDAR", "Desenhe pelo menos um andar."));
        foreach (var p in pisos)
        {
            var id = Texto(p["id"]);
            var nome = Texto(p["nome"]) ?? "Salão";
            if (id is null || id.Length > 80) { erros.Add(new("piso", id, "ANDAR_SEM_ID", $"O andar {nome} está sem identificação.")); continue; }
            if (contornos.ContainsKey(id)) { erros.Add(new("piso", id, "ANDAR_REPETIDO", $"O andar {nome} aparece duas vezes.")); continue; }

            var contorno = Pontos(p["contorno"]);
            if (contorno.Length < 3)
                erros.Add(new("piso", id, "CONTORNO", $"Feche o contorno de {nome}: ele precisa de pelo menos 3 cantos."));
            else if (Math.Abs(Area(contorno)) < 1)
                erros.Add(new("piso", id, "CONTORNO", $"O contorno de {nome} não forma uma área."));
            contornos[id] = contorno;

            andares.Add(new(id, Corta(nome, 80), (short)Math.Clamp(Inteiro(p["nivel"]) ?? 0, -10, 50)));
            var setores = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in (p["setores"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var sid = Texto(s["id"]);
                if (sid is null || sid.Length > 80 || !setores.Add(sid)) continue;
                areas.Add(new(sid, id, Corta(Texto(s["nome"]) ?? "Área", 80), Texto(s["cor"]) is { Length: <= 20 } cor ? cor : null));
            }
            setoresDoPiso[id] = setores;

            entrada |= (p["elementos"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Any(e =>
                Texto(e["tipo"]) == "entrada" || (Texto(e["tipo"]) == "porta" && e["principal"] is JsonValue v && v.TryGetValue<bool>(out var b) && b));
        }
        if (pisos.Count > 0 && !entrada) erros.Add(new("planta", null, "SEM_ENTRADA", "Marque a porta de entrada do salão."));

        if (mesas.Count == 0) erros.Add(new("planta", null, "SEM_MESA", "Ponha pelo menos uma mesa na planta."));
        var lidas = new List<PlantaMesaDoc>();
        var chaves = new HashSet<string>(StringComparer.Ordinal);
        var numeros = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mesas)
        {
            var id = Texto(m["id"]);
            var numero = Texto(m["numero"]);
            var rotulo = numero is null ? "Uma mesa" : $"Mesa {numero}";
            if (id is null || id.Length > 80) { erros.Add(new("mesa", id, "MESA_SEM_ID", $"{rotulo} está sem identificação.")); continue; }
            if (!chaves.Add(id)) { erros.Add(new("mesa", id, "MESA_REPETIDA", $"{rotulo} aparece duas vezes.")); continue; }

            if (numero is null) { erros.Add(new("mesa", id, "NUMERO_VAZIO", "Uma mesa está sem número.")); continue; }
            if (numero.Length > 20) { erros.Add(new("mesa", id, "NUMERO_LONGO", $"O número da {rotulo} é longo demais.")); continue; }
            numeros[numero] = numeros.GetValueOrDefault(numero) + 1;

            var pisoId = Texto(m["pisoId"]);
            if (pisoId is null || !contornos.TryGetValue(pisoId, out var contorno))
            {
                erros.Add(new("mesa", id, "MESA_SEM_ANDAR", $"{rotulo} está num andar que não existe."));
                continue;
            }
            var setorId = Texto(m["setorId"]);
            if (setorId is not null && !setoresDoPiso[pisoId].Contains(setorId))
                erros.Add(new("mesa", id, "MESA_SEM_AREA", $"{rotulo} está numa área que não existe."));

            var capacidade = Inteiro(m["capacidade"]) ?? (m["cadeiras"] as JsonArray)?.Count ?? 0;
            if (capacidade is < 1 or > 100) erros.Add(new("mesa", id, "LUGARES", $"{rotulo} precisa de 1 a 100 lugares."));
            var forma = Texto(m["forma"]);
            if (forma is null || !Formas.Contains(forma, StringComparer.Ordinal))
                erros.Add(new("mesa", id, "FORMA", $"{rotulo} está com um formato que não existe."));

            if (contorno.Length >= 3 && Numero(m["x"]) is double x && Numero(m["y"]) is double y && !Dentro(contorno, x, y))
                erros.Add(new("mesa", id, "FORA_DO_CONTORNO", $"{rotulo} está fora do contorno do andar."));

            var reservavel = m["reservavel"] is not JsonValue r || !r.TryGetValue<bool>(out var rb) || rb;
            lidas.Add(new(id, numero, pisoId, setorId, (short)Math.Clamp(capacidade, 0, 100), forma ?? "", reservavel));
        }
        foreach (var (numero, vezes) in numeros.Where(n => n.Value > 1))
            foreach (var m in lidas.Where(l => string.Equals(l.Numero, numero, StringComparison.OrdinalIgnoreCase)))
                erros.Add(new("mesa", m.Chave, "NUMERO_REPETIDO", $"Mesa {numero} aparece {vezes} vezes. Cada mesa precisa de um número só dela."));

        return erros.Count > 0 ? (null, erros) : (new PlantaLida(andares, areas, lidas), erros);
    }

    /// <summary>
    /// Compara a planta nova com o cadastro. A mesa é a mesma enquanto a chave da planta for a mesma: renumerar não cria mesa nova,
    /// então sessões, pedidos e auditoria continuam presos a ela. Mesa que sai da planta é desativada, nunca apagada; em uso, recusa.
    /// </summary>
    public static PlantaDiferenca Comparar(IReadOnlyList<PlantaMesaAtual> atuais, IReadOnlyList<PlantaMesaDoc> novas)
    {
        var porChave = atuais.ToDictionary(a => a.Chave, StringComparer.Ordinal);
        var criar = new List<PlantaMesaDoc>();
        var atualizar = new List<PlantaMesaMudanca>();
        foreach (var n in novas)
        {
            if (!porChave.TryGetValue(n.Chave, out var a)) { criar.Add(n); continue; }
            atualizar.Add(new(a.Id, n, a.Ativa && a.Numero != n.Numero ? a.Numero : null, !a.Ativa));
        }
        var chavesNovas = novas.Select(n => n.Chave).ToHashSet(StringComparer.Ordinal);
        var desativar = atuais.Where(a => a.Ativa && !chavesNovas.Contains(a.Chave)).ToList();
        var presas = desativar.Where(a => a.EmUso)
            .Select(a => new PlantaErro("mesa", a.Chave, "MESA_EM_USO", $"Não dá para tirar a Mesa {a.Numero}: ela está em uso."))
            .ToList();
        return new(criar, atualizar, desativar, presas);
    }

    private static string? Texto(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        string? s = v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.Number => Numero(v)?.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>Número vindo do JSON lido ou montado em código (int, long, double): lê pelo texto para não depender do tipo guardado.</summary>
    private static double? Numero(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.Number
        && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;

    private static int? Inteiro(JsonNode? n) => Numero(n) is double d ? (int)Math.Round(d) : null;

    private static string Corta(string s, int max) => s.Length <= max ? s : s[..max];

    private static double[][] Pontos(JsonNode? n) =>
        (n as JsonArray ?? new JsonArray()).OfType<JsonArray>()
            .Select(p => p.Count >= 2 && Numero(p[0]) is double x && Numero(p[1]) is double y ? new[] { x, y } : null)
            .Where(p => p is not null).Select(p => p!).ToArray();

    private static double Area(double[][] c)
    {
        double s = 0;
        for (var i = 0; i < c.Length; i++) { var a = c[i]; var b = c[(i + 1) % c.Length]; s += a[0] * b[1] - b[0] * a[1]; }
        return s / 2;
    }

    /// <summary>Ponto dentro do polígono (borda conta como dentro, para não recusar mesa encostada na parede).</summary>
    private static bool Dentro(double[][] c, double x, double y)
    {
        var dentro = false;
        for (int i = 0, j = c.Length - 1; i < c.Length; j = i++)
        {
            double xi = c[i][0], yi = c[i][1], xj = c[j][0], yj = c[j][1];
            var cruz = (xj - xi) * (y - yi) - (yj - yi) * (x - xi);
            if (Math.Abs(cruz) < 1e-6 && x >= Math.Min(xi, xj) && x <= Math.Max(xi, xj) && y >= Math.Min(yi, yj) && y <= Math.Max(yi, yj)) return true;
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) dentro = !dentro;
        }
        return dentro;
    }
}
