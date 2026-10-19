using APIBack.DTOs.Configuracoes;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace APIBack.Printing;

public sealed class PrintTemplate
{
 public Guid Id { get; set; } = Guid.NewGuid();
 [StringLength(100), Required] public string Nome { get; set; } = "Caixa";
 public string Tipo { get; set; } = "caixa";
 public string Estilo { get; set; } = "balcao";
 public string[] Blocos { get; set; } = ["loja","cliente","endereco","itens","observacoes","totais","pagamento"];
 public int TamanhoTexto { get; set; } = 12;
 public string Separador { get; set; } = "linha";
 public bool MostrarLogo { get; set; } = true;
 [StringLength(300)] public string Rodape { get; set; } = "Obrigado pela preferência!";
 public bool Arquivado { get; set; }
}
public sealed class PrintPrinter
{
 public Guid Id { get; set; } = Guid.NewGuid();
 public Guid EstacaoId { get; set; }
 [StringLength(100), Required] public string Nome { get; set; } = "Impressora";
 public string Conexao { get; set; } = "navegador";
 [StringLength(250)] public string Endereco { get; set; } = "";
 public int PapelMm { get; set; } = 80;
 public int PaginaCodigo { get; set; } = 860;
 public bool Corta { get; set; } = true;
 public bool AbreGaveta { get; set; }
 public int TamanhoTexto { get; set; } = 12;
 public bool Ativa { get; set; } = true;
 public string UltimoEstado { get; set; } = "desconhecido";
 public DateTimeOffset? UltimoEstadoEm { get; set; }
}
public sealed class PrintRule
{
 public Guid Id { get; set; } = Guid.NewGuid();
 [StringLength(100)] public string Nome { get; set; } = "Caixa ao aceitar";
 public bool Ativa { get; set; } = true;
 public string Gatilho { get; set; } = "aceitar";
 public string[] Origens { get; set; } = ["ifood","cardapio_web","ia_whatsapp","atendente"];
 public string[] Modalidades { get; set; } = ["entrega","retirada","local"];
 public Guid ModeloId { get; set; }
 public Guid? ImpressoraId { get; set; }
 public int Vias { get; set; } = 1;
 public int ExpiraEmSegundos { get; set; } = 300;
 public int Ordem { get; set; }
}
public sealed class PrintConfig
{
 public bool Automatica { get; set; }
 public long Revisao { get; set; }
 public List<PrintTemplate> Modelos { get; set; } = [];
 public List<PrintPrinter> Impressoras { get; set; } = [];
 public List<PrintRule> Regras { get; set; } = [];
}
public sealed class PrintStation
{
 public Guid Id { get; set; }
 public string Nome { get; set; } = "";
 public string Tipo { get; set; } = "navegador";
 public bool Ativa { get; set; }
 public string? Computador { get; set; }
 public string? IpLocal { get; set; }
 public string? VersaoAgente { get; set; }
 public DateTimeOffset? UltimoSinalEm { get; set; }
 public Guid? SessaoId { get; set; }
 public string? Navegador { get; set; }
 public bool Online => Ativa && UltimoSinalEm > DateTimeOffset.UtcNow.AddSeconds(-15);
}
public sealed class PrintJob
{
 public Guid Id { get; set; }
 public int? PedidoId { get; set; }
 public Guid? EstacaoId { get; set; }
 public Guid? ImpressoraId { get; set; }
 public string Estado { get; set; } = "pendente";
 public string? Motivo { get; set; }
 public string Origem { get; set; } = "";
 public string OrigemDisparo { get; set; } = "";
 public string Evento { get; set; } = "";
 [System.Text.Json.Serialization.JsonIgnore] public string? DocumentoJson { get; set; }
 public PrintDocument? Documento => DocumentoJson == null ? null : JsonSerializer.Deserialize<PrintDocument>(DocumentoJson, PrintStore.Json);
 public DateTimeOffset CriadoEm { get; set; }
 public DateTimeOffset? EntregueEm { get; set; }
 public DateTimeOffset? ImpressoEm { get; set; }
 public DateTimeOffset ExpiraEm { get; set; }
 public bool Descartado { get; set; }
}
public sealed record PrintLine(string Nome, decimal Quantidade, decimal? Total, string[] Detalhes);
public sealed record PrintBlock(string Tipo, string? Texto = null, PrintLine[]? Itens = null);
public sealed record PrintDocument(int Versao, int? PedidoId, string Origem, string Numero, string Modalidade,
 DateTimeOffset CriadoEm, bool Reimpressao, PrintTemplate Modelo, PrintPrinter Impressora, PrintBlock[] Blocos)
{ public DateTimeOffset EmitidoEm { get; init; } = DateTimeOffset.UtcNow; }
public sealed record PrintDraft(string Passo, JsonElement Conteudo);
public sealed record StationInput([Required, StringLength(100)] string Nome, string Tipo);
public sealed record PairInput(string Codigo, string Computador);
public sealed record Heartbeat(Guid SessaoId, string? Computador, string? Versao, string? Navegador, PrintDetectedPrinter[]? Impressoras, string? IpLocal=null);
public sealed record PrintDetectedPrinter(string Nome, string Estado, string Conexao="sistema", string? Endereco=null);
public sealed record PrintResult(string Estado, string? Motivo, Guid? SessaoId);
public sealed record AgentIdentity(Guid EstabelecimentoId, Guid Id);
