using System.Text.Json.Nodes;
using APIBack.Mesas.Planta;
using Xunit;

namespace APIBack.Tests.Unit.Mesas.Planta;

/// <summary>Regras de publicar a planta e de derivar o cadastro de mesas (P05), sem banco.</summary>
public class PlantaRegrasTests
{
    /// <summary>Salão pequeno da Unidade Centro do protótipo: 1 andar, 2 áreas, porta principal e mesas 1 a 3.</summary>
    public static JsonObject Planta(Action<JsonObject>? mexer = null)
    {
        var doc = (JsonObject)JsonNode.Parse("""
            {
              "pisos": [{
                "id": "p-peq", "nome": "Salão", "nivel": 0, "largura": 1200, "altura": 800, "paredeEsp": 20,
                "contorno": [[0,0],[1200,0],[1200,800],[0,800]],
                "setores": [{ "id": "s-peq-salao", "nome": "Salão", "cor": "#3b82f6" }, { "id": "s-peq-bar", "nome": "Bar", "cor": "#0ea5b7" }],
                "elementos": [{ "id": "e1", "tipo": "porta", "x": 1060, "y": 800, "larg": 140, "principal": true }],
                "fundo": { "src": "data:image/png;base64,AAAA" }
              }],
              "mesas": [
                { "id": "m-peq-1", "numero": 1, "pisoId": "p-peq", "setorId": "s-peq-salao", "forma": "quadrada", "x": 160, "y": 330, "cadeiras": [{},{},{},{}] },
                { "id": "m-peq-2", "numero": 2, "pisoId": "p-peq", "setorId": "s-peq-salao", "forma": "quadrada", "x": 370, "y": 330, "capacidade": 4 },
                { "id": "m-peq-3", "numero": "3", "pisoId": "p-peq", "setorId": "s-peq-bar", "forma": "redonda", "x": 600, "y": 120, "capacidade": 2 }
              ]
            }
            """)!;
        mexer?.Invoke(doc);
        return doc;
    }

    private static JsonObject Mesa(JsonObject doc, int i) => (JsonObject)doc["mesas"]![i]!;

    [Fact]
    public void Planta_certa_publica_e_le_andares_areas_e_mesas()
    {
        var (lida, erros) = PlantaRegras.ValidarParaPublicar(Planta());
        Assert.Empty(erros);
        Assert.NotNull(lida);
        Assert.Single(lida!.Andares);
        Assert.Equal(2, lida.Areas.Count);
        Assert.Equal(new[] { "1", "2", "3" }, lida.Mesas.Select(m => m.Numero));
        Assert.Equal(4, lida.Mesas[0].Capacidade); // lugares contados pelas cadeiras
        Assert.True(lida.Mesas[0].Reservavel);
    }

    [Fact]
    public void Erros_vem_por_item_e_nada_e_lido()
    {
        var doc = Planta(d =>
        {
            ((JsonObject)d["pisos"]![0]!)["contorno"] = new JsonArray(new JsonArray(0, 0), new JsonArray(10, 0));
            ((JsonObject)d["pisos"]![0]!)["elementos"] = new JsonArray();
            Mesa(d, 1)["numero"] = 1;
            Mesa(d, 2)["forma"] = "triangulo";
        });
        var (lida, erros) = PlantaRegras.ValidarParaPublicar(doc);
        Assert.Null(lida);
        Assert.Contains(erros, e => e is { Item: "piso", Id: "p-peq", Codigo: "CONTORNO" });
        Assert.Contains(erros, e => e is { Item: "planta", Codigo: "SEM_ENTRADA" });
        Assert.Equal(2, erros.Count(e => e.Codigo == "NUMERO_REPETIDO"));
        Assert.Contains(erros, e => e is { Item: "mesa", Id: "m-peq-3", Codigo: "FORMA" });
    }

    [Fact]
    public void Planta_vazia_pede_andar_e_mesa()
    {
        var (_, erros) = PlantaRegras.ValidarParaPublicar(new JsonObject { ["pisos"] = new JsonArray(), ["mesas"] = new JsonArray() });
        Assert.Contains(erros, e => e.Codigo == "SEM_ANDAR");
        Assert.Contains(erros, e => e.Codigo == "SEM_MESA");
    }

    [Fact]
    public void Mesa_fora_do_contorno_ou_em_andar_inexistente_e_recusada()
    {
        var (_, erros) = PlantaRegras.ValidarParaPublicar(Planta(d =>
        {
            Mesa(d, 0)["x"] = 1500;
            Mesa(d, 1)["pisoId"] = "p-nao-existe";
            Mesa(d, 2)["setorId"] = "s-outro";
        }));
        Assert.Contains(erros, e => e is { Id: "m-peq-1", Codigo: "FORA_DO_CONTORNO" });
        Assert.Contains(erros, e => e is { Id: "m-peq-2", Codigo: "MESA_SEM_ANDAR" });
        Assert.Contains(erros, e => e is { Id: "m-peq-3", Codigo: "MESA_SEM_AREA" });
    }

    [Fact]
    public void Mesa_encostada_na_parede_vale()
    {
        var (_, erros) = PlantaRegras.ValidarParaPublicar(Planta(d => { Mesa(d, 0)["x"] = 0; Mesa(d, 0)["y"] = 400; }));
        Assert.Empty(erros);
    }

    [Fact]
    public void Formato_do_rascunho_so_exige_as_listas()
    {
        Assert.Null(PlantaRegras.ValidarFormato(new JsonObject { ["pisos"] = new JsonArray(), ["mesas"] = new JsonArray() }, 10));
        Assert.NotNull(PlantaRegras.ValidarFormato(new JsonObject { ["pisos"] = new JsonArray() }, 10));
        Assert.NotNull(PlantaRegras.ValidarFormato(null, 0));
        Assert.NotNull(PlantaRegras.ValidarFormato(Planta(), PlantaRegras.TamanhoMaximo + 1));
    }

    [Fact]
    public void Renumerar_mantem_a_mesma_mesa()
    {
        var id = Guid.NewGuid();
        var atuais = new[] { new PlantaMesaAtual(id, "m-peq-3", "3", true, false) };
        var nova = new PlantaMesaDoc("m-peq-3", "13", "p-peq", null, 2, "redonda", true);
        var dif = PlantaRegras.Comparar(atuais, new[] { nova });
        Assert.Empty(dif.Criar);
        Assert.Empty(dif.Desativar);
        var r = Assert.Single(dif.Renumeradas);
        Assert.Equal(id, r.Id);
        Assert.Equal("3", r.NumeroAntes);
    }

    [Fact]
    public void Mesa_que_sai_e_desativada_e_em_uso_trava()
    {
        var livre = new PlantaMesaAtual(Guid.NewGuid(), "a", "1", true, false);
        var emUso = new PlantaMesaAtual(Guid.NewGuid(), "b", "2", true, true);
        var inativa = new PlantaMesaAtual(Guid.NewGuid(), "c", "9", false, false);
        var dif = PlantaRegras.Comparar(new[] { livre, emUso, inativa }, new[] { new PlantaMesaDoc("c", "9", "p", null, 2, "oval", true) });
        Assert.Equal(new[] { livre.Id, emUso.Id }, dif.Desativar.Select(d => d.Id));
        var presa = Assert.Single(dif.Presas);
        Assert.Equal("MESA_EM_USO", presa.Codigo);
        Assert.True(Assert.Single(dif.Atualizar).Reativar);
    }
}
