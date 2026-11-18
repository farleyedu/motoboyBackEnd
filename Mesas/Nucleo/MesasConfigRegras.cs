using System.Text.Json;
using System.Text.Json.Nodes;

namespace APIBack.Mesas.Nucleo;

/// <summary>
/// Padrões de <c>mesas_config.dados</c> (CONTRATO-API §5, iguais ao protótipo) e as regras puras para ler, validar e gravar
/// uma chave. O banco guarda só o que a loja mudou; a leitura devolve padrão + mudanças.
/// </summary>
public static class MesasConfigRegras
{
    public const string ChaveAtivo = "ativo";

    private const string PadroesJson = """
    {
      "aprovacao": {
        "modo": "regras",
        "regras": [
          { "id": "r1", "quando": { "origem": "cliente", "estacaoId": "bar" }, "exige": false, "rotulo": "Bebidas pedidas pelo cliente vão direto ao bar" },
          { "id": "r2", "quando": { "origem": "cliente" }, "exige": true, "rotulo": "Pedido feito pelo cliente no celular passa pelo garçom" },
          { "id": "r3", "quando": { "precoAcima": 120 }, "exige": true, "rotulo": "Item acima de R$ 120 passa pelo garçom" }
        ],
        "escalonarMin": 4,
        "escalonarPara": null
      },
      "pedidoPeloCelular": true,
      "preparo": {
        "cozinha": { "existe": true, "painel": true, "via": "impressora" },
        "bar": { "existe": true, "painel": true, "via": "impressora" }
      },
      "rotas": { "inativas": { "chapa": true, "forno": true }, "nomes": {}, "categorias": {}, "produtos": {}, "etapas": {} },
      "estacoes": { "producao_geral": 10, "cozinha": 18, "bar": 5, "chapa": 14, "forno": 16 },
      "cozinha": { "umPorItem": {} },
      "painelTv": { "modo": "tocavel" },
      "filaCliente": "faixa_tempo",
      "alertas": { "tempoMesaMin": 120, "atrasoMin": 20 },
      "alertaSemPedidoMin": 25,
      "abertura": { "tipoComandaPadrao": "mesa" },
      "taxas": [
        { "id": "tx-servico", "tipo": "servico", "rotulo": "Serviço", "percentual": 10, "valor": null, "porPessoa": false, "ativa": true, "quemLibera": "garcom" },
        { "id": "tx-couvert", "tipo": "couvert", "rotulo": "Couvert artístico", "percentual": null, "valor": 12, "porPessoa": true, "ativa": false, "quemLibera": "gestor" },
        { "id": "tx-rolha", "tipo": "rolha", "rotulo": "Taxa de rolha", "percentual": null, "valor": 40, "porPessoa": false, "ativa": false, "quemLibera": "gestor" },
        { "id": "tx-minima", "tipo": "consumacaoMinima", "rotulo": "Consumação mínima", "percentual": null, "valor": 60, "porPessoa": true, "ativa": false, "quemLibera": "gestor" }
      ],
      "servico": { "distribuicao": { "modo": "igual", "retencaoPct": 20 }, "pontos": {} },
      "gorjeta": {
        "ativa": true, "tipo": "valor", "sugestoes": [5, 10, 15],
        "repasse": { "diretoPct": 100, "fundo": { "funcoes": ["garcom"], "soComPonto": true, "divisao": "igual" } }
      },
      "rodizio": {
        "ativo": false,
        "faixas": [ { "id": "adulto", "rotulo": "Adulto", "preco": 89.9 }, { "id": "crianca", "rotulo": "Criança", "preco": 44.9 } ],
        "inclusos": { "categorias": ["pizzas"], "produtos": [] },
        "horarios": [ { "dias": [5, 6, 0], "inicio": "19:00", "fim": "23:00" } ],
        "tempoLimiteMin": 120,
        "taxaDesperdicio": 0
      },
      "retirada": { "modo": "garcom", "clienteEscolhe": false, "estacoes": {}, "produtos": {}, "pontos": {} },
      "entrega": { "fechaSozinho": true, "minutos": 20 },
      "maquininha": { "provedor": "infinitepay" },
      "fiscal": { "ativo": false },
      "reservas": {
        "ativa": true,
        "horarios": ["18:00", "18:30", "19:00", "19:30", "20:00", "20:30", "21:00", "21:30", "22:00"],
        "duracaoMin": 90, "antecedenciaMin": 30, "toleranciaMin": 15, "noShowMin": 30,
        "escolhaDeLugar": true, "mesasReservaveis": null, "linkAoChegar": true,
        "duracaoPorGrupo": [], "juntarMesas": false, "diasFechados": [], "feriados": [], "limitePorHorario": null,
        "quemAtribui": "casa", "faltasAviso": 3,
        "consentimentoTexto": "Aceito receber mensagens desta reserva pelo WhatsApp.",
        "whatsapp": {
          "confirmacao": { "ligado": false, "texto": "" },
          "lembrete": { "ligado": false, "minutosAntes": 120, "texto": "" },
          "atraso": { "ligado": false, "texto": "" },
          "modeloIds": {}
        }
      },
      "autonomiaGarcom": {
        "limiteCortesia": 0,
        "regras": {
          "itemPessoa": "sozinho", "itemMesa": "sozinho", "itemOutraMesa": "sozinho", "dividirItem": "sozinho", "cadeira": "sozinho",
          "juntarMesas": "sozinho", "trocarMesa": "sozinho", "passarMesa": "sozinho", "editarAntes": "sozinho", "cancelarAntes": "motivo",
          "itemDepois": "gerente", "pratoVoltou": "motivo", "soltarPrincipal": "sozinho", "taxa": "motivo", "desconto": "gerente",
          "cortesia": "gerente", "estorno": "gerente", "receber": "sozinho"
        }
      },
      "remuneracaoGarcom": {
        "padrao": {
          "diaria": { "ativa": false, "valor": 80 },
          "atendimento": { "ativa": false, "por": "mesa", "valor": 5 },
          "comissao": { "ativa": false, "percentual": 3, "soCategorias": [] },
          "servico": { "ativa": true },
          "gorjeta": { "ativa": true },
          "metas": { "ativa": false }
        }
      },
      "metasGarcom": {
        "ativa": true, "ranking": false,
        "metas": [
          { "id": "sobremesas", "rotulo": "Sobremesas vendidas", "alvo": 10, "premio": 20 },
          { "id": "mesas", "rotulo": "Mesas atendidas", "alvo": 12, "premio": 0 }
        ]
      },
      "esgotados": []
    }
    """;

    /// <summary>Valores aceitos em campos de escolha (caminho sem índice de lista).</summary>
    private static readonly Dictionary<string, string[]> Escolhas = new(StringComparer.Ordinal)
    {
        ["aprovacao.modo"] = new[] { "direto", "garcom", "regras" },
        ["preparo.cozinha.via"] = new[] { "impressora", "garcom" },
        ["preparo.bar.via"] = new[] { "impressora", "garcom" },
        ["painelTv.modo"] = new[] { "tocavel", "so_ver" },
        ["filaCliente"] = new[] { "contagem", "faixa", "tempo", "faixa_tempo", "nada" },
        ["abertura.tipoComandaPadrao"] = new[] { "mesa", "pessoa" },
        ["taxas.tipo"] = new[] { "servico", "couvert", "rolha", "consumacaoMinima" },
        ["taxas.quemLibera"] = new[] { "garcom", "gestor" },
        ["servico.distribuicao.modo"] = new[] { "igual", "pontos", "garcomDaMesa" },
        ["gorjeta.tipo"] = new[] { "valor", "percentual" },
        ["gorjeta.repasse.fundo.divisao"] = new[] { "igual", "pontos" },
        ["gorjeta.repasse.fundo.funcoes"] = new[] { "garcom", "cozinha", "bar", "caixa", "gerente_salao" },
        ["retirada.modo"] = new[] { "garcom", "balcao" },
        ["maquininha.provedor"] = new[] { "infinitepay", "mercadopago" },
        ["reservas.quemAtribui"] = new[] { "casa", "cliente" },
        ["remuneracaoGarcom.padrao.atendimento.por"] = new[] { "mesa", "pessoa" },
    };

    private static readonly string[] Autonomia = { "sozinho", "motivo", "gerente" };

    /// <summary>Teto de percentuais (lei da gorjeta: retenção até 33%).</summary>
    private static readonly Dictionary<string, decimal> Tetos = new(StringComparer.Ordinal)
    {
        ["servico.distribuicao.retencaoPct"] = 33,
        ["gorjeta.repasse.diretoPct"] = 100,
        ["taxas.percentual"] = 100,
        ["remuneracaoGarcom.padrao.comissao.percentual"] = 100,
    };

    public static JsonObject Padroes() => (JsonObject)JsonNode.Parse(PadroesJson)!;

    /// <summary>Padrão + o que a loja gravou (objetos mesclam; listas e valores substituem).</summary>
    public static JsonObject Efetiva(JsonObject? gravado)
    {
        var resultado = Padroes();
        if (gravado is not null) Mesclar(resultado, gravado);
        return resultado;
    }

    private static void Mesclar(JsonObject alvo, JsonObject fonte)
    {
        foreach (var (chave, valor) in fonte)
        {
            if (valor is JsonObject sub && alvo[chave] is JsonObject destino) Mesclar(destino, sub);
            else alvo[chave] = valor?.DeepClone();
        }
    }

    public static JsonNode? Ler(JsonObject raiz, string chave)
    {
        JsonNode? atual = raiz;
        foreach (var parte in chave.Split('.'))
        {
            if (atual is not JsonObject obj || !obj.TryGetPropertyValue(parte, out atual)) return null;
        }
        return atual;
    }

    /// <summary>Grava o valor no caminho, criando os objetos intermediários.</summary>
    public static void Gravar(JsonObject raiz, string chave, JsonNode? valor)
    {
        var partes = chave.Split('.');
        var atual = raiz;
        foreach (var parte in partes[..^1])
        {
            if (atual[parte] is not JsonObject proximo)
            {
                proximo = new JsonObject();
                atual[parte] = proximo;
            }
            atual = proximo;
        }
        atual[partes[^1]] = valor?.DeepClone();
    }

    /// <summary>Null quando vale; senão a mensagem curta para a tela.</summary>
    public static string? Validar(string? chave, JsonNode? valor)
    {
        if (string.IsNullOrWhiteSpace(chave) || chave.Length > 120) return "Informe o que mudar.";
        if (chave == ChaveAtivo)
            return valor is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? null : "Escolha ligado ou desligado.";

        var padrao = Padroes();
        var partes = chave.Split('.');
        if (partes.Any(string.IsNullOrWhiteSpace)) return "Configuração desconhecida.";
        // Caminho precisa existir no padrão, a não ser dentro de um mapa aberto ({} no padrão: rotas.nomes, estacoes, servico.pontos...).
        JsonNode? referencia = padrao;
        for (var i = 0; i < partes.Length; i++)
        {
            if (referencia is not JsonObject obj) return "Configuração desconhecida.";
            if (obj.Count == 0 || EhMapaAberto(string.Join('.', partes[..i])))
            {
                return ValidarNo(string.Join('.', partes[..i]) + ".*", null, valor);
            }
            if (!obj.TryGetPropertyValue(partes[i], out referencia)) return "Configuração desconhecida.";
        }
        return ValidarNo(chave, referencia, valor);
    }

    // Mapas por id (estação, categoria, produto, pessoa): aceitam chaves novas.
    private static bool EhMapaAberto(string caminho) => caminho is "estacoes" or "rotas.inativas" or "rotas.nomes" or "rotas.categorias"
        or "rotas.produtos" or "rotas.etapas" or "cozinha.umPorItem" or "servico.pontos" or "retirada.estacoes" or "retirada.produtos"
        or "retirada.pontos" or "reservas.whatsapp.modeloIds";

    private static string? ValidarNo(string caminho, JsonNode? referencia, JsonNode? valor)
    {
        // Padrão null (escalonarPara, mesasReservaveis, limitePorHorario...): aceita vazio, texto, número ou lista.
        if (referencia is null)
        {
            if (valor is JsonObject && !caminho.EndsWith(".*", StringComparison.Ordinal)) return "Valor inválido.";
            return ValidarEscalar(caminho, valor);
        }

        switch (referencia)
        {
            case JsonObject refObj:
                if (valor is not JsonObject obj) return "Valor inválido.";
                if (refObj.Count == 0 || EhMapaAberto(caminho))
                {
                    foreach (var (k, v) in obj)
                    {
                        if (k.Length > 80) return "Valor inválido.";
                        var erro = ValidarNo(caminho + ".*", null, v);
                        if (erro is not null) return erro;
                    }
                    return null;
                }
                foreach (var (k, v) in obj)
                {
                    if (!refObj.TryGetPropertyValue(k, out var r)) return "Valor inválido.";
                    var erro = ValidarNo(caminho + "." + k, r, v);
                    if (erro is not null) return erro;
                }
                return null;

            case JsonArray refLista:
                if (valor is not JsonArray lista) return "Valor inválido.";
                if (lista.Count > 200) return "Lista grande demais.";
                var modelo = refLista.Count > 0 ? refLista[0] : null;
                foreach (var item in lista)
                {
                    var erro = modelo is JsonObject modeloObj && item is JsonObject itemObj
                        ? ValidarItemDeLista(caminho, modeloObj, itemObj)
                        : modelo is null ? ValidarEscalar(caminho, item) : ValidarNo(caminho, modelo, item);
                    if (erro is not null) return erro;
                }
                return null;

            default:
                var tipoRef = referencia.GetValueKind();
                if (valor is null)
                    return "Valor inválido.";
                var tipo = valor.GetValueKind();
                var mesmoTipo = tipoRef == tipo || (tipoRef is JsonValueKind.True or JsonValueKind.False && tipo is JsonValueKind.True or JsonValueKind.False);
                if (!mesmoTipo) return "Valor inválido.";
                return ValidarEscalar(caminho, valor);
        }
    }

    // Item de lista de objetos (taxas, faixas, regras, metas): campos do modelo; campo ausente no modelo é recusado,
    // e campos com padrão null no modelo (percentual/valor das taxas) aceitam número ou vazio.
    private static string? ValidarItemDeLista(string caminho, JsonObject modelo, JsonObject item)
    {
        foreach (var (k, v) in item)
        {
            if (!modelo.TryGetPropertyValue(k, out var r)) return "Valor inválido.";
            var erro = r is null || (r is JsonValue rv && rv.GetValueKind() == JsonValueKind.Number && v is null)
                ? ValidarEscalar(caminho + "." + k, v)
                : r is JsonObject && k == "quando" ? (v is JsonObject ? null : "Valor inválido.") : ValidarNo(caminho + "." + k, r, v);
            if (erro is not null) return erro;
        }
        return null;
    }

    private static string? ValidarEscalar(string caminho, JsonNode? valor)
    {
        if (valor is null) return null;
        if (valor is JsonArray lista)
        {
            if (lista.Count > 200) return "Lista grande demais.";
            foreach (var item in lista) if (ValidarEscalar(caminho, item) is { } erro) return erro;
            return null;
        }
        if (valor is JsonObject) return caminho.EndsWith(".*", StringComparison.Ordinal) ? null : "Valor inválido.";
        var tipo = valor.GetValueKind();
        if (tipo == JsonValueKind.String)
        {
            var texto = valor.GetValue<string>();
            if (texto.Length > 1000) return "Texto longo demais.";
            if (Escolhas.TryGetValue(caminho, out var opcoes) && !opcoes.Contains(texto, StringComparer.Ordinal)) return "Opção inválida.";
            if (caminho.StartsWith("autonomiaGarcom.regras.", StringComparison.Ordinal) && !Autonomia.Contains(texto, StringComparer.Ordinal))
                return "Opção inválida.";
        }
        if (tipo == JsonValueKind.Number)
        {
            // ToJsonString serve para número vindo do JSON e para número criado no código (JsonValue<int>).
            if (!decimal.TryParse(valor.ToJsonString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var numero)) return "Número inválido.";
            if (numero < 0) return "O número não pode ser negativo.";
            if (Tetos.TryGetValue(caminho, out var teto) && numero > teto) return $"O máximo é {teto}.";
        }
        return null;
    }

    public static bool Iguais(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);
}
