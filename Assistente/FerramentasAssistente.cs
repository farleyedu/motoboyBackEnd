using System.Text.Json;
using System.Text.Json.Nodes;

namespace APIBack.Assistente;

/// <summary>
/// Definição (JSON Schema, strict) e leitura das quatro ferramentas da IA. Sem rede nem banco: o serviço executa o que daqui sai.
/// </summary>
public static class FerramentasAssistente
{
    public const string Consultar = "consultar";
    public const string ProporAcao = "propor_acao";
    public const string AbrirTela = "abrir_tela";
    public const string RascunhoChamado = "rascunho_chamado";

    public static readonly string[] TiposConsulta = ["loja_status", "horarios_especiais", "produto", "chamados"];
    public static readonly string[] CategoriasChamado = ["parou", "duvida", "pagamento", "ideia"];

    public sealed record PedidoConsulta(string Tipo, string? Busca);
    public sealed record PedidoAcao(string FuncionalidadeId, string Resumo, ParametrosAcao Parametros);
    public sealed record PedidoTela(string FuncionalidadeId);
    public sealed record PedidoChamado(string Categoria, string Titulo, string Texto);

    /// <summary>
    /// As ferramentas como JSON cru da API (name, description, input_schema, strict). Só muda quando muda a lista de ações
    /// <c>ia_faz</c> com executor, então o prefixo (ferramentas + sistema) continua em cache.
    /// </summary>
    public static IReadOnlyList<JsonObject> Definicoes(IReadOnlyList<string> funcionalidadesIaFaz)
    {
        var lista = new List<JsonObject>
        {
            Ferramenta(Consultar,
                "Lê dados da loja logada. loja_status: se está aberta e, se não, por quê e como resolver. horarios_especiais: feriados e " +
                "exceções cadastrados. produto: busca no cardápio por nome (preço, se está pausado). chamados: chamados da loja com a Zippy e a situação.",
                Objeto(new()
                {
                    ["tipo"] = new JsonObject { ["type"] = "string", ["enum"] = Arr(TiposConsulta) },
                    ["busca"] = Nulavel("string", "Texto da busca (nome do produto). Nulo nos outros tipos."),
                })),
            Ferramenta(AbrirTela,
                "Mostra o cartão com os passos e o botão que abre a tela de uma funcionalidade do catálogo. Use para o que é modo tela, " +
                "e para ia_faz quando a pessoa não tem permissão ou prefere fazer sozinha.",
                Objeto(new() { ["funcionalidadeId"] = new JsonObject { ["type"] = "string", ["description"] = "id do item no catálogo" } })),
            Ferramenta(RascunhoChamado,
                "Mostra um rascunho de chamado para a Zippy que a pessoa lê, ajusta e decide se envia. Use quando a funcionalidade não existe " +
                "(categoria ideia), quando algo parou de funcionar (parou) ou para dúvida de pagamento da assinatura (pagamento). Você não envia nada.",
                Objeto(new()
                {
                    ["categoria"] = new JsonObject { ["type"] = "string", ["enum"] = Arr(CategoriasChamado) },
                    ["titulo"] = new JsonObject { ["type"] = "string", ["description"] = "Até 80 caracteres." },
                    ["texto"] = new JsonObject { ["type"] = "string", ["description"] = "O pedido em primeira pessoa, como a loja escreveria. Curto." },
                })),
        };
        if (funcionalidadesIaFaz.Count > 0)
            lista.Insert(1, Ferramenta(ProporAcao,
                "Propõe uma ação simples (modo ia_faz). NÃO executa: grava a proposta e mostra o cartão com antes → depois e o botão Confirmar. " +
                "Só a pessoa executa, clicando. Se voltar erro, explique à pessoa em uma frase.",
                Objeto(new()
                {
                    ["funcionalidadeId"] = new JsonObject { ["type"] = "string", ["enum"] = Arr(funcionalidadesIaFaz) },
                    ["resumo"] = new JsonObject { ["type"] = "string", ["description"] = "O que vai mudar, em poucas palavras." },
                    ["parametros"] = Objeto(new()
                    {
                        ["produto"] = Nulavel("string", "Nome do produto como a pessoa disse."),
                        ["data"] = Nulavel("string", "AAAA-MM-DD."),
                        ["fechado"] = Nulavel("boolean", "Horário especial: true = fechado o dia todo."),
                        ["abreAs"] = Nulavel("string", "HH:mm."),
                        ["fechaAs"] = Nulavel("string", "HH:mm."),
                        ["motivo"] = Nulavel("string", "Ex.: Feriado de Natal."),
                    }),
                })));
        return lista;
    }

    /// <summary>
    /// Ajusta a ferramenta ao que o provedor aceita (cópia; a original não muda). Sem <paramref name="strict"/>: tira <c>strict</c>.
    /// Sem <paramref name="esquemaCompleto"/>: campo anulável (<c>["string","null"]</c>) vira tipo simples e opcional e sai o
    /// <c>additionalProperties</c>. A leitura no chat continua igual (campo ausente = nulo). Público para teste.
    /// </summary>
    public static JsonObject Adaptar(JsonObject ferramenta, bool strict, bool esquemaCompleto)
    {
        var copia = ferramenta.DeepClone().AsObject();
        if (!strict) copia.Remove("strict");
        if (!esquemaCompleto) SimplificarObjeto(copia["input_schema"]!.AsObject());
        return copia;
    }

    /// <summary>Sem strict e com esquema simplificado (o caso mais restrito).</summary>
    public static JsonObject Simplificar(JsonObject ferramenta) => Adaptar(ferramenta, strict: false, esquemaCompleto: false);

    private static void SimplificarObjeto(JsonObject schema)
    {
        if (schema["properties"] is not JsonObject props) return;
        var obrigatorios = schema["required"] is JsonArray req ? req.Select(r => r!.GetValue<string>()).ToList() : [];
        foreach (var (nome, valor) in props.ToList())
        {
            if (valor is not JsonObject p) continue;
            if (p["type"] is JsonArray tipos)
            {
                var simples = tipos.Select(t => t!.GetValue<string>()).First(t => t != "null");
                p["type"] = simples;
                obrigatorios.Remove(nome);
            }
            if (p["type"]?.GetValue<string>() == "object") SimplificarObjeto(p);
        }
        schema["required"] = new JsonArray(obrigatorios.Select(o => (JsonNode?)JsonValue.Create(o)).ToArray());
        schema.Remove("additionalProperties");
    }

    public static PedidoConsulta LerConsulta(IReadOnlyDictionary<string, JsonElement> input)
    {
        var tipo = Txt(input, "tipo");
        if (tipo is null || !TiposConsulta.Contains(tipo)) throw new ArgumentException("tipo de consulta desconhecido");
        return new PedidoConsulta(tipo, Txt(input, "busca"));
    }

    public static PedidoAcao LerAcao(IReadOnlyDictionary<string, JsonElement> input)
    {
        var id = Txt(input, "funcionalidadeId") ?? throw new ArgumentException("funcionalidadeId é obrigatório");
        var p = input.TryGetValue("parametros", out var pe) && pe.ValueKind == JsonValueKind.Object ? pe : default;
        string? T(string nome) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool? B(string nome) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(nome, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        return new PedidoAcao(id, Txt(input, "resumo") ?? "", new ParametrosAcao(T("produto"), T("data"), B("fechado"), T("abreAs"), T("fechaAs"), T("motivo")));
    }

    public static PedidoTela LerTela(IReadOnlyDictionary<string, JsonElement> input) =>
        new(Txt(input, "funcionalidadeId") ?? throw new ArgumentException("funcionalidadeId é obrigatório"));

    public static PedidoChamado LerChamado(IReadOnlyDictionary<string, JsonElement> input)
    {
        var categoria = Txt(input, "categoria");
        if (categoria is null || !CategoriasChamado.Contains(categoria)) categoria = "ideia";
        var titulo = (Txt(input, "titulo") ?? "").Trim();
        var texto = (Txt(input, "texto") ?? "").Trim();
        if (texto.Length == 0) throw new ArgumentException("texto do chamado vazio");
        return new PedidoChamado(categoria, titulo.Length > 80 ? titulo[..80] : titulo, texto.Length > 2000 ? texto[..2000] : texto);
    }

    private static string? Txt(IReadOnlyDictionary<string, JsonElement> d, string k) =>
        d.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonObject Ferramenta(string nome, string descricao, JsonObject schema) => new()
    {
        ["name"] = nome, ["description"] = descricao, ["input_schema"] = schema, ["strict"] = true,
    };

    private static JsonObject Objeto(Dictionary<string, JsonNode> props) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = Arr(props.Keys),
        ["properties"] = new JsonObject(props.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
    };

    private static JsonObject Nulavel(string tipo, string descricao) => new()
    {
        ["type"] = new JsonArray(tipo, "null"), ["description"] = descricao,
    };

    private static JsonArray Arr(IEnumerable<string> itens) => new(itens.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
}
